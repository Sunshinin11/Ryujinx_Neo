using Ryujinx.Common.Logging;
using Ryujinx.Cpu.Nce;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.Horizon.Common;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Services.Ro
{
    // One owner for the NRO aliases, emulator island, physical pages and resource reservation.
    // Used only by Android processes whose actual CPU memory manager is native NCE.
    internal sealed class NroNceMapping
    {
        private const ulong PageSize = 0x1000, GuardSize = 0x4000;
        private const int MaxRetries = 0x200;
        private readonly KProcess _process;
        private readonly IVirtualMemoryManager _cpuMemory;
        private readonly NroInfo _info;
        private readonly List<(ulong Address, ulong Size)> _releasedSources = new();
        private NceCpuCodePatch _patch;
        private KPageList _patchPages;
        private ulong _patchAddress, _address, _remainingSourceSize;
        private bool _bssMapped, _dataMutable, _patched, _reserved;

        internal bool IsReleased => _patchPages == null && !_reserved && !_bssMapped &&
            _remainingSourceSize == 0 && !_patched && _releasedSources.Count == 0;

        internal NroNceMapping(KProcess process, NroInfo info)
        {
            _process = process;
            // IRoInterface holds its ref-counted _ownerMm through service disposal.
            _cpuMemory = process.CpuMemory;
            _info = info;
        }

        internal ResultCode Load(out ulong address)
        {
            address = 0;
            ResultCode result;
            try
            {
                // ParseNro has already authorized the original hash and applied the user's mods.
                _patch = NcePatcher.CreatePatch(_info.Executable.Text);
                result = LoadCore();
            }
            catch (NotSupportedException exception)
            {
                Logger.Warning?.Print(LogClass.Cpu, $"[NEO-NCE-R13] relocation rejected: {exception.Message}");
                result = ResultCode.InsufficientAddressSpace;
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException or InvalidMemoryRegionException)
            {
                Logger.Warning?.Print(LogClass.Cpu, $"[NEO-NCE-R13] publication rejected: {exception.Message}");
                result = ResultCode.InvalidNro;
            }
            catch (OutOfMemoryException)
            {
                result = (ResultCode)KernelResult.OutOfMemory.ErrorCode;
            }
            catch
            {
                Unload();
                throw;
            }

            if (result != ResultCode.Success)
            {
                ResultCode cleanup = Unload();
                if (cleanup != ResultCode.Success)
                {
                    Logger.Warning?.Print(LogClass.Cpu, $"[NEO-NCE-R13] load={result} cleanup={cleanup}; owner retained");
                    return cleanup;
                }
                return result;
            }

            address = _address;
            Logger.Info?.Print(LogClass.Cpu,
                $"[NEO-NCE-R13] nro=0x{address:X} textBytes={_info.Executable.Text.Length} patch=0x{_patchAddress:X} patchBytes={_patch.Size}");
            return ResultCode.Success;
        }

        private ResultCode LoadCore()
        {
            KPageTableBase memory = _process.MemoryManager;
            if ((_info.NroAddress & (PageSize - 1)) != 0 ||
                (_info.BssSize != 0 && (_info.BssAddress & (PageSize - 1)) != 0))
                return ResultCode.InvalidAddress;
            if (!CanBorrowSource(_info.NroAddress, _info.NroSize) ||
                (_info.BssSize != 0 && !CanBorrowSource(_info.BssAddress, _info.BssSize)))
                return (ResultCode)KernelResult.InvalidMemState.ErrorCode;
            ulong span = checked(WindowSize(_patch.Size, _info.NroSize, _info.BssSize) + GuardSize * 2);
            ulong regionSize = memory.AslrRegionEnd - memory.AslrRegionStart;
            if (span > regionSize) return ResultCode.InsufficientAddressSpace;
            ulong positions = (regionSize - span) / PageSize + 1;

            if (_patch.Size != 0)
            {
                if (_process.ResourceLimit != null && !_process.ResourceLimit.Reserve(LimitableResource.Memory, checked((long)_patch.Size), 0))
                    return (ResultCode)KernelResult.ResLimitExceeded.ErrorCode;
                _reserved = true;
            }

            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                ulong windowStart = checked(memory.AslrRegionStart + (ulong)Random.Shared.NextInt64((long)positions) * PageSize);
                ulong candidate = checked(windowStart + GuardSize);
                if (!TryLayout(memory.AslrRegionStart, memory.AslrRegionEnd, candidate,
                    _patch.Size, _info.NroSize, _info.BssSize, out ulong nroAddress, out ulong end))
                    continue;
                if (!memory.CanContain(windowStart, end - windowStart, MemoryState.CodeStatic)) continue;
                KMemoryInfo free = memory.QueryMemory(windowStart);
                if (free.State != MemoryState.Unmapped || free.Address > windowStart ||
                    free.Size < end - free.Address) continue;

                _patchAddress = _patch.Size == 0 ? 0 : candidate;
                _address = nroAddress;
                // Exact two-way imm26 validation, not a conservative whole-image size estimate.
                _patch.ValidateAddresses(_patchAddress, checked(_address + _info.Executable.TextOffset));

                if (_patch.Size != 0)
                {
                    Result patchResult = memory.MapNceCodePatch(_patchAddress, _patch.Size, out KPageList pages);
                    if (patchResult == KernelResult.InvalidMemState) continue;
                    if (patchResult != Result.Success) return (ResultCode)patchResult.ErrorCode;
                    _patchPages = pages;
                }

                Result result = memory.MapProcessCodeMemory(_address, _info.NroAddress, _info.NroSize);
                if (result == Result.Success)
                {
                    _remainingSourceSize = _info.NroSize;
                    if (_info.BssSize != 0)
                    {
                        result = memory.MapProcessCodeMemory(checked(_address + _info.NroSize), _info.BssAddress, _info.BssSize);
                        _bssMapped = result == Result.Success;
                    }
                }
                if (result != Result.Success)
                {
                    ResultCode cleanup = ReleaseAttempt();
                    if (cleanup != ResultCode.Success) return cleanup;
                    if (result == KernelResult.InvalidMemState && CanBorrowSource(_info.NroAddress, _info.NroSize) &&
                        (_info.BssSize == 0 || CanBorrowSource(_info.BssAddress, _info.BssSize))) continue;
                    return (ResultCode)result.ErrorCode;
                }

                // Recheck every guard after mapping; another service may have allocated nearby.
                if (!GuardIsFree(windowStart) || !GuardIsFree(end - GuardSize) ||
                    (_patch.Size != 0 && !GuardIsFree(checked(_patchAddress + _patch.Size))))
                {
                    ResultCode cleanup = ReleaseAttempt();
                    if (cleanup != ResultCode.Success) return cleanup;
                    continue;
                }
                return Publish();
            }
            return ResultCode.InsufficientAddressSpace;
        }

        private bool GuardIsFree(ulong address)
        {
            KMemoryInfo info = _process.MemoryManager.QueryMemory(address);
            return info.State == MemoryState.Unmapped && info.Address <= address &&
                info.Size >= checked(address - info.Address + GuardSize);
        }

        private bool CanBorrowSource(ulong address, ulong size)
        {
            ulong end = checked(address + size);
            if (size == 0 || end <= address) return false;
            while (address < end)
            {
                KMemoryInfo info = _process.MemoryManager.QueryMemory(address);
                if (info.State != MemoryState.Heap || info.Permission != KMemoryPermission.ReadAndWrite ||
                    (info.Attribute & ~MemoryAttribute.IpcAndDeviceMapped) != MemoryAttribute.None)
                    return false;
                ulong next = checked(info.Address + info.Size);
                if (next <= address) return false;
                address = next;
            }
            return true;
        }

        private ResultCode Publish()
        {
            var executable = _info.Executable;
            IVirtualMemoryManager cpu = _cpuMemory;
            ulong text = checked(_address + executable.TextOffset);
            ulong ro = checked(_address + executable.RoOffset);
            ulong data = checked(_address + executable.DataOffset);
            cpu.Write(text, executable.Text);
            cpu.Write(ro, executable.Ro);
            cpu.Write(data, executable.Data);
            if (_info.BssSize != 0) cpu.Fill(checked(_address + _info.NroSize), _info.BssSize, 0);

            // Mark before Write so partially published words are also restored on failure.
            _patched = true;
            _patch.Write(cpu, _patchAddress, text);
            Result result = Permission(text, (ulong)executable.Text.Length, KMemoryPermission.ReadAndExecute);
            if (result != Result.Success) return (ResultCode)result.ErrorCode;
            result = Permission(ro, (ulong)executable.Ro.Length, KMemoryPermission.Read);
            if (result != Result.Success) return (ResultCode)result.ErrorCode;
            ulong writableSize = checked((ulong)executable.Data.Length + _info.BssSize);
            result = Permission(data, writableSize, KMemoryPermission.ReadAndWrite);
            if (result != Result.Success) return (ResultCode)result.ErrorCode;
            _dataMutable = executable.Data.Length != 0;
            result = Permission(_patchAddress, _patch.Size, KMemoryPermission.ReadAndExecute);
            if (result != Result.Success) return (ResultCode)result.ErrorCode;

            // Both executable aliases exist and are RX before maintenance and IPC publication.
            _process.Context.InvalidateCacheRegion(text, (ulong)executable.Text.Length);
            _process.Context.InvalidateCacheRegion(_patchAddress, _patch.Size);
            return ResultCode.Success;
        }

        private Result Permission(ulong address, ulong size, KMemoryPermission permission)
            => size == 0 ? Result.Success : _process.MemoryManager.SetProcessMemoryPermission(address, size, permission);

        internal ResultCode Unload()
        {
            try
            {
                ResultCode result = ReleaseAttempt();
                if (result != ResultCode.Success) return result;
                if (_reserved)
                {
                    _process.ResourceLimit?.Release(LimitableResource.Memory, _patch.Size);
                    _reserved = false;
                }
                return ResultCode.Success;
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException or InvalidMemoryRegionException)
            {
                Logger.Warning?.Print(LogClass.Cpu, $"[NEO-NCE-R13] cleanup rejected: {exception.Message}; owner retained");
                return ResultCode.InvalidNro;
            }
        }

        private ResultCode ReleaseAttempt()
        {
            // A failed retry keeps the reservation; terminal Unload releases it exactly once.
            try
            {
                if (_bssMapped)
                {
                    Result result = _process.MemoryManager.UnmapProcessCodeMemory(
                        checked(_address + _info.NroSize), _info.BssAddress, _info.BssSize);
                    if (result != Result.Success) return (ResultCode)result.ErrorCode;
                    _bssMapped = false;
                    _releasedSources.Add((_info.BssAddress, _info.BssSize));
                }
                if (_dataMutable)
                {
                    ulong offset = _info.Executable.DataOffset;
                    ulong size = _info.NroSize - offset;
                    Result result = _process.MemoryManager.UnmapProcessCodeMemory(
                        checked(_address + offset), checked(_info.NroAddress + offset), size);
                    if (result != Result.Success) return (ResultCode)result.ErrorCode;
                    _dataMutable = false;
                    _remainingSourceSize = offset;
                    _releasedSources.Add((checked(_info.NroAddress + offset), size));
                }
                if (_remainingSourceSize != 0)
                {
                    Result result = _process.MemoryManager.UnmapProcessCodeMemory(_address, _info.NroAddress, _remainingSourceSize);
                    if (result != Result.Success) return (ResultCode)result.ErrorCode;
                    _releasedSources.Add((_info.NroAddress, _remainingSourceSize));
                    _remainingSourceSize = 0;
                }
                if (_patched)
                {
                    _patch.RestoreText(_cpuMemory, checked(_info.NroAddress + _info.Executable.TextOffset),
                        _patchAddress, checked(_address + _info.Executable.TextOffset), _info.Executable.Text);
                    _patched = false;
                }
                if (_patchPages != null)
                {
                    Result result = _process.MemoryManager.UnmapPages(_patchAddress, _patchPages, MemoryState.CodeStatic);
                    if (result != Result.Success) return (ResultCode)result.ErrorCode;
                    _patchPages = null;
                }
                return ResultCode.Success;
            }
            finally
            {
                // Kernel UnmapProcessCodeMemory updates Heap/RW metadata but does not restore
                // native source protections. These sources were RW before the alias was made.
                while (_releasedSources.Count != 0)
                {
                    var source = _releasedSources[0];
                    _cpuMemory.Reprotect(source.Address, source.Size, MemoryPermission.ReadAndWrite);
                    _releasedSources.RemoveAt(0);
                }
            }
        }

        internal static ulong WindowSize(ulong patchSize, ulong nroSize, ulong bssSize)
            => checked(patchSize + (patchSize == 0 ? 0 : GuardSize) + nroSize + bssSize);

        internal static bool TryLayout(ulong regionStart, ulong regionEnd, ulong candidate,
            ulong patchSize, ulong nroSize, ulong bssSize, out ulong nroAddress, out ulong end)
        {
            nroAddress = end = 0;
            if (nroSize == 0 || ((regionStart | regionEnd | candidate | patchSize | nroSize | bssSize) & (PageSize - 1)) != 0)
                return false;
            try
            {
                ulong start = checked(regionStart + GuardSize);
                ulong image = checked(candidate + patchSize + (patchSize == 0 ? 0 : GuardSize));
                ulong finish = checked(candidate + WindowSize(patchSize, nroSize, bssSize) + GuardSize);
                if (candidate < start || finish > regionEnd) return false;
                nroAddress = image;
                end = finish;
                return true;
            }
            catch (OverflowException) { return false; }
        }
    }
}
