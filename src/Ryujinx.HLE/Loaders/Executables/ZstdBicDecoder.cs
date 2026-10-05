using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Ryujinx.HLE.Loaders.Executables
{
    /// <summary>
    /// NEOV2-ZBIC1: managed Zstandard decoder with support for Nintendo's ZBIC variant
    /// (NsoFlags_UseZbicCompression, SDK 22+). ZBIC is Zstandard with the magic "ZBIC" and the
    /// FSE normalized-count headers stored with binary interpolative coding. Everything else
    /// follows RFC 8878. Regular Zstandard frames are also accepted.
    /// </summary>
    internal static class ZstdBicDecoder
    {
        public const uint ZbicMagic = 0x4349425A;
        public const uint ZstdMagic = 0xFD2FB528;
        private const uint SkippableMagicMask = 0xFFFFFFF0;
        private const uint SkippableMagicStart = 0x184D2A50;

        private const int BlockSizeMax = 1 << 17;

        private const int MaxLL = 35;
        private const int MaxML = 52;
        private const int MaxOff = 31;
        private const int LLFSELog = 9;
        private const int MLFSELog = 9;
        private const int OffFSELog = 8;
        private const int HufTableLogMax = 12;
        private const int HufWeightsFSELog = 6;

        private static readonly short[] _llDefaultNorm =
        {
            4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1,
            2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1,
            -1, -1, -1, -1,
        };

        private static readonly short[] _mlDefaultNorm =
        {
            1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1,
            -1, -1, -1, -1, -1,
        };

        private static readonly short[] _ofDefaultNorm =
        {
            1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1,
        };

        private static readonly int[] _llBase =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
            16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096,
            8192, 16384, 32768, 65536,
        };

        private static readonly int[] _llBits =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12,
            13, 14, 15, 16,
        };

        private static readonly int[] _mlBase =
        {
            3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
            19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
            35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051,
            4099, 8195, 16387, 32771, 65539,
        };

        private static readonly int[] _mlBits =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11,
            12, 13, 14, 15, 16,
        };

        /// <summary>Precomputed (middle, first, last) indices used by the BIC entropy table coding.</summary>
        private static readonly ushort[] _bicTable =
        {
            0x80, 0x0, 0x100, 0xC0, 0x80, 0x100, 0xE0, 0xC0, 0x100, 0xF0, 0xE0, 0x100, 0xF8, 0xF0, 0x100, 0xFC,
            0xF8, 0x100, 0xFE, 0xFC, 0x100, 0xFF, 0xFE, 0x100, 0xFD, 0xFC, 0xFE, 0xFA, 0xF8, 0xFC, 0xFB, 0xFA,
            0xFC, 0xF9, 0xF8, 0xFA, 0xF4, 0xF0, 0xF8, 0xF6, 0xF4, 0xF8, 0xF7, 0xF6, 0xF8, 0xF5, 0xF4, 0xF6,
            0xF2, 0xF0, 0xF4, 0xF3, 0xF2, 0xF4, 0xF1, 0xF0, 0xF2, 0xE8, 0xE0, 0xF0, 0xEC, 0xE8, 0xF0, 0xEE,
            0xEC, 0xF0, 0xEF, 0xEE, 0xF0, 0xED, 0xEC, 0xEE, 0xEA, 0xE8, 0xEC, 0xEB, 0xEA, 0xEC, 0xE9, 0xE8,
            0xEA, 0xE4, 0xE0, 0xE8, 0xE6, 0xE4, 0xE8, 0xE7, 0xE6, 0xE8, 0xE5, 0xE4, 0xE6, 0xE2, 0xE0, 0xE4,
            0xE3, 0xE2, 0xE4, 0xE1, 0xE0, 0xE2, 0xD0, 0xC0, 0xE0, 0xD8, 0xD0, 0xE0, 0xDC, 0xD8, 0xE0, 0xDE,
            0xDC, 0xE0, 0xDF, 0xDE, 0xE0, 0xDD, 0xDC, 0xDE, 0xDA, 0xD8, 0xDC, 0xDB, 0xDA, 0xDC, 0xD9, 0xD8,
            0xDA, 0xD4, 0xD0, 0xD8, 0xD6, 0xD4, 0xD8, 0xD7, 0xD6, 0xD8, 0xD5, 0xD4, 0xD6, 0xD2, 0xD0, 0xD4,
            0xD3, 0xD2, 0xD4, 0xD1, 0xD0, 0xD2, 0xC8, 0xC0, 0xD0, 0xCC, 0xC8, 0xD0, 0xCE, 0xCC, 0xD0, 0xCF,
            0xCE, 0xD0, 0xCD, 0xCC, 0xCE, 0xCA, 0xC8, 0xCC, 0xCB, 0xCA, 0xCC, 0xC9, 0xC8, 0xCA, 0xC4, 0xC0,
            0xC8, 0xC6, 0xC4, 0xC8, 0xC7, 0xC6, 0xC8, 0xC5, 0xC4, 0xC6, 0xC2, 0xC0, 0xC4, 0xC3, 0xC2, 0xC4,
            0xC1, 0xC0, 0xC2, 0xA0, 0x80, 0xC0, 0xB0, 0xA0, 0xC0, 0xB8, 0xB0, 0xC0, 0xBC, 0xB8, 0xC0, 0xBE,
            0xBC, 0xC0, 0xBF, 0xBE, 0xC0, 0xBD, 0xBC, 0xBE, 0xBA, 0xB8, 0xBC, 0xBB, 0xBA, 0xBC, 0xB9, 0xB8,
            0xBA, 0xB4, 0xB0, 0xB8, 0xB6, 0xB4, 0xB8, 0xB7, 0xB6, 0xB8, 0xB5, 0xB4, 0xB6, 0xB2, 0xB0, 0xB4,
            0xB3, 0xB2, 0xB4, 0xB1, 0xB0, 0xB2, 0xA8, 0xA0, 0xB0, 0xAC, 0xA8, 0xB0, 0xAE, 0xAC, 0xB0, 0xAF,
            0xAE, 0xB0, 0xAD, 0xAC, 0xAE, 0xAA, 0xA8, 0xAC, 0xAB, 0xAA, 0xAC, 0xA9, 0xA8, 0xAA, 0xA4, 0xA0,
            0xA8, 0xA6, 0xA4, 0xA8, 0xA7, 0xA6, 0xA8, 0xA5, 0xA4, 0xA6, 0xA2, 0xA0, 0xA4, 0xA3, 0xA2, 0xA4,
            0xA1, 0xA0, 0xA2, 0x90, 0x80, 0xA0, 0x98, 0x90, 0xA0, 0x9C, 0x98, 0xA0, 0x9E, 0x9C, 0xA0, 0x9F,
            0x9E, 0xA0, 0x9D, 0x9C, 0x9E, 0x9A, 0x98, 0x9C, 0x9B, 0x9A, 0x9C, 0x99, 0x98, 0x9A, 0x94, 0x90,
            0x98, 0x96, 0x94, 0x98, 0x97, 0x96, 0x98, 0x95, 0x94, 0x96, 0x92, 0x90, 0x94, 0x93, 0x92, 0x94,
            0x91, 0x90, 0x92, 0x88, 0x80, 0x90, 0x8C, 0x88, 0x90, 0x8E, 0x8C, 0x90, 0x8F, 0x8E, 0x90, 0x8D,
            0x8C, 0x8E, 0x8A, 0x88, 0x8C, 0x8B, 0x8A, 0x8C, 0x89, 0x88, 0x8A, 0x84, 0x80, 0x88, 0x86, 0x84,
            0x88, 0x87, 0x86, 0x88, 0x85, 0x84, 0x86, 0x82, 0x80, 0x84, 0x83, 0x82, 0x84, 0x81, 0x80, 0x82,
            0x40, 0x0, 0x80, 0x60, 0x40, 0x80, 0x70, 0x60, 0x80, 0x78, 0x70, 0x80, 0x7C, 0x78, 0x80, 0x7E,
            0x7C, 0x80, 0x7F, 0x7E, 0x80, 0x7D, 0x7C, 0x7E, 0x7A, 0x78, 0x7C, 0x7B, 0x7A, 0x7C, 0x79, 0x78,
            0x7A, 0x74, 0x70, 0x78, 0x76, 0x74, 0x78, 0x77, 0x76, 0x78, 0x75, 0x74, 0x76, 0x72, 0x70, 0x74,
            0x73, 0x72, 0x74, 0x71, 0x70, 0x72, 0x68, 0x60, 0x70, 0x6C, 0x68, 0x70, 0x6E, 0x6C, 0x70, 0x6F,
            0x6E, 0x70, 0x6D, 0x6C, 0x6E, 0x6A, 0x68, 0x6C, 0x6B, 0x6A, 0x6C, 0x69, 0x68, 0x6A, 0x64, 0x60,
            0x68, 0x66, 0x64, 0x68, 0x67, 0x66, 0x68, 0x65, 0x64, 0x66, 0x62, 0x60, 0x64, 0x63, 0x62, 0x64,
            0x61, 0x60, 0x62, 0x50, 0x40, 0x60, 0x58, 0x50, 0x60, 0x5C, 0x58, 0x60, 0x5E, 0x5C, 0x60, 0x5F,
            0x5E, 0x60, 0x5D, 0x5C, 0x5E, 0x5A, 0x58, 0x5C, 0x5B, 0x5A, 0x5C, 0x59, 0x58, 0x5A, 0x54, 0x50,
            0x58, 0x56, 0x54, 0x58, 0x57, 0x56, 0x58, 0x55, 0x54, 0x56, 0x52, 0x50, 0x54, 0x53, 0x52, 0x54,
            0x51, 0x50, 0x52, 0x48, 0x40, 0x50, 0x4C, 0x48, 0x50, 0x4E, 0x4C, 0x50, 0x4F, 0x4E, 0x50, 0x4D,
            0x4C, 0x4E, 0x4A, 0x48, 0x4C, 0x4B, 0x4A, 0x4C, 0x49, 0x48, 0x4A, 0x44, 0x40, 0x48, 0x46, 0x44,
            0x48, 0x47, 0x46, 0x48, 0x45, 0x44, 0x46, 0x42, 0x40, 0x44, 0x43, 0x42, 0x44, 0x41, 0x40, 0x42,
            0x20, 0x0, 0x40, 0x30, 0x20, 0x40, 0x38, 0x30, 0x40, 0x3C, 0x38, 0x40, 0x3E, 0x3C, 0x40, 0x3F,
            0x3E, 0x40, 0x3D, 0x3C, 0x3E, 0x3A, 0x38, 0x3C, 0x3B, 0x3A, 0x3C, 0x39, 0x38, 0x3A, 0x34, 0x30,
            0x38, 0x36, 0x34, 0x38, 0x37, 0x36, 0x38, 0x35, 0x34, 0x36, 0x32, 0x30, 0x34, 0x33, 0x32, 0x34,
            0x31, 0x30, 0x32, 0x28, 0x20, 0x30, 0x2C, 0x28, 0x30, 0x2E, 0x2C, 0x30, 0x2F, 0x2E, 0x30, 0x2D,
            0x2C, 0x2E, 0x2A, 0x28, 0x2C, 0x2B, 0x2A, 0x2C, 0x29, 0x28, 0x2A, 0x24, 0x20, 0x28, 0x26, 0x24,
            0x28, 0x27, 0x26, 0x28, 0x25, 0x24, 0x26, 0x22, 0x20, 0x24, 0x23, 0x22, 0x24, 0x21, 0x20, 0x22,
            0x10, 0x0, 0x20, 0x18, 0x10, 0x20, 0x1C, 0x18, 0x20, 0x1E, 0x1C, 0x20, 0x1F, 0x1E, 0x20, 0x1D,
            0x1C, 0x1E, 0x1A, 0x18, 0x1C, 0x1B, 0x1A, 0x1C, 0x19, 0x18, 0x1A, 0x14, 0x10, 0x18, 0x16, 0x14,
            0x18, 0x17, 0x16, 0x18, 0x15, 0x14, 0x16, 0x12, 0x10, 0x14, 0x13, 0x12, 0x14, 0x11, 0x10, 0x12,
            0x8, 0x0, 0x10, 0xC, 0x8, 0x10, 0xE, 0xC, 0x10, 0xF, 0xE, 0x10, 0xD, 0xC, 0xE, 0xA,
            0x8, 0xC, 0xB, 0xA, 0xC, 0x9, 0x8, 0xA, 0x4, 0x0, 0x8, 0x6, 0x4, 0x8, 0x7, 0x6,
            0x8, 0x5, 0x4, 0x6, 0x2, 0x0, 0x4, 0x3, 0x2, 0x4, 0x1, 0x0, 0x2, 0x0, 0x0, 0x1,
        };

        private static InvalidDataException Corrupt(string what) => new($"[NEOV2-ZBIC1] Corrupted zstd/zbic data: {what}");

        /// <summary>Returns true when the data starts with a ZBIC or Zstandard frame magic.</summary>
        public static bool IsSupportedFrame(ReadOnlySpan<byte> src)
        {
            if (src.Length < 4)
            {
                return false;
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(src);

            return magic == ZbicMagic || magic == ZstdMagic;
        }

        /// <summary>
        /// Decompresses all frames in <paramref name="src"/> into <paramref name="dst"/>.
        /// Returns the number of bytes written. Throws <see cref="InvalidDataException"/> on corrupted input.
        /// </summary>
        public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int srcPos = 0;
            int dstPos = 0;
            int frames = 0;

            while (srcPos < src.Length)
            {
                if (src.Length - srcPos < 4)
                {
                    throw Corrupt("truncated frame magic");
                }

                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(src[srcPos..]);

                if ((magic & SkippableMagicMask) == SkippableMagicStart)
                {
                    if (src.Length - srcPos < 8)
                    {
                        throw Corrupt("truncated skippable frame");
                    }

                    long skip = 8L + BinaryPrimitives.ReadUInt32LittleEndian(src[(srcPos + 4)..]);

                    if (skip > src.Length - srcPos)
                    {
                        throw Corrupt("truncated skippable frame");
                    }

                    srcPos += (int)skip;
                    continue;
                }

                bool bic;

                if (magic == ZbicMagic)
                {
                    bic = true;
                }
                else if (magic == ZstdMagic)
                {
                    bic = false;
                }
                else
                {
                    throw Corrupt($"unknown frame magic 0x{magic:X8}");
                }

                FrameDecoder frame = new(bic);
                srcPos += 4;
                dstPos += frame.Decode(src, ref srcPos, dst, dstPos);
                frames++;
            }

            if (frames == 0)
            {
                throw Corrupt("no frame");
            }

            return dstPos;
        }

        /// <summary>Reads a backward bitstream (zstd convention: last byte holds the end mark).</summary>
        private ref struct BackwardBitReader
        {
            private readonly ReadOnlySpan<byte> _data;
            private long _bitPos;

            public BackwardBitReader(ReadOnlySpan<byte> data)
            {
                if (data.IsEmpty)
                {
                    throw Corrupt("empty bitstream");
                }

                byte last = data[^1];

                if (last == 0)
                {
                    throw Corrupt("bitstream without end mark");
                }

                _data = data;
                _bitPos = (long)(data.Length - 1) * 8 + BitOperations.Log2(last);
            }

            public readonly long BitPos => _bitPos;

            public readonly bool Overflowed => _bitPos < 0;

            public uint ReadBits(int count)
            {
                if (count == 0)
                {
                    return 0;
                }

                _bitPos -= count;

                return Extract(_bitPos, count);
            }

            public readonly uint PeekBits(int count)
            {
                if (count == 0)
                {
                    return 0;
                }

                return Extract(_bitPos - count, count);
            }

            public void Consume(int count)
            {
                _bitPos -= count;
            }

            private readonly uint Extract(long low, int count)
            {
                int shift = 0;

                if (low < 0)
                {
                    if (-low >= count)
                    {
                        return 0;
                    }

                    shift = (int)-low;
                    count -= shift;
                    low = 0;
                }

                int byteIndex = (int)(low >> 3);
                ulong value;

                if (byteIndex + 8 <= _data.Length)
                {
                    value = BinaryPrimitives.ReadUInt64LittleEndian(_data[byteIndex..]);
                }
                else
                {
                    value = 0;

                    for (int i = _data.Length - 1; i >= byteIndex; i--)
                    {
                        value = (value << 8) | _data[i];
                    }
                }

                value >>= (int)(low & 7);

                return (uint)(value & ((1UL << count) - 1)) << shift;
            }
        }

        private sealed class FseTable
        {
            public int TableLog;
            public readonly byte[] Symbol;
            public readonly byte[] NbBits;
            public readonly ushort[] NewState;

            public FseTable(int capacity)
            {
                Symbol = new byte[capacity];
                NbBits = new byte[capacity];
                NewState = new ushort[capacity];
            }

            public void BuildRle(byte symbol)
            {
                TableLog = 0;
                Symbol[0] = symbol;
                NbBits[0] = 0;
                NewState[0] = 0;
            }

            public void Build(short[] norm, int maxSymbolValue, int tableLog)
            {
                int tableSize = 1 << tableLog;

                if (tableSize > Symbol.Length)
                {
                    throw Corrupt("FSE table too large");
                }

                int highThreshold = tableSize - 1;
                Span<int> symbolNext = stackalloc int[256];

                for (int s = 0; s <= maxSymbolValue; s++)
                {
                    if (norm[s] == -1)
                    {
                        if (highThreshold < 0)
                        {
                            throw Corrupt("FSE low probability overflow");
                        }

                        Symbol[highThreshold--] = (byte)s;
                        symbolNext[s] = 1;
                    }
                    else if (norm[s] < -1)
                    {
                        throw Corrupt("FSE invalid normalized count");
                    }
                    else
                    {
                        symbolNext[s] = norm[s];
                    }
                }

                int step = (tableSize >> 1) + (tableSize >> 3) + 3;
                int mask = tableSize - 1;
                int position = 0;

                for (int s = 0; s <= maxSymbolValue; s++)
                {
                    for (int i = 0; i < norm[s]; i++)
                    {
                        Symbol[position] = (byte)s;

                        do
                        {
                            position = (position + step) & mask;
                        }
                        while (position > highThreshold);
                    }
                }

                if (position != 0)
                {
                    throw Corrupt("FSE normalized counts do not fill the table");
                }

                for (int u = 0; u < tableSize; u++)
                {
                    int s = Symbol[u];
                    int nextState = symbolNext[s]++;

                    if (nextState <= 0)
                    {
                        throw Corrupt("FSE invalid state");
                    }

                    int nbBits = tableLog - BitOperations.Log2((uint)nextState);

                    NbBits[u] = (byte)nbBits;
                    NewState[u] = (ushort)((nextState << nbBits) - tableSize);
                }

                TableLog = tableLog;
            }

            public int Update(int state, ref BackwardBitReader reader)
            {
                return NewState[state] + (int)reader.ReadBits(NbBits[state]);
            }
        }

        private sealed class HufTable
        {
            public int TableLog;
            public bool Valid;
            public readonly byte[] Symbol = new byte[1 << HufTableLogMax];
            public readonly byte[] NbBits = new byte[1 << HufTableLogMax];
        }

        private static uint PeekForward(ReadOnlySpan<byte> src, long bitOffset)
        {
            int byteIndex = (int)(bitOffset >> 3);
            ulong value = 0;

            for (int i = 7; i >= 0; i--)
            {
                int index = byteIndex + i;
                value = (value << 8) | (index < src.Length ? src[index] : (byte)0);
            }

            return (uint)(value >> (int)(bitOffset & 7));
        }

        /// <summary>Standard zstd FSE normalized-count header (RFC 8878 4.1.1).</summary>
        private static int ReadNCountNormal(ReadOnlySpan<byte> src, short[] norm, ref int maxSymbolValue, out int tableLog)
        {
            if (src.IsEmpty)
            {
                throw Corrupt("empty FSE header");
            }

            long bitOffset = 0;
            tableLog = (int)(PeekForward(src, bitOffset) & 0xF) + 5;
            bitOffset += 4;

            if (tableLog > 15)
            {
                throw Corrupt("FSE table log too large");
            }

            int remaining = (1 << tableLog) + 1;
            int threshold = 1 << tableLog;
            int nbBits = tableLog + 1;
            int charNum = 0;
            bool previous0 = false;

            while (remaining > 1 && charNum <= maxSymbolValue)
            {
                if (previous0)
                {
                    int n0 = charNum;

                    while (true)
                    {
                        int repeat = (int)(PeekForward(src, bitOffset) & 3);
                        bitOffset += 2;
                        n0 += repeat;

                        if (repeat != 3)
                        {
                            break;
                        }

                        if (n0 > maxSymbolValue)
                        {
                            throw Corrupt("FSE zero run too long");
                        }
                    }

                    if (n0 > maxSymbolValue)
                    {
                        throw Corrupt("FSE zero run too long");
                    }

                    while (charNum < n0)
                    {
                        norm[charNum++] = 0;
                    }
                }

                uint bits = PeekForward(src, bitOffset);
                int max = (2 * threshold - 1) - remaining;
                int count;

                if ((bits & (uint)(threshold - 1)) < (uint)max)
                {
                    count = (int)(bits & (uint)(threshold - 1));
                    bitOffset += nbBits - 1;
                }
                else
                {
                    count = (int)(bits & (uint)(2 * threshold - 1));

                    if (count >= threshold)
                    {
                        count -= max;
                    }

                    bitOffset += nbBits;
                }

                count--;
                remaining -= count < 0 ? -count : count;
                norm[charNum++] = (short)count;
                previous0 = count == 0;

                if (remaining < 1)
                {
                    throw Corrupt("FSE normalized counts exceed table");
                }

                while (remaining < threshold)
                {
                    nbBits--;
                    threshold >>= 1;
                }
            }

            if (remaining != 1)
            {
                throw Corrupt("FSE normalized counts incomplete");
            }

            int size = (int)((bitOffset + 7) >> 3);

            if (size > src.Length)
            {
                throw Corrupt("FSE header truncated");
            }

            maxSymbolValue = charNum - 1;

            return size;
        }

        /// <summary>ZBIC FSE normalized-count header (binary interpolative coding), ported from Atmosphere's loader.</summary>
        private static int ReadNCountBic(ReadOnlySpan<byte> src, short[] norm, ref int maxSymbolValue, out int tableLog)
        {
            int hbSize = src.Length;

            if (hbSize == 0)
            {
                throw Corrupt("empty BIC header");
            }

            uint first = src[0];
            int raw = (int)(first & 0x7F);
            int useLowProbCount = (int)(first >> 7);

            if (raw >= hbSize)
            {
                throw Corrupt("BIC header size");
            }

            int dataSize = raw + 1;

            if (dataSize >= hbSize)
            {
                throw Corrupt("BIC header size");
            }

            Span<uint> counter = stackalloc uint[257];
            counter.Clear();

            ulong i = 0;

            while (raw != 0 && (i >> 32) < 0x100)
            {
                i = src[raw--] | (i << 8);
            }

            ulong encodedCharTable = i / 0x34;
            int j = (int)(i % 0x34);

            while (raw != 0 && (encodedCharTable >> 32) < 0x100)
            {
                encodedCharTable = src[raw--] | (encodedCharTable << 8);
            }

            int charNum = j + 1;

            if (charNum > maxSymbolValue)
            {
                throw Corrupt("BIC symbol count");
            }

            ulong charTable = encodedCharTable >> 3;
            int k = (int)(encodedCharTable & 7);

            while (raw != 0 && (charTable >> 32) < 0x100)
            {
                charTable = src[raw--] | (charTable << 8);
            }

            tableLog = k + 5;
            uint remaining = 1u << tableLog;
            ulong l = charTable / remaining;

            while (raw != 0 && (l >> 32) < 0x100)
            {
                l = src[raw--] | (l << 8);
            }

            int charLast = (int)(charTable % remaining) + 1;

            if (useLowProbCount != 0)
            {
                charLast = charNum + (int)(charTable % remaining) + 2;
            }

            uint nextPow2 = (uint)charNum;
            nextPow2 |= nextPow2 >> 1;
            nextPow2 |= nextPow2 >> 2;
            nextPow2 |= nextPow2 >> 4;
            nextPow2 |= nextPow2 >> 8;
            nextPow2 |= nextPow2 >> 16;
            nextPow2++;

            if (nextPow2 > 0xFF)
            {
                throw Corrupt("BIC symbol count");
            }

            counter[(int)nextPow2] = (uint)charLast;

            uint bicCount = 0;

            if (nextPow2 != 0xFF)
            {
                do
                {
                    int tableOffset = (int)(3 * (bicCount - nextPow2 + 0x100));
                    int middleIndex = _bicTable[tableOffset + 0];
                    int firstIndex = _bicTable[tableOffset + 1];
                    int lastIndex = _bicTable[tableOffset + 2];
                    uint firstEntry = counter[firstIndex];
                    uint lastEntry = counter[lastIndex];

                    if (firstEntry == lastEntry)
                    {
                        for (int index = firstIndex + 1; index < lastIndex; index++)
                        {
                            counter[index] = firstEntry;
                        }
                    }
                    else
                    {
                        uint divisor = lastEntry - firstEntry + 1;

                        if (divisor == 0)
                        {
                            throw Corrupt("BIC divisor");
                        }

                        ulong next = l / divisor;
                        ulong entry = l % divisor;

                        l = next;

                        while (raw != 0 && (l >> 32) < 0x100)
                        {
                            l = src[raw--] | (l << 8);
                        }

                        counter[middleIndex] = (uint)(entry + firstEntry);
                    }

                    bicCount++;
                }
                while (bicCount < nextPow2);
            }

            if (charNum != 0xFF)
            {
                int accCount = 0;

                for (int index = 1; index <= charNum + 1; index++)
                {
                    short bcCount = unchecked((short)counter[index]);
                    short countDist = unchecked((short)(bcCount - accCount));
                    short count = unchecked((short)(countDist - useLowProbCount));

                    accCount += countDist;
                    norm[index - 1] = count;
                    remaining -= (uint)(count < 0 ? -count : count);
                }
            }

            if (remaining != 0)
            {
                throw Corrupt("BIC normalized counts incomplete");
            }

            if (raw != 0)
            {
                throw Corrupt("BIC header not fully consumed");
            }

            maxSymbolValue = charNum;

            return dataSize;
        }

        private static int ReadNCount(bool bic, ReadOnlySpan<byte> src, short[] norm, ref int maxSymbolValue, out int tableLog)
        {
            return bic
                ? ReadNCountBic(src, norm, ref maxSymbolValue, out tableLog)
                : ReadNCountNormal(src, norm, ref maxSymbolValue, out tableLog);
        }

        private sealed class FrameDecoder
        {
            private readonly bool _bic;
            private readonly HufTable _huf = new();
            private readonly FseTable _llTable = new(1 << LLFSELog);
            private readonly FseTable _ofTable = new(1 << OffFSELog);
            private readonly FseTable _mlTable = new(1 << MLFSELog);
            private readonly FseTable _weightTable = new(1 << HufWeightsFSELog);
            private bool _llValid;
            private bool _ofValid;
            private bool _mlValid;
            private readonly int[] _rep = { 1, 4, 8 };
            private readonly byte[] _literals = new byte[BlockSizeMax];
            private readonly short[] _norm = new short[256];
            private readonly byte[] _weights = new byte[256];

            public FrameDecoder(bool bic)
            {
                _bic = bic;
            }

            private static void Need(int available, int required, string what)
            {
                if (required < 0 || available < required)
                {
                    throw Corrupt($"truncated {what}");
                }
            }

            public int Decode(ReadOnlySpan<byte> src, ref int srcPos, Span<byte> dst, int dstStart)
            {
                Need(src.Length - srcPos, 1, "frame header");

                byte descriptor = src[srcPos++];
                int fcsFlag = descriptor >> 6;
                bool singleSegment = (descriptor & 0x20) != 0;
                bool checksum = (descriptor & 0x04) != 0;
                int dictFlag = descriptor & 3;

                if ((descriptor & 0x08) != 0)
                {
                    throw Corrupt("reserved frame header bit set");
                }

                if (!singleSegment)
                {
                    Need(src.Length - srcPos, 1, "window descriptor");
                    srcPos++;
                }

                int dictSize = dictFlag switch
                {
                    0 => 0,
                    1 => 1,
                    2 => 2,
                    _ => 4,
                };

                Need(src.Length - srcPos, dictSize, "dictionary id");

                uint dictId = 0;

                for (int i = dictSize - 1; i >= 0; i--)
                {
                    dictId = (dictId << 8) | src[srcPos + i];
                }

                srcPos += dictSize;

                if (dictId != 0)
                {
                    throw Corrupt("dictionaries are not supported");
                }

                int fcsSize = fcsFlag switch
                {
                    0 => singleSegment ? 1 : 0,
                    1 => 2,
                    2 => 4,
                    _ => 8,
                };

                Need(src.Length - srcPos, fcsSize, "frame content size");

                ulong contentSize = 0;

                for (int i = fcsSize - 1; i >= 0; i--)
                {
                    contentSize = (contentSize << 8) | src[srcPos + i];
                }

                if (fcsSize == 2)
                {
                    contentSize += 256;
                }

                srcPos += fcsSize;

                int dstPos = dstStart;

                while (true)
                {
                    Need(src.Length - srcPos, 3, "block header");

                    int blockHeader = src[srcPos] | (src[srcPos + 1] << 8) | (src[srcPos + 2] << 16);
                    srcPos += 3;

                    bool lastBlock = (blockHeader & 1) != 0;
                    int blockType = (blockHeader >> 1) & 3;
                    int blockSize = blockHeader >> 3;

                    switch (blockType)
                    {
                        case 0:
                            Need(src.Length - srcPos, blockSize, "raw block");
                            Need(dst.Length - dstPos, blockSize, "output buffer");
                            src.Slice(srcPos, blockSize).CopyTo(dst[dstPos..]);
                            srcPos += blockSize;
                            dstPos += blockSize;
                            break;
                        case 1:
                            Need(src.Length - srcPos, 1, "RLE block");
                            Need(dst.Length - dstPos, blockSize, "output buffer");
                            dst.Slice(dstPos, blockSize).Fill(src[srcPos]);
                            srcPos += 1;
                            dstPos += blockSize;
                            break;
                        case 2:
                            if (blockSize >= BlockSizeMax)
                            {
                                throw Corrupt("compressed block too large");
                            }

                            Need(src.Length - srcPos, blockSize, "compressed block");
                            dstPos = DecodeCompressedBlock(src.Slice(srcPos, blockSize), dst, dstStart, dstPos);
                            srcPos += blockSize;
                            break;
                        default:
                            throw Corrupt("reserved block type");
                    }

                    if (lastBlock)
                    {
                        break;
                    }
                }

                if (checksum)
                {
                    Need(src.Length - srcPos, 4, "frame checksum");
                    srcPos += 4;
                }

                int written = dstPos - dstStart;

                if (fcsSize != 0 && (ulong)written != contentSize)
                {
                    throw Corrupt($"frame size mismatch (expected {contentSize}, got {written})");
                }

                return written;
            }

            private int DecodeLiterals(ReadOnlySpan<byte> block, out int literalCount)
            {
                Need(block.Length, 1, "literals header");

                int b0 = block[0];
                int type = b0 & 3;
                int sizeFormat = (b0 >> 2) & 3;

                if (type <= 1)
                {
                    int size;
                    int headerSize;

                    switch (sizeFormat)
                    {
                        case 0:
                        case 2:
                            size = b0 >> 3;
                            headerSize = 1;
                            break;
                        case 1:
                            Need(block.Length, 2, "literals header");
                            size = (b0 >> 4) + (block[1] << 4);
                            headerSize = 2;
                            break;
                        default:
                            Need(block.Length, 3, "literals header");
                            size = (b0 >> 4) + (block[1] << 4) + (block[2] << 12);
                            headerSize = 3;
                            break;
                    }

                    if (size > BlockSizeMax)
                    {
                        throw Corrupt("literals too large");
                    }

                    literalCount = size;

                    if (type == 0)
                    {
                        Need(block.Length - headerSize, size, "raw literals");
                        block.Slice(headerSize, size).CopyTo(_literals);

                        return headerSize + size;
                    }

                    Need(block.Length - headerSize, 1, "RLE literals");
                    _literals.AsSpan(0, size).Fill(block[headerSize]);

                    return headerSize + 1;
                }

                int lhSize;
                int bits;
                bool singleStream = false;

                switch (sizeFormat)
                {
                    case 0:
                        singleStream = true;
                        lhSize = 3;
                        bits = 10;
                        break;
                    case 1:
                        lhSize = 3;
                        bits = 10;
                        break;
                    case 2:
                        lhSize = 4;
                        bits = 14;
                        break;
                    default:
                        lhSize = 5;
                        bits = 18;
                        break;
                }

                Need(block.Length, lhSize, "literals header");

                ulong header = 0;

                for (int i = lhSize - 1; i >= 0; i--)
                {
                    header = (header << 8) | block[i];
                }

                ulong mask = (1UL << bits) - 1;
                int regenerated = (int)((header >> 4) & mask);
                int compressed = (int)((header >> (4 + bits)) & mask);

                if (regenerated > BlockSizeMax)
                {
                    throw Corrupt("literals too large");
                }

                Need(block.Length - lhSize, compressed, "compressed literals");

                ReadOnlySpan<byte> data = block.Slice(lhSize, compressed);

                if (type == 2)
                {
                    int treeSize = ReadHuffmanTable(data);
                    data = data[treeSize..];
                }
                else if (!_huf.Valid)
                {
                    throw Corrupt("treeless literals without a previous Huffman table");
                }

                Span<byte> output = _literals.AsSpan(0, regenerated);

                if (singleStream)
                {
                    DecodeHuffmanStream(data, output);
                }
                else
                {
                    Need(data.Length, 6, "Huffman jump table");

                    int size1 = BinaryPrimitives.ReadUInt16LittleEndian(data);
                    int size2 = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
                    int size3 = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
                    int size4 = data.Length - 6 - size1 - size2 - size3;

                    if (size4 < 1)
                    {
                        throw Corrupt("Huffman jump table");
                    }

                    int segment = (regenerated + 3) / 4;

                    if (segment * 3 > regenerated)
                    {
                        throw Corrupt("Huffman 4-stream size");
                    }

                    int start = 6;

                    DecodeHuffmanStream(data.Slice(start, size1), output[..segment]);
                    start += size1;
                    DecodeHuffmanStream(data.Slice(start, size2), output.Slice(segment, segment));
                    start += size2;
                    DecodeHuffmanStream(data.Slice(start, size3), output.Slice(segment * 2, segment));
                    start += size3;
                    DecodeHuffmanStream(data.Slice(start, size4), output[(segment * 3)..]);
                }

                literalCount = regenerated;

                return lhSize + compressed;
            }

            private int ReadHuffmanTable(ReadOnlySpan<byte> src)
            {
                Need(src.Length, 1, "Huffman header");

                int headerByte = src[0];
                int numWeights;
                int consumed;
                byte[] weights = _weights;

                if (headerByte >= 128)
                {
                    numWeights = headerByte - 127;
                    int bytes = (numWeights + 1) / 2;
                    Need(src.Length - 1, bytes, "Huffman weights");

                    for (int i = 0; i < numWeights; i++)
                    {
                        byte b = src[1 + i / 2];
                        weights[i] = (byte)((i & 1) == 0 ? b >> 4 : b & 0xF);
                    }

                    consumed = 1 + bytes;
                }
                else
                {
                    Need(src.Length - 1, headerByte, "Huffman FSE weights");
                    numWeights = DecodeFseWeights(src.Slice(1, headerByte), weights);
                    consumed = 1 + headerByte;
                }

                if (numWeights == 0 || numWeights > 255)
                {
                    throw Corrupt("Huffman weight count");
                }

                Span<int> rankStats = stackalloc int[HufTableLogMax + 2];
                rankStats.Clear();
                uint weightTotal = 0;

                for (int i = 0; i < numWeights; i++)
                {
                    int w = weights[i];

                    if (w > HufTableLogMax)
                    {
                        throw Corrupt("Huffman weight too large");
                    }

                    rankStats[w]++;
                    weightTotal += (1u << w) >> 1;
                }

                if (weightTotal == 0)
                {
                    throw Corrupt("Huffman weights are empty");
                }

                int tableLog = BitOperations.Log2(weightTotal) + 1;

                if (tableLog > HufTableLogMax)
                {
                    throw Corrupt("Huffman table log too large");
                }

                uint rest = (1u << tableLog) - weightTotal;
                int restLog = BitOperations.Log2(rest);

                if ((1u << restLog) != rest)
                {
                    throw Corrupt("Huffman last weight");
                }

                int lastWeight = restLog + 1;
                weights[numWeights] = (byte)lastWeight;
                rankStats[lastWeight]++;

                if (rankStats[1] < 2 || (rankStats[1] & 1) != 0)
                {
                    throw Corrupt("Huffman rank 1 count");
                }

                int numSymbols = numWeights + 1;
                Span<int> rankStart = stackalloc int[HufTableLogMax + 2];
                int nextRankStart = 0;

                for (int w = 1; w <= tableLog; w++)
                {
                    rankStart[w] = nextRankStart;
                    nextRankStart += rankStats[w] << (w - 1);
                }

                if (nextRankStart != 1 << tableLog)
                {
                    throw Corrupt("Huffman table fill");
                }

                for (int s = 0; s < numSymbols; s++)
                {
                    int w = weights[s];

                    if (w == 0)
                    {
                        continue;
                    }

                    int length = (1 << w) >> 1;
                    int start = rankStart[w];
                    byte nbBits = (byte)(tableLog + 1 - w);

                    _huf.Symbol.AsSpan(start, length).Fill((byte)s);
                    _huf.NbBits.AsSpan(start, length).Fill(nbBits);
                    rankStart[w] = start + length;
                }

                _huf.TableLog = tableLog;
                _huf.Valid = true;

                return consumed;
            }

            private int DecodeFseWeights(ReadOnlySpan<byte> src, byte[] weights)
            {
                int maxSymbolValue = 255;
                int ncountSize = ReadNCount(_bic, src, _norm, ref maxSymbolValue, out int tableLog);

                if (tableLog > HufWeightsFSELog)
                {
                    throw Corrupt("Huffman weights table log too large");
                }

                _weightTable.Build(_norm, maxSymbolValue, tableLog);

                BackwardBitReader reader = new(src[ncountSize..]);

                int state1 = (int)reader.ReadBits(tableLog);
                int state2 = (int)reader.ReadBits(tableLog);
                int count = 0;
                const int Capacity = 255;

                while (true)
                {
                    if (count > Capacity - 2)
                    {
                        throw Corrupt("too many Huffman weights");
                    }

                    weights[count++] = _weightTable.Symbol[state1];
                    state1 = _weightTable.Update(state1, ref reader);

                    if (reader.Overflowed)
                    {
                        weights[count++] = _weightTable.Symbol[state2];
                        break;
                    }

                    if (count > Capacity - 2)
                    {
                        throw Corrupt("too many Huffman weights");
                    }

                    weights[count++] = _weightTable.Symbol[state2];
                    state2 = _weightTable.Update(state2, ref reader);

                    if (reader.Overflowed)
                    {
                        weights[count++] = _weightTable.Symbol[state1];
                        break;
                    }
                }

                return count;
            }

            private void DecodeHuffmanStream(ReadOnlySpan<byte> src, Span<byte> output)
            {
                BackwardBitReader reader = new(src);
                int tableLog = _huf.TableLog;
                byte[] symbols = _huf.Symbol;
                byte[] nbBits = _huf.NbBits;

                for (int i = 0; i < output.Length; i++)
                {
                    int index = (int)reader.PeekBits(tableLog);
                    output[i] = symbols[index];
                    reader.Consume(nbBits[index]);
                }

                if (reader.BitPos != 0)
                {
                    throw Corrupt("Huffman stream not fully consumed");
                }
            }

            private int BuildSequenceTable(ReadOnlySpan<byte> src, int mode, FseTable table, ref bool valid, short[] defaultNorm, int defaultLog, int maxSymbol, int maxLog)
            {
                switch (mode)
                {
                    case 0:
                        table.Build(defaultNorm, defaultNorm.Length - 1, defaultLog);
                        valid = true;
                        return 0;
                    case 1:
                        Need(src.Length, 1, "RLE sequence table");

                        if (src[0] > maxSymbol)
                        {
                            throw Corrupt("RLE sequence symbol");
                        }

                        table.BuildRle(src[0]);
                        valid = true;
                        return 1;
                    case 2:
                        int maxSymbolValue = maxSymbol;
                        int size = ReadNCount(_bic, src, _norm, ref maxSymbolValue, out int tableLog);

                        if (tableLog > maxLog || maxSymbolValue > maxSymbol)
                        {
                            throw Corrupt("sequence table parameters");
                        }

                        table.Build(_norm, maxSymbolValue, tableLog);
                        valid = true;
                        return size;
                    default:
                        if (!valid)
                        {
                            throw Corrupt("repeat sequence table without a previous table");
                        }

                        return 0;
                }
            }

            private int DecodeCompressedBlock(ReadOnlySpan<byte> block, Span<byte> dst, int frameStart, int dstPos)
            {
                int pos = DecodeLiterals(block, out int literalCount);

                Need(block.Length - pos, 1, "sequences header");

                int b0 = block[pos++];
                int sequenceCount;

                if (b0 == 0)
                {
                    sequenceCount = 0;
                }
                else if (b0 < 128)
                {
                    sequenceCount = b0;
                }
                else if (b0 < 255)
                {
                    Need(block.Length - pos, 1, "sequences header");
                    sequenceCount = ((b0 - 128) << 8) + block[pos++];
                }
                else
                {
                    Need(block.Length - pos, 2, "sequences header");
                    sequenceCount = block[pos] + (block[pos + 1] << 8) + 0x7F00;
                    pos += 2;
                }

                ReadOnlySpan<byte> literals = _literals.AsSpan(0, literalCount);

                if (sequenceCount == 0)
                {
                    if (pos != block.Length)
                    {
                        throw Corrupt("trailing data after literals-only block");
                    }

                    Need(dst.Length - dstPos, literalCount, "output buffer");
                    literals.CopyTo(dst[dstPos..]);

                    return dstPos + literalCount;
                }

                Need(block.Length - pos, 1, "sequence modes");

                int modes = block[pos++];

                if ((modes & 3) != 0)
                {
                    throw Corrupt("reserved sequence mode bits");
                }

                pos += BuildSequenceTable(block[pos..], modes >> 6, _llTable, ref _llValid, _llDefaultNorm, 6, MaxLL, LLFSELog);
                pos += BuildSequenceTable(block[pos..], (modes >> 4) & 3, _ofTable, ref _ofValid, _ofDefaultNorm, 5, MaxOff, OffFSELog);
                pos += BuildSequenceTable(block[pos..], (modes >> 2) & 3, _mlTable, ref _mlValid, _mlDefaultNorm, 6, MaxML, MLFSELog);

                BackwardBitReader reader = new(block[pos..]);

                int llState = (int)reader.ReadBits(_llTable.TableLog);
                int ofState = (int)reader.ReadBits(_ofTable.TableLog);
                int mlState = (int)reader.ReadBits(_mlTable.TableLog);

                int[] rep = _rep;
                int literalPos = 0;

                for (int i = 0; i < sequenceCount; i++)
                {
                    int llCode = _llTable.Symbol[llState];
                    int ofCode = _ofTable.Symbol[ofState];
                    int mlCode = _mlTable.Symbol[mlState];

                    if (llCode > MaxLL || mlCode > MaxML || ofCode > MaxOff)
                    {
                        throw Corrupt("sequence code out of range");
                    }

                    long offsetValue = (1L << ofCode) + reader.ReadBits(ofCode);
                    int matchLength = _mlBase[mlCode] + (int)reader.ReadBits(_mlBits[mlCode]);
                    int literalLength = _llBase[llCode] + (int)reader.ReadBits(_llBits[llCode]);

                    long offset;

                    if (offsetValue > 3)
                    {
                        offset = offsetValue - 3;

                        if (offset > int.MaxValue)
                        {
                            throw Corrupt("offset too large");
                        }

                        rep[2] = rep[1];
                        rep[1] = rep[0];
                        rep[0] = (int)offset;
                    }
                    else
                    {
                        int index = (int)offsetValue - 1 + (literalLength == 0 ? 1 : 0);

                        if (index == 0)
                        {
                            offset = rep[0];
                        }
                        else
                        {
                            offset = index == 3 ? rep[0] - 1L : rep[index];

                            if (offset <= 0)
                            {
                                throw Corrupt("invalid repeat offset");
                            }

                            if (index != 1)
                            {
                                rep[2] = rep[1];
                            }

                            rep[1] = rep[0];
                            rep[0] = (int)offset;
                        }
                    }

                    if (i != sequenceCount - 1)
                    {
                        llState = _llTable.Update(llState, ref reader);
                        mlState = _mlTable.Update(mlState, ref reader);
                        ofState = _ofTable.Update(ofState, ref reader);
                    }

                    if (literalLength > literalCount - literalPos)
                    {
                        throw Corrupt("literal length exceeds literals");
                    }

                    if ((long)literalLength + matchLength > dst.Length - dstPos)
                    {
                        throw Corrupt("output buffer too small");
                    }

                    literals.Slice(literalPos, literalLength).CopyTo(dst[dstPos..]);
                    literalPos += literalLength;
                    dstPos += literalLength;

                    if (offset > dstPos - frameStart)
                    {
                        throw Corrupt("match offset before frame start");
                    }

                    int matchStart = dstPos - (int)offset;

                    if (offset >= matchLength)
                    {
                        dst.Slice(matchStart, matchLength).CopyTo(dst[dstPos..]);
                    }
                    else
                    {
                        for (int m = 0; m < matchLength; m++)
                        {
                            dst[dstPos + m] = dst[matchStart + m];
                        }
                    }

                    dstPos += matchLength;
                }

                if (reader.BitPos != 0)
                {
                    throw Corrupt("sequence bitstream not fully consumed");
                }

                int remainingLiterals = literalCount - literalPos;

                Need(dst.Length - dstPos, remainingLiterals, "output buffer");
                literals[literalPos..].CopyTo(dst[dstPos..]);

                return dstPos + remainingLiterals;
            }
        }
    }
}
