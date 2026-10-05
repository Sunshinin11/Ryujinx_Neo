using Ryujinx.Memory;
using System;
using System.Runtime.CompilerServices;

namespace Ryujinx.Cpu.Nce
{
    /// <summary>
    /// Bounded lock-free read index from the host TPIDR identity to an NCE context.
    /// TPIDR_EL0 remains owned by NativeAOT; registration and teardown happen while
    /// guest code is stopped. The original thread table is the collision fallback.
    /// </summary>
    static class NceThreadContextIndex
    {
        public const int BucketBits = 12;
        public const int BucketCount = 1 << BucketBits;
        public const int Ways = 8;
        public const int EntrySize = 16;
        public const int BucketStrideShift = 7;
        public const ulong HashMultiplier = 11400714819323198485UL;

        private struct Entry
        {
            public nint ThreadId;
            public nint NativeContextPtr;
        }

        private static readonly MemoryBlock _block = new((ulong)BucketCount * Ways * EntrySize);

        public static nint EntriesPointer => _block.Pointer;

        public static int Register(nint threadId, nint nativeContextPtr)
        {
            int bucketStart = GetBucket(threadId) * Ways;
            Span<Entry> entries = GetStorage();

            lock (_block)
            {
                for (int way = 0; way < Ways; way++)
                {
                    int index = bucketStart + way;

                    if (entries[index].ThreadId == nint.Zero)
                    {
                        entries[index].NativeContextPtr = nativeContextPtr;
                        entries[index].ThreadId = threadId;
                        return index;
                    }
                }
            }

            return -1;
        }

        public static void Unregister(int index)
        {
            if (index < 0)
            {
                return;
            }

            Span<Entry> entries = GetStorage();

            lock (_block)
            {
                entries[index].ThreadId = nint.Zero;
                entries[index].NativeContextPtr = nint.Zero;
            }
        }

        private static int GetBucket(nint threadId)
        {
            ulong key = unchecked((ulong)(long)threadId);
            return (int)((key * HashMultiplier) >> (64 - BucketBits));
        }

        private static unsafe Span<Entry> GetStorage()
        {
            return new Span<Entry>((void*)_block.Pointer, BucketCount * Ways);
        }
    }
}
