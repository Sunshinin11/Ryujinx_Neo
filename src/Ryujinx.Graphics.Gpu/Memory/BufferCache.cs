using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Graphics.Gpu.Memory
{
    /// <summary>
    /// Buffer cache.
    /// </summary>
    class BufferCache : IDisposable
    {
        /// <summary>
        /// Initial size for the array holding overlaps.
        /// </summary>
        public const int OverlapsBufferInitialCapacity = 10;

        /// <summary>
        /// Maximum size that an array holding overlaps may have after trimming.
        /// </summary>
        public const int OverlapsBufferMaxCapacity = 10000;

        private const ulong BufferAlignmentSize = 0x1000;
        private const ulong BufferAlignmentMask = BufferAlignmentSize - 1;

        /// <summary>
        /// Alignment required for sparse buffer mappings.
        /// </summary>
        public const ulong SparseBufferAlignmentSize = 0x10000;

        private const ulong MaxDynamicGrowthSize = 0x100000;

        private const int TranslationCacheSize = 2048;
        private const int TranslationCacheMask = TranslationCacheSize - 1;

        private struct TranslationCacheEntry
        {
            public MemoryManager MemoryManager;
            public ulong GpuVa;
            public ulong Size;
            public ulong Address;
            public int Sequence;
        }

        private readonly GpuContext _context;
        private readonly PhysicalMemory _physicalMemory;

        /// <remarks>
        /// Only modified from the GPU thread. Must lock for add/remove.
        /// Must lock for any access from other threads.
        /// </remarks>
        private readonly NonOverlappingRangeList<Buffer> _buffers;
        private readonly MultiRangeList<MultiRangeBuffer> _multiRangeBuffers;

        private readonly Dictionary<ulong, BufferCacheEntry> _dirtyCache;
        private readonly Dictionary<ulong, BufferCacheEntry> _modifiedCache;
        private readonly TranslationCacheEntry[] _translationCache;
        private bool _pruneCaches;
        private int _translationCacheSequence;
        private ulong _translationCacheCalls;
        private ulong _translationCacheHits;
        private int _virtualModifiedSequenceNumber;

        // NEOV2-GPUMEM1: Eden-style buffer garbage collection ("Memoria de GPU estilo Eden").
        private ulong _frameTick;

        public event Action NotifyBuffersModified;

        /// <summary>
        /// Creates a new instance of the buffer manager.
        /// </summary>
        /// <param name="context">The GPU context that the buffer manager belongs to</param>
        /// <param name="physicalMemory">Physical memory where the cached buffers are mapped</param>
        public BufferCache(GpuContext context, PhysicalMemory physicalMemory)
        {
            _context = context;
            _physicalMemory = physicalMemory;

            _buffers = [];
            _multiRangeBuffers = [];

            _dirtyCache = new Dictionary<ulong, BufferCacheEntry>();
            _translationCache = new TranslationCacheEntry[TranslationCacheSize];

            // There are a lot more entries on the modified cache, so it is separate from the one for ForceDirty.
            _modifiedCache = new Dictionary<ulong, BufferCacheEntry>();
        }

        /// <summary>
        /// Handles removal of buffers written to a memory region being unmapped.
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">Event arguments</param>
        public void MemoryUnmappedHandler(object sender, UnmapEventArgs e)
        {
            MultiRange range = ((MemoryManager)sender).GetPhysicalRegions(e.Address, e.Size);

            for (int index = 0; index < range.Count; index++)
            {
                MemoryRange subRange = range.GetSubRange(index);

                ReadOnlySpan<Buffer> overlaps = _buffers.FindOverlapsAsSpan(subRange.Address, subRange.Size);

                for (int i = 0; i < overlaps.Length; i++)
                {
                    overlaps[i].Unmapped(subRange.Address, subRange.Size);
                }
            }
        }

        /// <summary>
        /// Performs address translation of the GPU virtual address, and creates a
        /// new buffer, if needed, for the specified contiguous range.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        /// <returns>Contiguous physical range of the buffer, after address translation</returns>
        public MultiRange TranslateAndCreateBuffer(MemoryManager memoryManager, ulong gpuVa, ulong size, BufferStage stage)
        {
            if (gpuVa == 0)
            {
                return new MultiRange(MemoryManager.PteUnmapped, size);
            }

            int sequence = Volatile.Read(ref _translationCacheSequence);
            int cacheIndex = (int)(((gpuVa >> 8) ^ (gpuVa >> 20)) & TranslationCacheMask);
            ref TranslationCacheEntry entry = ref _translationCache[cacheIndex];

            ulong calls = ++_translationCacheCalls;
            ulong address;

            if (entry.Sequence == sequence &&
                ReferenceEquals(entry.MemoryManager, memoryManager) &&
                entry.GpuVa == gpuVa &&
                entry.Size == size)
            {
                address = entry.Address;
                _translationCacheHits++;

                if ((calls & 0xfffff) == 0)
                {
                    Logger.Info?.Print(LogClass.Gpu, $"[NEOV2-BUFFER-TRANSCACHE1] calls={calls} hits={_translationCacheHits}");
                }
            }
            else
            {
                address = memoryManager.Translate(gpuVa);
                entry.MemoryManager = memoryManager;
                entry.GpuVa = gpuVa;
                entry.Size = size;
                entry.Address = address;
                entry.Sequence = sequence;

                if ((calls & 0xfffff) == 0)
                {
                    Logger.Info?.Print(LogClass.Gpu, $"[NEOV2-BUFFER-TRANSCACHE1] calls={calls} hits={_translationCacheHits}");
                }
            }

            if (address != MemoryManager.PteUnmapped)
            {
                CreateBuffer(address, size, stage);
            }

            return new MultiRange(address, size);
        }

        /// <summary>
        /// Performs address translation of the GPU virtual address, and creates
        /// new physical and virtual buffers, if needed, for the specified range.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        /// <returns>Physical ranges of the buffer, after address translation</returns>
        public MultiRange TranslateAndCreateMultiBuffers(MemoryManager memoryManager, ulong gpuVa, ulong size, BufferStage stage)
        {
            if (gpuVa == 0 || size == 0)
            {
                return new MultiRange(MemoryManager.PteUnmapped, size);
            }

            // Fast path not taken for non-contiguous ranges,
            // since multi-range buffers are not coalesced, so a buffer that covers
            // the entire cached range might not actually exist.
            if (memoryManager.VirtualRangeCache.TryGetOrAddRange(gpuVa, size, out MultiRange range) &&
                range.Count == 1)
            {
                return range;
            }

            CreateBuffer(range, stage);

            return range;
        }

        /// <summary>
        /// Performs address translation of the GPU virtual address, and creates
        /// new physical buffers, if needed, for the specified range.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        /// <returns>Physical ranges of the buffer, after address translation</returns>
        public MultiRange TranslateAndCreateMultiBuffersPhysicalOnly(MemoryManager memoryManager, ulong gpuVa, ulong size, BufferStage stage)
        {
            if (gpuVa == 0)
            {
                return new MultiRange(MemoryManager.PteUnmapped, size);
            }

            // Fast path not taken for non-contiguous ranges,
            // since multi-range buffers are not coalesced, so a buffer that covers
            // the entire cached range might not actually exist.
            if (memoryManager.VirtualRangeCache.TryGetOrAddRange(gpuVa, size, out MultiRange range) &&
                range.Count == 1)
            {
                return range;
            }

            for (int i = 0; i < range.Count; i++)
            {
                MemoryRange subRange = range.GetSubRange(i);

                if (subRange.Address != MemoryManager.PteUnmapped)
                {
                    if (range.Count > 1)
                    {
                        CreateBuffer(subRange.Address, subRange.Size, stage, SparseBufferAlignmentSize);
                    }
                    else
                    {
                        CreateBuffer(subRange.Address, subRange.Size, stage);
                    }
                }
            }

            return range;
        }

        /// <summary>
        /// Creates a new buffer for the specified range, if it does not yet exist.
        /// This can be used to ensure the existance of a buffer.
        /// </summary>
        /// <param name="range">Physical ranges of memory where the buffer data is located</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        public void CreateBuffer(MultiRange range, BufferStage stage)
        {
            if (range.Count > 1)
            {
                CreateMultiRangeBuffer(range, stage);
            }
            else
            {
                MemoryRange subRange = range.GetSubRange(0);

                if (subRange.Address != MemoryManager.PteUnmapped)
                {
                    CreateBuffer(subRange.Address, subRange.Size, stage);
                }
            }
        }

        /// <summary>
        /// Creates a new buffer for the specified range, if it does not yet exist.
        /// This can be used to ensure the existance of a buffer.
        /// </summary>
        /// <param name="address">Address of the buffer in memory</param>
        /// <param name="size">Size of the buffer in bytes</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        public void CreateBuffer(ulong address, ulong size, BufferStage stage)
        {
            ulong endAddress = address + size;

            ulong alignedAddress = address & ~BufferAlignmentMask;
            ulong alignedEndAddress = (endAddress + BufferAlignmentMask) & ~BufferAlignmentMask;

            // The buffer must have the size of at least one page.
            if (alignedEndAddress == alignedAddress)
            {
                alignedEndAddress += BufferAlignmentSize;
            }

            CreateBufferAligned(alignedAddress, alignedEndAddress - alignedAddress, stage);
        }

        /// <summary>
        /// Creates a new buffer for the specified range, if it does not yet exist.
        /// This can be used to ensure the existance of a buffer.
        /// </summary>
        /// <param name="address">Address of the buffer in memory</param>
        /// <param name="size">Size of the buffer in bytes</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        /// <param name="alignment">Alignment of the start address of the buffer in bytes</param>
        public void CreateBuffer(ulong address, ulong size, BufferStage stage, ulong alignment)
        {
            ulong alignmentMask = alignment - 1;
            ulong pageAlignmentMask = BufferAlignmentMask;
            ulong endAddress = address + size;

            ulong alignedAddress = address & ~alignmentMask;
            ulong alignedEndAddress = (endAddress + pageAlignmentMask) & ~pageAlignmentMask;

            // The buffer must have the size of at least one page.
            if (alignedEndAddress == alignedAddress)
            {
                alignedEndAddress += pageAlignmentMask;
            }

            CreateBufferAligned(alignedAddress, alignedEndAddress - alignedAddress, stage, alignment);
        }

        /// <summary>
        /// Creates a buffer for a memory region composed of multiple physical ranges,
        /// if it does not exist yet.
        /// </summary>
        /// <param name="range">Physical ranges of memory</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        private void CreateMultiRangeBuffer(MultiRange range, BufferStage stage)
        {
            // Ensure all non-contiguous buffer we might use are sparse aligned.
            for (int i = 0; i < range.Count; i++)
            {
                MemoryRange subRange = range.GetSubRange(i);

                if (subRange.Address != MemoryManager.PteUnmapped)
                {
                    CreateBuffer(subRange.Address, subRange.Size, stage, SparseBufferAlignmentSize);
                }
            }

            // Create sparse buffer.
            MultiRangeBuffer[] overlaps = new MultiRangeBuffer[10];

            int overlapCount = _multiRangeBuffers.FindOverlaps(range, ref overlaps);

            for (int index = 0; index < overlapCount; index++)
            {
                if (overlaps[index].Range.Contains(range))
                {
                    return;
                }
            }

            for (int index = 0; index < overlapCount; index++)
            {
                if (range.Contains(overlaps[index].Range))
                {
                    _multiRangeBuffers.Remove(overlaps[index]);
                    overlaps[index].Dispose();
                }
            }

            MultiRangeBuffer multiRangeBuffer;

            MemoryRange[] alignedSubRanges = new MemoryRange[range.Count];

            ulong alignmentMask = SparseBufferAlignmentSize - 1;

            if (_context.Capabilities.SupportsSparseBuffer)
            {
                BufferRange[] storages = new BufferRange[range.Count];

                for (int i = 0; i < range.Count; i++)
                {
                    MemoryRange subRange = range.GetSubRange(i);

                    if (subRange.Address != MemoryManager.PteUnmapped)
                    {
                        ulong endAddress = subRange.Address + subRange.Size;

                        ulong alignedAddress = subRange.Address & ~alignmentMask;
                        ulong alignedEndAddress = (endAddress + alignmentMask) & ~alignmentMask;
                        ulong alignedSize = alignedEndAddress - alignedAddress;

                        Buffer buffer = _buffers.FindOverlap(alignedAddress, alignedSize);
                        BufferRange bufferRange = buffer.GetRange(alignedAddress, alignedSize, false);

                        alignedSubRanges[i] = new MemoryRange(alignedAddress, alignedSize);
                        storages[i] = bufferRange;
                    }
                    else
                    {
                        ulong alignedSize = (subRange.Size + alignmentMask) & ~alignmentMask;

                        alignedSubRanges[i] = new MemoryRange(MemoryManager.PteUnmapped, alignedSize);
                        storages[i] = new BufferRange(BufferHandle.Null, 0, (int)alignedSize);
                    }
                }

                multiRangeBuffer = new(_context, new MultiRange(alignedSubRanges), storages);
            }
            else
            {
                for (int i = 0; i < range.Count; i++)
                {
                    MemoryRange subRange = range.GetSubRange(i);

                    if (subRange.Address != MemoryManager.PteUnmapped)
                    {
                        ulong endAddress = subRange.Address + subRange.Size;

                        ulong alignedAddress = subRange.Address & ~alignmentMask;
                        ulong alignedEndAddress = (endAddress + alignmentMask) & ~alignmentMask;
                        ulong alignedSize = alignedEndAddress - alignedAddress;

                        alignedSubRanges[i] = new MemoryRange(alignedAddress, alignedSize);
                    }
                    else
                    {
                        ulong alignedSize = (subRange.Size + alignmentMask) & ~alignmentMask;

                        alignedSubRanges[i] = new MemoryRange(MemoryManager.PteUnmapped, alignedSize);
                    }
                }

                multiRangeBuffer = new(_context, new MultiRange(alignedSubRanges));

                UpdateVirtualBufferDependencies(multiRangeBuffer);
            }

            _multiRangeBuffers.Add(multiRangeBuffer);
        }

        /// <summary>
        /// Adds two-way dependencies to all physical buffers contained within a given virtual buffer.
        /// </summary>
        /// <param name="virtualBuffer">Virtual buffer to have dependencies added</param>
        private void UpdateVirtualBufferDependencies(MultiRangeBuffer virtualBuffer)
        {
            virtualBuffer.ClearPhysicalDependencies();

            ulong dstOffset = 0;

            HashSet<Buffer> physicalBuffers = [];

            for (int i = 0; i < virtualBuffer.Range.Count; i++)
            {
                MemoryRange subRange = virtualBuffer.Range.GetSubRange(i);

                if (subRange.Address != MemoryManager.PteUnmapped)
                {
                    Buffer buffer = _buffers.FindOverlap(subRange.Address, subRange.Size);

                    virtualBuffer.AddPhysicalDependency(buffer, subRange.Address, dstOffset, subRange.Size);
                    physicalBuffers.Add(buffer);
                }

                dstOffset += subRange.Size;
            }

            foreach (Buffer buffer in physicalBuffers)
            {
                buffer.CopyToDependantVirtualBuffer(virtualBuffer);
            }
        }

        /// <summary>
        /// Performs address translation of the GPU virtual address, and attempts to force
        /// the buffer in the region as dirty.
        /// The buffer lookup for this function is cached in a dictionary for quick access, which
        /// accelerates common UBO updates.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        public void ForceDirty(MemoryManager memoryManager, ulong gpuVa, ulong size)
        {
            if (_pruneCaches)
            {
                Prune();
            }

            if (!_dirtyCache.TryGetValue(gpuVa, out BufferCacheEntry result) ||
                result.EndGpuAddress < gpuVa + size ||
                result.UnmappedSequence != result.Buffer.UnmappedSequence)
            {
                MultiRange range = TranslateAndCreateBuffer(memoryManager, gpuVa, size, BufferStage.Internal);
                ulong address = range.GetSubRange(0).Address;
                result = new BufferCacheEntry(address, gpuVa, GetBuffer(address, size, BufferStage.Internal));

                _dirtyCache[gpuVa] = result;
            }

            result.Buffer.LastUseFrame = _frameTick;
            result.Buffer.ForceDirty(result.Address, size);
        }

        /// <summary>
        /// Checks if the given buffer range has been GPU modifed.
        /// </summary>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">Start GPU virtual address of the buffer</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <returns>True if modified, false otherwise</returns>
        public bool CheckModified(MemoryManager memoryManager, ulong gpuVa, ulong size, out ulong outAddr)
        {
            if (_pruneCaches)
            {
                Prune();
            }

            // Align the address to avoid creating too many entries on the quick lookup dictionary.
            ulong mask = BufferAlignmentMask;
            ulong alignedGpuVa = gpuVa & (~mask);
            ulong alignedEndGpuVa = (gpuVa + size + mask) & (~mask);

            size = alignedEndGpuVa - alignedGpuVa;

            if (!_modifiedCache.TryGetValue(alignedGpuVa, out BufferCacheEntry result) ||
                result.EndGpuAddress < alignedEndGpuVa ||
                result.UnmappedSequence != result.Buffer.UnmappedSequence)
            {
                MultiRange range = TranslateAndCreateBuffer(memoryManager, alignedGpuVa, size, BufferStage.None);
                ulong address = range.GetSubRange(0).Address;
                result = new BufferCacheEntry(address, alignedGpuVa, GetBuffer(address, size, BufferStage.None));

                _modifiedCache[alignedGpuVa] = result;
            }

            result.Buffer.LastUseFrame = _frameTick;
            outAddr = result.Address | (gpuVa & mask);

            return result.Buffer.IsModified(result.Address, size);
        }

        /// <summary>
        /// Creates a new buffer for the specified range, if needed.
        /// If a buffer where this range can be fully contained already exists,
        /// then the creation of a new buffer is not necessary.
        /// </summary>
        /// <param name="address">Address of the buffer in guest memory</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        private void CreateBufferAligned(ulong address, ulong size, BufferStage stage)
        {
            ReadOnlySpan<Buffer> overlaps = _buffers.FindOverlapsAsSpan(address, size);

            if (overlaps.Length != 0)
            {
                // The buffer already exists. We can just return the existing buffer
                // if the buffer we need is fully contained inside the overlapping buffer.
                // Otherwise, we must delete the overlapping buffers and create a bigger buffer
                // that fits all the data we need. We also need to copy the contents from the
                // old buffer(s) to the new buffer.

                ulong endAddress = address + size;

                if (overlaps[0].Address > address || overlaps[0].EndAddress < endAddress)
                {
                    bool anySparseCompatible = false;

                    // Check if the following conditions are met:
                    // - We have a single overlap.
                    // - The overlap starts at or before the requested range. That is, the overlap happens at the end.
                    // - The size delta between the new, merged buffer and the old one is of at most 2 pages.
                    // In this case, we attempt to extend the buffer further than the requested range,
                    // this can potentially avoid future resizes if the application keeps using overlapping
                    // sequential memory.
                    // Allowing for 2 pages (rather than just one) is necessary to catch cases where the
                    // range crosses a page, and after alignment, ends having a size of 2 pages.
                    if (overlaps.Length == 1 &&
                        address >= overlaps[0].Address &&
                        endAddress - overlaps[0].EndAddress <= BufferAlignmentSize * 2)
                    {
                        // Try to grow the buffer by 1.5x of its current size.
                        // This improves performance in the cases where the buffer is resized often by small amounts.
                        ulong existingSize = overlaps[0].Size;
                        ulong growthSize = (existingSize + Math.Min(existingSize >> 1, MaxDynamicGrowthSize)) & ~BufferAlignmentMask;

                        size = Math.Max(size, growthSize);
                        endAddress = address + size;

                        overlaps = _buffers.FindOverlapsAsSpan(address, size);
                    }
                    
                    address = Math.Min(address, overlaps[0].Address);
                    endAddress = Math.Max(endAddress, overlaps[^1].EndAddress);
                    
                    for (int i = 0; i < overlaps.Length; i++)
                    {
                        anySparseCompatible |= overlaps[i].SparseCompatible;
                    }

                    Buffer[] overlapsArray = overlaps.ToArray();
                    
                    _buffers.RemoveRange(overlaps[0], overlaps[^1]);
                    
                    ulong newSize = endAddress - address;

                    _buffers.Add(CreateBufferAligned(address, newSize, stage, anySparseCompatible, overlapsArray));
                }
            }
            else
            {
                // No overlap, just create a new buffer.
                Buffer newBuffer = new(_context, _physicalMemory, address, size, stage, sparseCompatible: false, []);
                newBuffer.LastUseFrame = _frameTick;
                _buffers.Add(newBuffer);
            }
        }

        /// <summary>
        /// Creates a new buffer for the specified range, if needed.
        /// If a buffer where this range can be fully contained already exists,
        /// then the creation of a new buffer is not necessary.
        /// </summary>
        /// <param name="address">Address of the buffer in guest memory</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        /// <param name="alignment">Alignment of the start address of the buffer</param>
        private void CreateBufferAligned(ulong address, ulong size, BufferStage stage, ulong alignment)
        {
            bool sparseAligned = alignment >= SparseBufferAlignmentSize;
            
            ReadOnlySpan<Buffer> overlaps = _buffers.FindOverlapsAsSpan(address, size);

            if (overlaps.Length != 0)
            {
                // If the buffer already exists, make sure if covers the entire range,
                // and make sure it is properly aligned, otherwise sparse mapping may fail.

                ulong endAddress = address + size;

                if (overlaps[0].Address > address ||
                    overlaps[0].EndAddress < endAddress ||
                    (overlaps[0].Address & (alignment - 1)) != 0 ||
                    (!overlaps[0].SparseCompatible && sparseAligned))
                {
                    // We need to make sure the new buffer is properly aligned.
                    // However, after the range is aligned, it is possible that it
                    // overlaps more buffers, so try again after each extension
                    // and ensure we cover all overlaps.

                    endAddress = Math.Max(endAddress, overlaps[^1].EndAddress);
                    int oldOverlapCount;
                    
                    do
                    {
                        address = Math.Min(address, overlaps[0].Address);
                        endAddress = Math.Max(endAddress, overlaps[^1].EndAddress);

                        address &= ~(alignment - 1);

                        oldOverlapCount = overlaps.Length;
                        overlaps = _buffers.FindOverlapsAsSpan(address, endAddress - address);
                    }
                    while (oldOverlapCount != overlaps.Length);

                    ulong newSize = endAddress - address;
                    
                    Buffer[] overlapsArray = overlaps.ToArray();
                    
                    _buffers.RemoveRange(overlaps[0], overlaps[^1]);
                    
                    _buffers.Add(CreateBufferAligned(address, newSize, stage, sparseAligned, overlapsArray));
                }
            }
            else
            {
                // No overlap, just create a new buffer.
                Buffer newBuffer = new(_context, _physicalMemory, address, size, stage, sparseAligned, []);
                newBuffer.LastUseFrame = _frameTick;
                _buffers.Add(newBuffer);
            } 
        }

        /// <summary>
        /// Creates a new buffer for the specified range, if needed.
        /// If a buffer where this range can be fully contained already exists,
        /// then the creation of a new buffer is not necessary.
        /// </summary>
        /// <param name="address">Address of the buffer in guest memory</param>
        /// <param name="size">Size in bytes of the buffer</param>
        /// <param name="stage">The type of usage that created the buffer</param>
        /// <param name="sparseCompatible">Indicates if the buffer can be used in a sparse buffer mapping</param>
        /// <param name="overlaps">Buffers overlapping the range</param>
        private Buffer CreateBufferAligned(ulong address, ulong size, BufferStage stage, bool sparseCompatible, Buffer[] overlaps)
        {
            Buffer newBuffer = new(_context, _physicalMemory, address, size, stage, sparseCompatible, overlaps);
            newBuffer.LastUseFrame = _frameTick;

            for (int index = 0; index < overlaps.Length; index++)
            {
                Buffer buffer = overlaps[index];

                int dstOffset = (int)(buffer.Address - newBuffer.Address);

                buffer.CopyTo(newBuffer, dstOffset);
                newBuffer.InheritModifiedRanges(buffer);

                buffer.DecrementReferenceCount();
            }

            newBuffer.SynchronizeMemory(address, size);

            // Existing buffers were modified, we need to rebind everything.
            NotifyBuffersModified?.Invoke();

            RecreateMultiRangeBuffers(address, size);
            
            return newBuffer;
        }

        /// <summary>
        /// Recreates all the multi-range buffers that overlaps a given physical memory range.
        /// </summary>
        /// <param name="address">Start address of the range</param>
        /// <param name="size">Size of the range in bytes</param>
        private void RecreateMultiRangeBuffers(ulong address, ulong size)
        {
            if ((address & (SparseBufferAlignmentSize - 1)) != 0 || (size & (SparseBufferAlignmentSize - 1)) != 0)
            {
                return;
            }

            MultiRangeBuffer[] overlaps = new MultiRangeBuffer[10];

            int overlapCount = _multiRangeBuffers.FindOverlaps(address, size, ref overlaps);

            for (int index = 0; index < overlapCount; index++)
            {
                _multiRangeBuffers.Remove(overlaps[index]);
                overlaps[index].Dispose();
            }

            for (int index = 0; index < overlapCount; index++)
            {
                CreateMultiRangeBuffer(overlaps[index].Range, BufferStage.None);
            }
        }

        /// <summary>
        /// Copy a buffer data from a given address to another.
        /// </summary>
        /// <remarks>
        /// This does a GPU side copy.
        /// </remarks>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="srcVa">GPU virtual address of the copy source</param>
        /// <param name="dstVa">GPU virtual address of the copy destination</param>
        /// <param name="size">Size in bytes of the copy</param>
        private const ulong CpuDmaMinSize = 16UL * 1024 * 1024;
        private const ulong CpuDmaChunkSize = 16UL * 1024 * 1024;

        private bool CanUseCpuDma(MemoryManager memoryManager, ulong gpuVa, ulong size)
        {
            MultiRange range = memoryManager.GetPhysicalRegions(gpuVa, size);

            for (int i = 0; i < range.Count; i++)
            {
                MemoryRange subRange = range.GetSubRange(i);

                if (subRange.Address == MemoryManager.PteUnmapped)
                {
                    return false;
                }

                if (_buffers.FindOverlapsAsSpan(subRange.Address, subRange.Size).Length != 0)
                {
                    return false;
                }
            }

            return true;
        }

        public void CopyBuffer(MemoryManager memoryManager, ulong srcVa, ulong dstVa, ulong size)
        {
            // NEOV2-DMACPU1: Eden's BufferCache::DMACopy only uses the GPU when the memory already has buffers.
            if (!GraphicsConfig.EdenGpuMemory || size < CpuDmaMinSize || (srcVa < dstVa + size && dstVa < srcVa + size))
            {
                CopyBufferGpu(memoryManager, srcVa, dstVa, size);
                return;
            }

            for (ulong offset = 0; offset < size; offset += CpuDmaChunkSize)
            {
                ulong chunk = Math.Min(CpuDmaChunkSize, size - offset);

                if (!CanUseCpuDma(memoryManager, srcVa + offset, chunk) || !CanUseCpuDma(memoryManager, dstVa + offset, chunk))
                {
                    CopyBufferGpu(memoryManager, srcVa + offset, dstVa + offset, chunk);
                    continue;
                }

                // Tracked read: GPU modified textures over the source are flushed to guest memory first.
                ReadOnlySpan<byte> data = memoryManager.GetSpan(srcVa + offset, (int)chunk, tracked: true);
                memoryManager.WriteTrackedResource(dstVa + offset, data);

            }

        }

        private void CopyBufferGpu(MemoryManager memoryManager, ulong srcVa, ulong dstVa, ulong size)
        {
            MultiRange srcRange = TranslateAndCreateMultiBuffersPhysicalOnly(memoryManager, srcVa, size, BufferStage.Copy);
            MultiRange dstRange = TranslateAndCreateMultiBuffersPhysicalOnly(memoryManager, dstVa, size, BufferStage.Copy);

            if (srcRange.Count == 1 && dstRange.Count == 1)
            {
                CopyBufferSingleRange(memoryManager, srcRange.GetSubRange(0).Address, dstRange.GetSubRange(0).Address, size);
            }
            else
            {
                ulong copiedSize = 0;
                ulong srcOffset = 0;
                ulong dstOffset = 0;
                int srcRangeIndex = 0;
                int dstRangeIndex = 0;

                while (copiedSize < size)
                {
                    if (srcRange.GetSubRange(srcRangeIndex).Size == srcOffset)
                    {
                        srcRangeIndex++;
                        srcOffset = 0;
                    }

                    if (dstRange.GetSubRange(dstRangeIndex).Size == dstOffset)
                    {
                        dstRangeIndex++;
                        dstOffset = 0;
                    }

                    MemoryRange srcSubRange = srcRange.GetSubRange(srcRangeIndex);
                    MemoryRange dstSubRange = dstRange.GetSubRange(dstRangeIndex);

                    ulong srcSize = srcSubRange.Size - srcOffset;
                    ulong dstSize = dstSubRange.Size - dstOffset;
                    ulong copySize = Math.Min(srcSize, dstSize);

                    CopyBufferSingleRange(memoryManager, srcSubRange.Address + srcOffset, dstSubRange.Address + dstOffset, copySize);

                    srcOffset += copySize;
                    dstOffset += copySize;
                    copiedSize += copySize;
                }
            }
        }

        /// <summary>
        /// Copy a buffer data from a given address to another.
        /// </summary>
        /// <remarks>
        /// This does a GPU side copy.
        /// </remarks>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="srcAddress">Physical address of the copy source</param>
        /// <param name="dstAddress">Physical address of the copy destination</param>
        /// <param name="size">Size in bytes of the copy</param>
        private void CopyBufferSingleRange(MemoryManager memoryManager, ulong srcAddress, ulong dstAddress, ulong size)
        {
            Buffer srcBuffer = GetBuffer(srcAddress, size, BufferStage.Copy);
            Buffer dstBuffer = GetBuffer(dstAddress, size, BufferStage.Copy);

            int srcOffset = (int)(srcAddress - srcBuffer.Address);
            int dstOffset = (int)(dstAddress - dstBuffer.Address);

            _context.Renderer.Pipeline.CopyBuffer(
                srcBuffer.Handle,
                dstBuffer.Handle,
                srcOffset,
                dstOffset,
                (int)size);

            // NEOV2 DMA-RELAX1: com o readback relaxado, a copia fica so na GPU (como quando a origem ja foi
            // modificada pela GPU), sem a escrita na CPU que obriga a baixar as texturas sobrepostas ao destino.
            if (GraphicsConfig.RelaxedDmaCopy || srcBuffer.IsModified(srcAddress, size))
            {
                dstBuffer.SignalModified(dstAddress, size, BufferStage.Copy);
            }
            else
            {
                // Optimization: If the data being copied is already in memory, then copy it directly instead of flushing from GPU.

                dstBuffer.ClearModified(dstAddress, size);
                memoryManager.Physical.WriteTrackedResource(dstAddress, memoryManager.Physical.GetSpan(srcAddress, (int)size), ResourceKind.Buffer);
            }

            dstBuffer.CopyToDependantVirtualBuffers(dstAddress, size);
        }

        /// <summary>
        /// Clears a buffer at a given address with the specified value.
        /// </summary>
        /// <remarks>
        /// Both the address and size must be aligned to 4 bytes.
        /// </remarks>
        /// <param name="memoryManager">GPU memory manager where the buffer is mapped</param>
        /// <param name="gpuVa">GPU virtual address of the region to clear</param>
        /// <param name="size">Number of bytes to clear</param>
        /// <param name="value">Value to be written into the buffer</param>
        public void ClearBuffer(MemoryManager memoryManager, ulong gpuVa, ulong size, uint value)
        {
            // NEOV2-DMACPU1: Eden's BufferCache::DMAClear only clears on the GPU when the memory already has buffers.
            if (!GraphicsConfig.EdenGpuMemory || size < CpuDmaMinSize)
            {
                ClearBufferGpu(memoryManager, gpuVa, size, value);
                return;
            }

            for (ulong offset = 0; offset < size; offset += CpuDmaChunkSize)
            {
                ulong chunk = Math.Min(CpuDmaChunkSize, size - offset);

                if (!CanUseCpuDma(memoryManager, gpuVa + offset, chunk))
                {
                    ClearBufferGpu(memoryManager, gpuVa + offset, chunk, value);
                    continue;
                }

                MultiRange range = memoryManager.GetPhysicalRegions(gpuVa + offset, chunk);

                for (int index = 0; index < range.Count; index++)
                {
                    MemoryRange subRange = range.GetSubRange(index);

                    // Same guest memory fill the GPU path does after clearing the host buffer; textures over it are signalled.
                    memoryManager.Physical.FillTrackedResource(subRange.Address, subRange.Size, value, ResourceKind.Buffer);
                }

            }

        }

        private void ClearBufferGpu(MemoryManager memoryManager, ulong gpuVa, ulong size, uint value)
        {
            MultiRange range = TranslateAndCreateMultiBuffersPhysicalOnly(memoryManager, gpuVa, size, BufferStage.Copy);

            for (int index = 0; index < range.Count; index++)
            {
                MemoryRange subRange = range.GetSubRange(index);
                Buffer buffer = GetBuffer(subRange.Address, subRange.Size, BufferStage.Copy);

                int offset = (int)(subRange.Address - buffer.Address);

                _context.Renderer.Pipeline.ClearBuffer(buffer.Handle, offset, (int)subRange.Size, value);

                memoryManager.Physical.FillTrackedResource(subRange.Address, subRange.Size, value, ResourceKind.Buffer);

                buffer.CopyToDependantVirtualBuffers(subRange.Address, subRange.Size);
            }
        }

        /// <summary>
        /// Gets a buffer sub-range starting at a given memory address, aligned to the next page boundary.
        /// </summary>
        /// <param name="range">Physical regions of memory where the buffer is mapped</param>
        /// <param name="stage">Buffer stage that triggered the access</param>
        /// <param name="write">Whether the buffer will be written to by this use</param>
        /// <returns>The buffer sub-range starting at the given memory address</returns>
        public BufferRange GetBufferRangeAligned(MultiRange range, BufferStage stage, bool write = false)
        {
            if (range.Count > 1)
            {
                return GetBuffer(range, stage, write).GetRange(range);
            }
            else
            {
                MemoryRange subRange = range.GetSubRange(0);
                return GetBuffer(subRange.Address, subRange.Size, stage, write).GetRangeAligned(subRange.Address, subRange.Size, write);
            }
        }

        /// <summary>
        /// Gets a buffer sub-range for a given memory range.
        /// </summary>
        /// <param name="range">Physical regions of memory where the buffer is mapped</param>
        /// <param name="stage">Buffer stage that triggered the access</param>
        /// <param name="write">Whether the buffer will be written to by this use</param>
        /// <returns>The buffer sub-range for the given range</returns>
        public BufferRange GetBufferRange(MultiRange range, BufferStage stage, bool write = false)
        {
            if (range.Count > 1)
            {
                return GetBuffer(range, stage, write).GetRange(range);
            }
            else
            {
                MemoryRange subRange = range.GetSubRange(0);
                return GetBuffer(subRange.Address, subRange.Size, stage, write).GetRange(subRange.Address, subRange.Size, write);
            }
        }

        /// <summary>
        /// Gets a buffer for a given memory range.
        /// A buffer overlapping with the specified range is assumed to already exist on the cache.
        /// </summary>
        /// <param name="range">Physical regions of memory where the buffer is mapped</param>
        /// <param name="stage">Buffer stage that triggered the access</param>
        /// <param name="write">Whether the buffer will be written to by this use</param>
        /// <returns>The buffer where the range is fully contained</returns>
        private MultiRangeBuffer GetBuffer(MultiRange range, BufferStage stage, bool write = false)
        {
            for (int i = 0; i < range.Count; i++)
            {
                MemoryRange subRange = range.GetSubRange(i);

                Buffer subBuffer = _buffers.FindOverlap(subRange.Address, subRange.Size)
                    ?? CreateMissingBuffer(subRange.Address, subRange.Size, stage, SparseBufferAlignmentSize);

                subBuffer.LastUseFrame = _frameTick;
                subBuffer.SynchronizeMemory(subRange.Address, subRange.Size);

                if (write)
                {
                    subBuffer.SignalModified(subRange.Address, subRange.Size, stage);
                }
            }

            MultiRangeBuffer[] overlaps = new MultiRangeBuffer[10];

            int overlapCount = _multiRangeBuffers.FindOverlaps(range, ref overlaps);

            MultiRangeBuffer buffer = null;

            for (int i = 0; i < overlapCount; i++)
            {
                if (overlaps[i].Range.Contains(range))
                {
                    buffer = overlaps[i];
                    break;
                }
            }

            if (write && buffer != null && !_context.Capabilities.SupportsSparseBuffer)
            {
                buffer.AddModifiedRegion(range, ++_virtualModifiedSequenceNumber);
            }

            return buffer;
        }

        /// <summary>
        /// Gets a buffer for a given memory range.
        /// A buffer overlapping with the specified range is assumed to already exist on the cache.
        /// </summary>
        /// <param name="address">Start address of the memory range</param>
        /// <param name="size">Size in bytes of the memory range</param>
        /// <param name="stage">Buffer stage that triggered the access</param>
        /// <param name="write">Whether the buffer will be written to by this use</param>
        /// <returns>The buffer where the range is fully contained</returns>
        private Buffer GetBuffer(ulong address, ulong size, BufferStage stage, bool write = false)
        {
            Buffer buffer;

            if (size != 0)
            {
                buffer = _buffers.FindOverlap(address, size) ?? CreateMissingBuffer(address, size, stage, 0);

                buffer.LastUseFrame = _frameTick;
                buffer.CopyFromDependantVirtualBuffers();
                buffer.SynchronizeMemory(address, size);

                if (write)
                {
                    buffer.SignalModified(address, size, stage);
                }
            }
            else
            {
                buffer = _buffers.FindOverlapFast(address, 1);
            }

            return buffer;
        }

        /// <summary>
        /// Performs guest to host memory synchronization of a given memory range.
        /// </summary>
        /// <param name="range">Physical regions of memory where the buffer is mapped</param>
        public void SynchronizeBufferRange(MultiRange range)
        {
            if (range.Count == 1)
            {
                MemoryRange subRange = range.GetSubRange(0);
                SynchronizeBufferRange(subRange.Address, subRange.Size, copyBackVirtual: true);
            }
            else
            {
                for (int index = 0; index < range.Count; index++)
                {
                    MemoryRange subRange = range.GetSubRange(index);
                    SynchronizeBufferRange(subRange.Address, subRange.Size, copyBackVirtual: false);
                }
            }
        }

        /// <summary>
        /// Performs guest to host memory synchronization of a given memory range.
        /// </summary>
        /// <param name="address">Start address of the memory range</param>
        /// <param name="size">Size in bytes of the memory range</param>
        /// <param name="copyBackVirtual">Whether virtual buffers that uses this buffer as backing memory should have its data copied back if modified</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SynchronizeBufferRange(ulong address, ulong size, bool copyBackVirtual)
        {
            if (size != 0)
            {
                Buffer buffer = _buffers.FindOverlap(address, size) ?? CreateMissingBuffer(address, size, BufferStage.None, 0);

                buffer.LastUseFrame = _frameTick;

                if (copyBackVirtual)
                {
                    buffer.CopyFromDependantVirtualBuffers();
                }

                buffer.SynchronizeMemory(address, size);
            }
        }

        /// <summary>
        /// Signal that the given buffer's handle has changed,
        /// forcing rebind and any overlapping multi-range buffers to be recreated.
        /// </summary>
        /// <param name="buffer">The buffer that has changed handle</param>
        public void BufferBackingChanged(Buffer buffer)
        {
            NotifyBuffersModified?.Invoke();

            RecreateMultiRangeBuffers(buffer.Address, buffer.Size);
        }

        /// <summary>
        /// Prune any invalid entries from a quick access dictionary.
        /// </summary>
        /// <param name="dictionary">Dictionary to prune</param>
        /// <param name="toDelete">List used to track entries to delete</param>
        private static void Prune(Dictionary<ulong, BufferCacheEntry> dictionary, ref List<ulong> toDelete)
        {
            foreach (KeyValuePair<ulong, BufferCacheEntry> entry in dictionary)
            {
                if (entry.Value.UnmappedSequence != entry.Value.Buffer.UnmappedSequence)
                {
                    (toDelete ??= []).Add(entry.Key);
                }
            }

            if (toDelete != null)
            {
                foreach (ulong entry in toDelete)
                {
                    dictionary.Remove(entry);
                }
            }
        }

        /// <summary>
        /// Prune any invalid entries from the quick access dictionaries.
        /// </summary>
        private void Prune()
        {
            List<ulong> toDelete = null;

            Prune(_dirtyCache, ref toDelete);

            toDelete?.Clear();

            Prune(_modifiedCache, ref toDelete);

            _pruneCaches = false;
        }

        /// <summary>
        /// Queues a prune of invalid entries the next time a dictionary cache is accessed.
        /// </summary>
        public void QueuePrune()
        {
            _pruneCaches = true;
            Interlocked.Increment(ref _translationCacheSequence);
        }

        /// <summary>
        /// NEOV2-GPUMEM1: recreates a buffer that was deleted by the garbage collector while a binding still
        /// points to its range. The data is reloaded from guest memory (GPU modifications are synchronized before collection).
        /// </summary>
        private Buffer CreateMissingBuffer(ulong address, ulong size, BufferStage stage, ulong alignment)
        {
            if (alignment != 0)
            {
                CreateBuffer(address, size, stage, alignment);
            }
            else
            {
                CreateBuffer(address, size, stage);
            }

            return _buffers.FindOverlap(address, size);
        }

        /// <summary>
        /// NEOV2-GPUMEM1: per frame tick. With "Memoria de GPU estilo Eden" enabled, deletes buffers that were not
        /// used recently once the device memory reaches Eden's thresholds (BufferCache::RunGarbageCollector:
        /// 120 frames unused, or 60 frames and twice as many per tick when aggressive).
        /// Modified buffers are written back before deletion, with at most one readback per tick.
        /// </summary>
        public void TickFrame()
        {
            _frameTick++;

            if (!GraphicsConfig.EdenGpuMemory)
            {
                return;
            }

            long total = GpuMemoryStats.TotalBytes;
            int hostPressure = GpuMemoryStats.HostPressureLevel;

            if (total < GpuMemoryStats.ExpectedBytes && hostPressure == 0)
            {
                return;
            }

            bool aggressive = hostPressure != 0 || total >= GpuMemoryStats.CriticalBytes;
            ulong ticksToDestroy = hostPressure >= 2 ? 2UL : (aggressive ? 60UL : 120UL);
            int maxEvictions = hostPressure >= 2 ? 512 : (aggressive ? 64 : 32);

            if (_frameTick <= ticksToDestroy)
            {
                return;
            }

            ulong threshold = _frameTick - ticksToDestroy;

            List<Buffer> candidates = null;

            foreach (Buffer buffer in _buffers)
            {
                if (buffer.LastUseFrame < threshold && buffer.CanBeginEviction)
                {
                    candidates ??= new List<Buffer>();
                    candidates.Add(buffer);
                }
            }

            if (candidates == null)
            {
                return;
            }

            candidates.Sort(static (a, b) => a.LastUseFrame.CompareTo(b.LastUseFrame));

            int evicted = 0;
            int readbacks = 0;

            for (int index = 0; index < candidates.Count && evicted < maxEvictions; index++)
            {
                Buffer buffer = candidates[index];
                bool modified = buffer.HasModifiedData;

                if (modified && readbacks >= 1)
                {
                    continue;
                }

                bool ready = buffer.TryPrepareForEviction();

                if (modified)
                {
                    readbacks++;
                }

                // A partial flush or a newly modified range must never be discarded.
                if (!ready)
                {
                    continue;
                }

                if (_buffers.Remove(buffer))
                {
                    evicted++;
                    buffer.Dispose();
                }
            }

            if (evicted == 0 && readbacks == 0)
            {
                return;
            }

            // Bindings may still reference the deleted host buffers; force them to be resolved again.
            NotifyBuffersModified?.Invoke();
            QueuePrune();
        }

        /// <summary>
        /// Disposes all buffers in the cache.
        /// It's an error to use the buffer cache after disposal.
        /// </summary>
        public void Dispose()
        {
            lock (_buffers)
            {
                foreach (Buffer buffer in _buffers)
                {
                    buffer.Dispose();
                }
            }
        }
    }
}
