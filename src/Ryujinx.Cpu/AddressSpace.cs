using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Ryujinx.Cpu
{
    public class AddressSpace : IDisposable
    {
        private const MemoryAllocationFlags AsFlags = MemoryAllocationFlags.Reserve | MemoryAllocationFlags.ViewCompatible;

        private readonly MemoryBlock _backingMemory;

        public MemoryBlock Base { get; }
        public MemoryBlock Mirror { get; }

        public ulong AddressSpaceSize { get; }

        public AddressSpace(MemoryBlock backingMemory, MemoryBlock baseMemory, MemoryBlock mirrorMemory, ulong addressSpaceSize)
        {
            _backingMemory = backingMemory;

            Base = baseMemory;
            Mirror = mirrorMemory;
            AddressSpaceSize = addressSpaceSize;
        }

        public static bool TryCreate(MemoryBlock backingMemory, ulong asSize, out AddressSpace addressSpace)
        {
            addressSpace = null;

            MemoryBlock baseMemory = null;
            MemoryBlock mirrorMemory = null;

            try
            {
                baseMemory = new MemoryBlock(asSize, AsFlags);
                mirrorMemory = new MemoryBlock(asSize, AsFlags);
                addressSpace = new AddressSpace(backingMemory, baseMemory, mirrorMemory, asSize);
            }
            catch (SystemException)
            {
                baseMemory?.Dispose();
                mirrorMemory?.Dispose();
            }

            return addressSpace != null;
        }

        public static bool TryCreateWithoutMirror(ulong asSize, out MemoryBlock addressSpace)
        {
            const ulong NceAddressSpaceBase = 0x4000000000UL;
            const ulong NceAddressSpaceSize = 0x4000000000UL;

            MemoryBlock memoryBlock = new(NceAddressSpaceSize, AsFlags);

            if ((ulong)(nuint)memoryBlock.Pointer != NceAddressSpaceBase)
            {
                memoryBlock.Dispose();
                throw new SystemException("NCE probe: kernel did not return the requested low 39-bit address range");
            }

            addressSpace = memoryBlock;
            return true;
        }

        /// <summary>
        /// NEOV2 NCE-LOWBASE1: host addresses tried for the NCE address space, in order.
        /// On NCE the guest address equals the host address, and the program is loaded 0x8000000 bytes and more
        /// above the base. With a base below 0xF0000000 the program code stays below 4 GiB, as on the console
        /// and as with the JIT, instead of above it.
        /// </summary>
        private static readonly ulong[] _nceLowBaseCandidates =
        [
            0x80000000UL,
            0xA0000000UL,
            0xC0000000UL,
            0xE0000000UL,
            0x60000000UL,
            0x40000000UL,
        ];

        /// <summary>
        /// NEOV2 NCE-LOWBASE2: the base must be below this address for the program code to stay below 4 GiB.
        /// </summary>
        private const ulong NceLowBaseLimit = 0xF0000000UL;

        /// <summary>
        /// NEOV2 NCE-LOWBASE2: lowest base considered, the start of the code region on the console.
        /// </summary>
        private const ulong NceLowBaseMinimum = 0x8000000UL;

        /// <summary>
        /// NEOV2 NCE-LOWBASE2: alignment of the base, matching the kernel region alignment.
        /// </summary>
        private const ulong NceBaseAlignment = 0x200000UL;

        /// <summary>
        /// NEOV2 NCE-LOWBASE2: end of the user address space assumed on the host (39-bit).
        /// </summary>
        private const ulong NceHostAddressSpaceEnd = 1UL << 39;

        /// <summary>
        /// NEOV2 NCE-LOWBASE2: maximum number of host mappings written to the log when no low base is free.
        /// </summary>
        private const int MaxLoggedMappings = 64;

        /// <summary>
        /// NEOV2 NCE-LOWBASE2: reads the current host mappings from /proc/self/maps, sorted by address.
        /// </summary>
        /// <returns>The mappings, or null if they could not be read on this host</returns>
        private static List<(ulong Start, ulong End, string Name)> ReadHostMappings()
        {
            List<(ulong Start, ulong End, string Name)> mappings = [];

            try
            {
                foreach (string line in File.ReadLines("/proc/self/maps"))
                {
                    int dash = line.IndexOf('-');
                    int space = line.IndexOf(' ');

                    if (dash <= 0 || space <= dash)
                    {
                        continue;
                    }

                    ulong start = ulong.Parse(line.AsSpan(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    ulong end = ulong.Parse(line.AsSpan(dash + 1, space - dash - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                    string[] fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
                    string name = fields.Length >= 6 ? fields[5].Trim() : string.Empty;

                    mappings.Add((start, end, name.Length != 0 ? name : "[anon]"));
                }
            }
            catch (Exception)
            {
                return null;
            }

            mappings.Sort((x, y) => x.Start.CompareTo(y.Start));

            return mappings;
        }

        /// <summary>
        /// NEOV2 NCE-LOWBASE2: finds the lowest free host range that fits the given size, starting at
        /// <see cref="NceLowBaseMinimum"/>.
        /// </summary>
        /// <param name="mappings">Host mappings sorted by address</param>
        /// <param name="size">Size of the range</param>
        /// <returns>The start of the range, or <see cref="ulong.MaxValue"/> if none fits</returns>
        private static ulong FindLowestFreeBase(List<(ulong Start, ulong End, string Name)> mappings, ulong size)
        {
            ulong cursor = NceLowBaseMinimum;

            foreach ((ulong start, ulong end, _) in mappings)
            {
                if (end <= cursor)
                {
                    continue;
                }

                ulong candidate = AlignUpBase(cursor);

                if (start > candidate && start - candidate >= size)
                {
                    return candidate;
                }

                cursor = Math.Max(cursor, end);
            }

            ulong last = AlignUpBase(cursor);

            return last < NceHostAddressSpaceEnd && NceHostAddressSpaceEnd - last >= size ? last : ulong.MaxValue;
        }

        private static ulong AlignUpBase(ulong address)
        {
            return (address + NceBaseAlignment - 1) & ~(NceBaseAlignment - 1);
        }

        /// <summary>
        /// NEOV2 NCE-LOWBASE2: writes to the log the host mappings that sit where a low base would need to go.
        /// </summary>
        private static void LogLowRangeBlockers(List<(ulong Start, ulong End, string Name)> mappings, ulong size)
        {
            if (mappings == null)
            {
                Logger.Info?.Print(LogClass.Cpu, "[NEOV2-NCE-LOWBASE2] Host mappings could not be read");

                return;
            }

            ulong rangeEnd = NceLowBaseLimit + size;
            int logged = 0;
            int index = 0;

            while (index < mappings.Count && logged < MaxLoggedMappings)
            {
                (ulong start, ulong end, string name) = mappings[index++];

                if (end <= NceLowBaseMinimum || start >= rangeEnd)
                {
                    continue;
                }

                // Merge adjacent mappings of the same object into a single line.
                while (index < mappings.Count && mappings[index].Start == end && mappings[index].Name == name)
                {
                    end = mappings[index++].End;
                }

                Logger.Info?.Print(LogClass.Cpu, $"[NEOV2-NCE-LOWBASE2] Blocking mapping 0x{start:X}-0x{end:X} (0x{end - start:X}) {name}");
                logged++;
            }

            if (logged == 0)
            {
                Logger.Info?.Print(LogClass.Cpu, "[NEOV2-NCE-LOWBASE2] No host mapping found in the low range");
            }
        }

        /// <summary>
        /// NEOV2 NCE-LOWBASE1: same as <see cref="TryCreateWithoutMirror"/>, but for each size it first tries to
        /// place the address space at one of <see cref="_nceLowBaseCandidates"/>. If none of them is free, it uses
        /// the address chosen by the host, exactly as before.
        /// </summary>
        /// <param name="asSize">Requested address space size</param>
        /// <param name="addressSpace">The reserved address space</param>
        /// <param name="lowBase">True if the address space was placed at one of the low candidates</param>
        /// <returns>True if the address space was created, false otherwise</returns>
        public static bool TryCreateWithoutMirrorLowBase(ulong asSize, out MemoryBlock addressSpace, out bool lowBase)
        {
            addressSpace = null;
            lowBase = false;

            ulong minAddressSpaceSize = Math.Min(asSize, 1UL << 36);

            // NEOV2 NCE-LOWBASE2: use the real host memory map to find the lowest free range.
            List<(ulong Start, ulong End, string Name)> mappings = ReadHostMappings();

            for (ulong addressSpaceSize = asSize; addressSpaceSize >= minAddressSpaceSize; addressSpaceSize -= 0x100000000UL)
            {
                if (mappings != null)
                {
                    ulong lowestBase = FindLowestFreeBase(mappings, addressSpaceSize);

                    if (lowestBase < NceLowBaseLimit && MemoryBlock.TryReserveAt(lowestBase, addressSpaceSize, out MemoryBlock mapBlock))
                    {
                        addressSpace = mapBlock;
                        lowBase = true;

                        return true;
                    }
                }

                foreach (ulong candidate in _nceLowBaseCandidates)
                {
                    if (MemoryBlock.TryReserveAt(candidate, addressSpaceSize, out MemoryBlock lowBlock))
                    {
                        addressSpace = lowBlock;
                        lowBase = true;

                        return true;
                    }
                }

                try
                {
                    addressSpace = new MemoryBlock(addressSpaceSize, AsFlags);

                    // NEOV2 NCE-LOWBASE2: no low range fits, record what is in the way.
                    ulong lowestFree = mappings != null ? FindLowestFreeBase(mappings, addressSpaceSize) : ulong.MaxValue;

                    Logger.Info?.Print(LogClass.Cpu, $"[NEOV2-NCE-LOWBASE2] Size 0x{addressSpaceSize:X}: lowest free base in the host map is 0x{lowestFree:X}, a base below 0x{NceLowBaseLimit:X} is needed");

                    LogLowRangeBlockers(mappings, addressSpaceSize);

                    return true;
                }
                catch (SystemException)
                {
                }
            }

            return false;
        }

        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
            Base.MapView(_backingMemory, pa, va, size);
            Mirror.MapView(_backingMemory, pa, va, size);
        }

        public void Unmap(ulong va, ulong size)
        {
            Base.UnmapView(_backingMemory, va, size);
            Mirror.UnmapView(_backingMemory, va, size);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            Base.Dispose();
            Mirror.Dispose();
        }
    }
}
