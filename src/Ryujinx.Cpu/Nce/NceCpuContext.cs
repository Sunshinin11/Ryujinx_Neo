using ARMeilleure.Memory;
using ARMeilleure.Translation.PTC;
using Ryujinx.Cpu.Signal;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.Nce
{
    class NceCpuContext : ICpuContext
    {
        private static uint[] _getTpidrEl0Code = new uint[]
        {
            GetMrsTpidrEl0(0), // mrs x0, tpidr_el0
            0xd65f03c0u, // ret
        };

        private static readonly uint[] _getCtrEl0Code =
        {
            0xd53b0020u, // mrs x0, ctr_el0
            0xd65f03c0u, // ret
        };

        private static readonly uint[] _cleanDataCacheCode =
        {
            0xeb01001fu, 0x540000a2u, 0xd50b7b20u, 0x8b020000u,
            0xeb01001fu, 0x54ffffa3u, 0xd5033b9fu, 0xd65f03c0u,
        };

        private static readonly uint[] _invalidateInstructionCacheCode =
        {
            0xeb01001fu, 0x540000a2u, 0xd50b7520u, 0x8b020000u,
            0xeb01001fu, 0x54ffffa3u, 0xd5033b9fu, 0xd5033fdfu, 0xd65f03c0u,
        };

        private static uint GetMrsTpidrEl0(uint rd)
        {
            if (OperatingSystem.IsMacOS())
            {
                return 0xd53bd060u | rd; // TPIDRRO
            }
            else
            {
                return 0xd53bd040u | rd; // TPIDR
            }
        }

        readonly struct CodeWriter
        {
            private readonly List<uint> _fullCode;

            public CodeWriter()
            {
                _fullCode = new List<uint>();
            }

            public ulong Write(uint[] code)
            {
                ulong offset = (ulong)_fullCode.Count * sizeof(uint);
                _fullCode.AddRange(code);

                return offset;
            }

            public MemoryBlock CreateMemoryBlock()
            {
                ReadOnlySpan<byte> codeBytes = MemoryMarshal.Cast<uint, byte>(_fullCode.ToArray());

                MemoryBlock codeBlock = new(BitUtils.AlignUp((ulong)codeBytes.Length, 0x1000UL));

                codeBlock.Write(0, codeBytes);
                codeBlock.Reprotect(0, (ulong)codeBytes.Length, MemoryPermission.ReadAndExecute, true);

                return codeBlock;
            }
        }

        private delegate void ThreadStart(nint nativeContextPtr);
        private delegate nint GetTpidrEl0();
        private delegate ulong GetCtrEl0();
        private delegate void CacheRange(ulong start, ulong end, ulong lineSize);
        private static MemoryBlock _codeBlock;
        private static ThreadStart _threadStart;
        private static GetTpidrEl0 _getTpidrEl0;
        private static GetCtrEl0 _getCtrEl0;
        private static CacheRange _cleanDataCache;
        private static CacheRange _invalidateInstructionCache;
        private static ulong _dataCacheLineSize = 64;
        private static ulong _instructionCacheLineSize = 64;

        private readonly ITickSource _tickSource;
        private readonly IMemoryManager _memoryManager;

        static NceCpuContext()
        {
            CodeWriter codeWriter = new();

            uint[] threadStartCode = NcePatcher.GenerateThreadStartCode();
            uint[] ehSuspendCode = NcePatcher.GenerateSuspendExceptionHandler();

            ulong threadStartCodeOffset = codeWriter.Write(threadStartCode);
            ulong getTpidrEl0CodeOffset = codeWriter.Write(_getTpidrEl0Code);
            ulong getCtrEl0CodeOffset = codeWriter.Write(_getCtrEl0Code);
            ulong cleanDataCacheCodeOffset = codeWriter.Write(_cleanDataCacheCode);
            ulong invalidateInstructionCacheCodeOffset = codeWriter.Write(_invalidateInstructionCacheCode);
            ulong ehSuspendCodeOffset = codeWriter.Write(ehSuspendCode);

            MemoryBlock codeBlock = null;
            uint[] ehWrapperCode = null;
            ulong ehWrapperCodeOffset = 0;

            NativeSignalHandler.InstallUnixSignalWrapper((nint oldSignalHandlerSegfaultPtr, nint signalHandlerPtr) =>
            {
                ehWrapperCode = NcePatcher.GenerateWrapperExceptionHandler(
                    oldSignalHandlerSegfaultPtr,
                    signalHandlerPtr);

                ehWrapperCodeOffset = codeWriter.Write(ehWrapperCode);
                codeBlock = codeWriter.CreateMemoryBlock();

                return codeBlock.GetPointer(
                    ehWrapperCodeOffset,
                    (ulong)ehWrapperCode.Length * sizeof(uint));
            });

            nint wrapperPtr = codeBlock.GetPointer(
                ehWrapperCodeOffset,
                (ulong)ehWrapperCode.Length * sizeof(uint));

            nint suspendPtr = codeBlock.GetPointer(
                ehSuspendCodeOffset,
                (ulong)ehSuspendCode.Length * sizeof(uint));

            try
            {
                byte[] wrapperBytes =
                    new byte[ehWrapperCode.Length * sizeof(uint)];

                Buffer.BlockCopy(
                    ehWrapperCode, 0,
                    wrapperBytes, 0,
                    wrapperBytes.Length);

                byte[] suspendBytes =
                    new byte[ehSuspendCode.Length * sizeof(uint)];

                Buffer.BlockCopy(
                    ehSuspendCode, 0,
                    suspendBytes, 0,
                    suspendBytes.Length);

                System.IO.File.WriteAllBytes(
                    "/tmp/ryujinx-nce-wrapper.bin",
                    wrapperBytes);

                System.IO.File.WriteAllBytes(
                    "/tmp/ryujinx-nce-suspend.bin",
                    suspendBytes);

                System.IO.File.WriteAllText(
                    "/tmp/ryujinx-nce-wrapper.txt",
                    $"WRAPPER_PTR=0x{(ulong)wrapperPtr:X16}\n" +
                    $"WRAPPER_SIZE=0x{wrapperBytes.Length:X}\n" +
                    $"WRAPPER_OFFSET=0x{ehWrapperCodeOffset:X}\n" +
                    $"SUSPEND_PTR=0x{(ulong)suspendPtr:X16}\n" +
                    $"SUSPEND_SIZE=0x{suspendBytes.Length:X}\n" +
                    $"SUSPEND_OFFSET=0x{ehSuspendCodeOffset:X}\n" +
                    $"THREAD_START_OFFSET=0x{threadStartCodeOffset:X}\n" +
                    $"TPIDR_OFFSET=0x{getTpidrEl0CodeOffset:X}\n");
            }
            catch
            {
            }

            NativeSignalHandler.InstallUnixSignalHandler(
                NceThreadPal.UnixSuspendSignal,
                suspendPtr);

            _threadStart = Marshal.GetDelegateForFunctionPointer<ThreadStart>(codeBlock.GetPointer(threadStartCodeOffset, (ulong)threadStartCode.Length * sizeof(uint)));
            _getTpidrEl0 = Marshal.GetDelegateForFunctionPointer<GetTpidrEl0>(codeBlock.GetPointer(getTpidrEl0CodeOffset, (ulong)_getTpidrEl0Code.Length * sizeof(uint)));
            _getCtrEl0 = Marshal.GetDelegateForFunctionPointer<GetCtrEl0>(codeBlock.GetPointer(getCtrEl0CodeOffset, (ulong)_getCtrEl0Code.Length * sizeof(uint)));
            _cleanDataCache = Marshal.GetDelegateForFunctionPointer<CacheRange>(codeBlock.GetPointer(cleanDataCacheCodeOffset, (ulong)_cleanDataCacheCode.Length * sizeof(uint)));
            _invalidateInstructionCache = Marshal.GetDelegateForFunctionPointer<CacheRange>(codeBlock.GetPointer(invalidateInstructionCacheCodeOffset, (ulong)_invalidateInstructionCacheCode.Length * sizeof(uint)));

            if (!OperatingSystem.IsMacOS())
            {
                ulong ctrEl0 = _getCtrEl0();
                _dataCacheLineSize = 4UL << (int)((ctrEl0 >> 16) & 0xf);
                _instructionCacheLineSize = 4UL << (int)(ctrEl0 & 0xf);
            }

            _codeBlock = codeBlock;
        }

        public NceCpuContext(ITickSource tickSource, IMemoryManager memory, bool for64Bit)
        {
            _tickSource = tickSource;
            _memoryManager = memory;

            Logger.Info?.Print(
                LogClass.Cpu,
                $"[NEOV2-NCE2-R3] backend active; boundedContextIndex={NcePatcher.UsesThreadContextIndex}; altSignalStack=128KiB; compactFaultSnapshot=True");
        }

        /// <inheritdoc/>
        public IExecutionContext CreateExecutionContext(ExceptionCallbacks exceptionCallbacks)
        {
            return new NceExecutionContext(exceptionCallbacks);
        }

        /// <inheritdoc/>
        public void Execute(IExecutionContext context, ulong address)
        {
            if (!_memoryManager.IsMapped(address))
            {
                throw new InvalidMemoryRegionException($"NCE execution entry is not mapped: 0x{address:X16}");
            }

            NceExecutionContext nec = (NceExecutionContext)context;
            NceNativeInterface.RegisterThread(nec, _tickSource);
            int tableIndex = -1;
            int fastIndex = -1;
            bool started = false;

            try
            {
                nint hostThreadIdentity = _getTpidrEl0();
                tableIndex = NceThreadTable.Register(hostThreadIdentity, nec.NativeContextPtr);

                if (NcePatcher.UsesThreadContextIndex)
                {
                    fastIndex = NceThreadContextIndex.Register(hostThreadIdentity, nec.NativeContextPtr);
                }

                started = true;
                nec.SetStartAddress(address);
                _threadStart(nec.NativeContextPtr);
            }
            finally
            {
                if (started)
                {
                    nec.Exit();
                }

                NceThreadContextIndex.Unregister(fastIndex);

                if (tableIndex >= 0)
                {
                    NceThreadTable.Unregister(tableIndex);
                }

                NceNativeInterface.UnregisterThread();
            }
        }

        /// <inheritdoc/>
        public void InvalidateCacheRegion(ulong address, ulong size)
        {
            if (size == 0 || OperatingSystem.IsMacOS() || address > ulong.MaxValue - size)
            {
                return;
            }

            if (_memoryManager is not MemoryManagerNative memoryManager)
            {
                return;
            }

            IEnumerable<HostMemoryRange> hostRegions = memoryManager.GetHostRegions(address, size);
            if (hostRegions == null)
            {
                return;
            }

            foreach (HostMemoryRange region in hostRegions)
            {
                if (region.Size == 0)
                {
                    continue;
                }

                ulong start = (ulong)region.Address;
                ulong end = start + region.Size;
                _cleanDataCache(AlignDown(start, _dataCacheLineSize), end, _dataCacheLineSize);
            }

            _invalidateInstructionCache(
                AlignDown(address, _instructionCacheLineSize),
                address + size,
                _instructionCacheLineSize);
        }

        private static ulong AlignDown(ulong value, ulong alignment)
        {
            return value & ~(alignment - 1);
        }

        /// <inheritdoc/>
        public IDiskCacheLoadState LoadDiskCache(PtcCacheInfo cacheInfo, bool enabled)
        {
            return new DummyDiskCacheLoadState();
        }

        /// <inheritdoc/>
        public void PrepareCodeRange(ulong address, ulong size)
        {
            // The process factory calls this before NSO images are mapped on NCE.
            // Cache maintenance here would operate on unpublished guest mappings.
        }

        public void Dispose()
        {
        }
    }
}
