using Ryujinx.Common.Logging;
using ARMeilleure.Memory;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using Ryujinx.Memory.Tracking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Ryujinx.Cpu.Nce
{
    /// <summary>
    /// Represents a CPU memory manager which maps guest virtual memory directly onto a host virtual region.
    /// </summary>
    public sealed class MemoryManagerNative : VirtualMemoryManagerRefCountedBase, IMemoryManager, IVirtualMemoryManagerTracked, IWritableBlock
    {
        private readonly MemoryBlock _addressSpace;

        private readonly MemoryBlock _backingMemory;
        private readonly PageTable<ulong> _pageTable;

        private readonly MemoryEhMeilleure _memoryEh;

        private ManagedPageFlags _pages;

        /// <inheritdoc/>
        public bool UsesPrivateAllocations => false;

        public nint PageTablePointer => nint.Zero;

        public ulong ReservedSize => (ulong)_addressSpace.Pointer.ToInt64();

        public MemoryManagerType Type => MemoryManagerType.HostMappedUnsafe;

        public MemoryTracking Tracking { get; }

        public event Action<ulong, ulong> UnmapEvent;

        public int AddressSpaceBits { get; }
        protected override ulong AddressSpaceSize { get; }

        /// <summary>
        /// Creates a new instance of the host mapped memory manager.
        /// </summary>
        /// <param name="addressSpace">Address space memory block</param>
        /// <param name="backingMemory">Physical backing memory where virtual memory will be mapped to</param>
        /// <param name="addressSpaceSize">Size of the address space</param>
        /// <param name="invalidAccessHandler">Optional function to handle invalid memory accesses</param>
        public MemoryManagerNative(
            MemoryBlock addressSpace,
            MemoryBlock backingMemory,
            ulong addressSpaceSize,
            InvalidAccessHandler invalidAccessHandler = null)
        {
            _backingMemory = backingMemory;
            _pageTable = new PageTable<ulong>();
            AddressSpaceSize = addressSpaceSize;

            ulong asSize = PageSize;
            int asBits = PageBits;

            while (asSize < addressSpaceSize)
            {
                asSize <<= 1;
                asBits++;
            }

            AddressSpaceBits = asBits;

            _pages = new ManagedPageFlags(asBits);

            _addressSpace = addressSpace;
            AddressSpaceSize = ReservedSize + addressSpaceSize;

            Tracking = new MemoryTracking(this, PageSize, invalidAccessHandler);
            _memoryEh = new MemoryEhMeilleure(addressSpace, null, Tracking);
            _pages.NceTrackingAddressOffset = ReservedSize;
            _memoryEh.NceTrackingAddressOffset = ReservedSize;
        }

        /// <inheritdoc/>
        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
            va = AddressToOffset(va);
            AssertValidAddressAndSize(va, size);

            _addressSpace.MapView(_backingMemory, pa, AddressToOffset(va), size);
            _pages.AddMapping(va, size);
            PtMap(va, pa, size);

            Tracking.Map(va + ReservedSize, size);
        }

        private void PtMap(ulong va, ulong pa, ulong size)
        {
            while (size != 0)
            {
                _pageTable.Map(va, pa);

                va += PageSize;
                pa += PageSize;
                size -= PageSize;
            }
        }

        /// <inheritdoc/>
        public void Unmap(ulong va, ulong size)
        {
            va = AddressToOffset(va);
            AssertValidAddressAndSize(va, size);

            UnmapEvent?.Invoke(va + ReservedSize, size);
            Tracking.Unmap(va + ReservedSize, size);

            _pages.RemoveMapping(va, size);
            PtUnmap(va, size);
            _addressSpace.UnmapView(_backingMemory, AddressToOffset(va), size);
        }

        private void PtUnmap(ulong va, ulong size)
        {
            while (size != 0)
            {
                _pageTable.Unmap(va);

                va += PageSize;
                size -= PageSize;
            }
        }

        /// <inheritdoc/>
        public void Reprotect(ulong va, ulong size, MemoryPermission protection)
        {
            _addressSpace.Reprotect(AddressToOffset(va), size, protection);

            if (TrackingProtectionRestoreEnabled &&
                Tracking.RestoreGuestProtection(va, size) &&
                !_protectionRestoreLogged)
            {
                _protectionRestoreLogged = true;

                Logger.Info?.Print(
                    LogClass.Cpu,
                    $"[NEOV2-NCE-PROT1] Tracking protection restored after a guest permission change at 0x{va:X} (0x{size:X} bytes)");
            }
        }

        private static bool _protectionRestoreLogged;

        /// <summary>
        /// NCE-PROT1. Disabled by default until enabled by a game profile.
        /// </summary>
        public static bool TrackingProtectionRestoreEnabled { get; set; } = false;

        public ref T GetRef<T>(ulong va) where T : unmanaged
        {
            if (!IsContiguous(va, Unsafe.SizeOf<T>()))
            {
                ThrowMemoryNotContiguous();
            }

            SignalMemoryTracking(va, (ulong)Unsafe.SizeOf<T>(), true);

            return ref _backingMemory.GetRef<T>(GetPhysicalAddressChecked(va));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool IsMapped(ulong va)
        {
            va = AddressToOffset(va);
            return ValidateAddress(va) && _pages.IsMapped(va);
        }

        /// <inheritdoc/>
        public bool IsRangeMapped(ulong va, ulong size)
        {
            va = AddressToOffset(va);
            AssertValidAddressAndSize(va, size);

            return _pages.IsRangeMapped(va, size);
        }

        /// <inheritdoc/>
        public IEnumerable<HostMemoryRange> GetHostRegions(ulong va, ulong size)
        {
            if (size == 0)
            {
                return Enumerable.Empty<HostMemoryRange>();
            }

            List<MemoryRange> guestRegions = GetPhysicalRegionsImpl(va, size);
            if (guestRegions == null)
            {
                return null;
            }

            HostMemoryRange[] regions = new HostMemoryRange[guestRegions.Count];

            for (int i = 0; i < regions.Length; i++)
            {
                MemoryRange guestRegion = guestRegions[i];
                nint pointer = _backingMemory.GetPointer(guestRegion.Address, guestRegion.Size);
                regions[i] = new HostMemoryRange((nuint)(ulong)pointer, guestRegion.Size);
            }

            return regions;
        }

        /// <inheritdoc/>
        public IEnumerable<MemoryRange> GetPhysicalRegions(ulong va, ulong size)
        {
            if (size == 0)
            {
                return Enumerable.Empty<MemoryRange>();
            }

            return GetPhysicalRegionsImpl(va, size);
        }

        private List<MemoryRange> GetPhysicalRegionsImpl(ulong va, ulong size)
        {
            va = AddressToOffset(va);
            if (!ValidateAddress(va) || !ValidateAddressAndSize(va, size))
            {
                return null;
            }

            int pages = GetPagesCount(va, (uint)size, out va);

            List<MemoryRange> regions = new();

            ulong regionStart = GetPhysicalAddressInternal(va);
            ulong regionSize = PageSize;

            for (int page = 0; page < pages - 1; page++)
            {
                if (!ValidateAddress(va + PageSize))
                {
                    return null;
                }

                ulong newPa = GetPhysicalAddressInternal(va + PageSize);

                if (GetPhysicalAddressInternal(va) + PageSize != newPa)
                {
                    regions.Add(new MemoryRange(regionStart, regionSize));
                    regionStart = newPa;
                    regionSize = 0;
                }

                va += PageSize;
                regionSize += PageSize;
            }

            regions.Add(new MemoryRange(regionStart, regionSize));

            return regions;
        }

        private ulong GetPhysicalAddressChecked(ulong va)
        {
            if (!IsMapped(va))
            {
                ThrowInvalidMemoryRegionException($"Not mapped: va=0x{va:X16}");
            }

            return GetPhysicalAddressInternal(va);
        }

        private ulong GetPhysicalAddressInternal(ulong va)
        {
            va = AddressToOffset(va);
            return _pageTable.Read(va) + (va & PageMask);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// This function also validates that the given range is both valid and mapped, and will throw if it is not.
        /// </remarks>
        public override void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null)
        {
            va = AddressToOffset(va);
            AssertValidAddressAndSize(va, size);

            if (precise)
            {
                Tracking.VirtualMemoryEvent(va + ReservedSize, size, write, precise: true, exemptId);
                return;
            }

            _pages.SignalMemoryTracking(Tracking, va, size, write, exemptId);
        }

        /// <inheritdoc/>
        public void TrackingReprotect(ulong va, ulong size, MemoryPermission protection, bool guest)
        {
            va = AddressToOffset(va);
            if (guest)
            {
                _addressSpace.Reprotect(AddressToOffset(va), size, protection, false);
            }
            else
            {
                _pages.TrackingReprotect(va, size, protection);
            }
        }

        /// <inheritdoc/>
        public RegionHandle BeginTracking(ulong address, ulong size, int id, RegionFlags flags)
        {
            address = AddressToOffset(address);
            return Tracking.BeginTracking(address + ReservedSize, size, id, flags);
        }

        /// <inheritdoc/>
        public MultiRegionHandle BeginGranularTracking(ulong address, ulong size, IEnumerable<IRegionHandle> handles, ulong granularity, int id, RegionFlags flags)
        {
            address = AddressToOffset(address);
            return Tracking.BeginGranularTracking(address + ReservedSize, size, handles, granularity, id, flags);
        }

        /// <inheritdoc/>
        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ulong granularity, int id)
        {
            address = AddressToOffset(address);
            return Tracking.BeginSmartGranularTracking(address + ReservedSize, size, granularity, id);
        }

        private ulong AddressToOffset(ulong address)
        {
            ulong reservedSize = ReservedSize;
            ulong logicalAddressSpaceSize = AddressSpaceSize - reservedSize;

            if (address >= reservedSize)
            {
                address -= reservedSize;
            }

            if (address >= logicalAddressSpaceSize)
            {
                throw new ArgumentException("Invalid NCE virtual address");
            }

            return address;
        }

        /// <summary>
        /// Disposes of resources used by the memory manager.
        /// </summary>
        protected override void Destroy()
        {
            _addressSpace.Dispose();
            _memoryEh.Dispose();
        }

        protected override Memory<byte> GetPhysicalAddressMemory(nuint pa, int size)
            => _backingMemory.GetMemory(pa, size);

        protected override Span<byte> GetPhysicalAddressSpan(nuint pa, int size)
            => _backingMemory.GetSpan(pa, size);

        protected override nuint TranslateVirtualAddressChecked(ulong va)
            => (nuint)GetPhysicalAddressChecked(va);

        protected override nuint TranslateVirtualAddressUnchecked(ulong va)
            => (nuint)GetPhysicalAddressInternal(va);
    }
}
