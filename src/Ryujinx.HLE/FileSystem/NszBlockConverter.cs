#if !ANDROID
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Ryujinx.HLE.FileSystem
{
    public static class NszBlockConverter
    {
        private const int HeaderSize = 0x4000;
        private const int BlockExponent = 20;
        private const int BlockSize = 1 << BlockExponent;
        private const int ZstdLevel = 3;

        private sealed class Entry
        {
            public string Name;
            public long SourceOffset;
            public long SourceSize;
            public long OutputSize;
            public bool Convert;
            public long PrefixLength;
            public long DecompressedSize;
            public byte[] Hash;
            public byte[] PrefixHash;
        }

        public static bool HasSolidNcz(string path)
        {
            using FileStream f = File.OpenRead(path);

            foreach (Entry e in ReadEntries(f))
            {
                if (!e.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ParseNcz(f, e);

                if (e.Convert)
                {
                    return true;
                }
            }

            return false;
        }

        public static void ConvertAndVerify(
            string sourcePath,
            string outputPath,
            Action<int, string> progress = null)
        {
            using FileStream input = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1 << 20,
                FileOptions.SequentialScan);

            List<Entry> entries = ReadEntries(input);

            long total = 0;

            foreach (Entry e in entries)
            {
                if (e.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
                {
                    ParseNcz(input, e);
                }

                total += e.Convert ? e.DecompressedSize : e.SourceSize;
            }

            if (!entries.Exists(x => x.Convert))
            {
                throw new InvalidOperationException("NSZ ja usa NCZBLOCK.");
            }

            long done = 0;
            const string conversionPhase = "Convertendo NSZ para NCZBLOCK";

            progress?.Invoke(0, conversionPhase);

            void Advance(long count)
            {
                done += count;
                int percent = total == 0
                    ? 100
                    : (int)Math.Clamp(done * 100 / total, 0, 100);
                progress?.Invoke(percent, conversionPhase);
            }

            int pfsHeaderSize = GetPfsHeaderSize(entries);

            using (FileStream output = new(
                outputPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                1 << 20))
            {
                output.Write(new byte[pfsHeaderSize]);

                foreach (Entry e in entries)
                {
                    long start = output.Position;

                    if (e.Convert)
                    {
                        ConvertSolid(input, output, e, Advance);
                    }
                    else
                    {
                        e.Hash = CopyAndHash(
                            input,
                            e.SourceOffset,
                            e.SourceSize,
                            output,
                            Advance);
                    }

                    e.OutputSize = output.Position - start;
                }

                long end = output.Position;

                output.Position = 0;
                output.Write(BuildPfsHeader(entries, pfsHeaderSize));
                output.Flush(true);
                output.Position = end;
            }

            progress?.Invoke(0, "Verificando integridade");
            Verify(outputPath, entries, progress);
            progress?.Invoke(100, "Concluido");
        }

        private static void ParseNcz(FileStream f, Entry e)
        {
            f.Position = e.SourceOffset + HeaderSize;

            byte[] h = new byte[0x10];
            ReadExact(f, h);

            if (!h.AsSpan(0, 8).SequenceEqual("NCZSECTN"u8))
            {
                throw new InvalidDataException("NCZSECTN ausente.");
            }

            long sectionCount = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(8));

            if (sectionCount <= 0 || sectionCount > 0x100)
            {
                throw new InvalidDataException("section count invalido.");
            }

            byte[] sections = new byte[checked((int)sectionCount * 0x40)];
            ReadExact(f, sections);

            long expectedEnd = HeaderSize;

            for (int i = 0; i < sectionCount; i++)
            {
                ReadOnlySpan<byte> s = sections.AsSpan(i * 0x40, 0x40);

                long off = BinaryPrimitives.ReadInt64LittleEndian(s);
                long size = BinaryPrimitives.ReadInt64LittleEndian(s[8..]);

                expectedEnd = Math.Max(expectedEnd, off + size);
            }

            e.PrefixLength = HeaderSize + 0x10 + sections.Length;

            f.Position = e.SourceOffset + e.PrefixLength;

            byte[] magic = new byte[8];
            ReadExact(f, magic);

            e.Convert = !magic.AsSpan().SequenceEqual("NCZBLOCK"u8);
            e.DecompressedSize = expectedEnd - HeaderSize;
        }

        private static void ConvertSolid(
            FileStream input,
            FileStream output,
            Entry e,
            Action<long> advance)
        {
            e.PrefixHash = HashRange(
                input,
                e.SourceOffset,
                e.PrefixLength);

            CopyRange(
                input,
                e.SourceOffset,
                e.PrefixLength,
                output);

            int blockCount = checked(
                (int)((e.DecompressedSize + BlockSize - 1) / BlockSize));

            byte[] blockHeader = new byte[0x18];

            "NCZBLOCK"u8.CopyTo(blockHeader);
            blockHeader[8] = 2;
            blockHeader[9] = 1;
            blockHeader[0xB] = BlockExponent;

            BinaryPrimitives.WriteInt32LittleEndian(
                blockHeader.AsSpan(0xC),
                blockCount);

            BinaryPrimitives.WriteInt64LittleEndian(
                blockHeader.AsSpan(0x10),
                e.DecompressedSize);

            output.Write(blockHeader);

            long tablePos = output.Position;
            byte[] sizes = new byte[blockCount * 4];

            output.Write(sizes);

            long compressedOffset = e.SourceOffset + e.PrefixLength;
            long compressedSize = e.SourceSize - e.PrefixLength;

            using LimitedStream source = new(
                input,
                compressedOffset,
                compressedSize);

            using BlockWriter writer = new(
                output,
                e.DecompressedSize,
                advance);

            NativeZstd.DecompressToStream(
                source,
                writer,
                e.DecompressedSize);

            writer.Complete();
            e.Hash = writer.Hash;

            if (writer.Sizes.Count != blockCount)
            {
                throw new InvalidDataException("Quantidade de blocos incorreta.");
            }

            for (int i = 0; i < blockCount; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    sizes.AsSpan(i * 4),
                    writer.Sizes[i]);
            }

            long end = output.Position;
            output.Position = tablePos;
            output.Write(sizes);
            output.Position = end;
        }

        private static void Verify(
            string path,
            List<Entry> sourceEntries,
            Action<int, string> progress)
        {
            using FileStream f = File.OpenRead(path);
            List<Entry> entries = ReadEntries(f);

            if (entries.Count != sourceEntries.Count)
            {
                throw new InvalidDataException("PFS0 alterado durante conversao.");
            }

            long total = 0;
            foreach (Entry e in sourceEntries)
            {
                total += e.Convert ? e.DecompressedSize : e.SourceSize;
            }

            long done = 0;

            void AdvanceVerify(long count)
            {
                done += count;
                int percent = total == 0
                    ? 100
                    : (int)Math.Clamp(done * 100 / total, 0, 100);
                progress?.Invoke(percent, "Verificando integridade");
            }

            for (int i = 0; i < entries.Count; i++)
            {
                Entry src = sourceEntries[i];
                Entry dst = entries[i];

                if (src.Name != dst.Name)
                {
                    throw new InvalidDataException("Nome de entry divergente.");
                }

                byte[] hash;

                if (src.Convert)
                {
                    ParseNcz(f, dst);

                    if (dst.Convert)
                    {
                        throw new InvalidDataException("Destino ainda e NCZ solid.");
                    }


                    byte[] prefixHash = HashRange(
                        f,
                        dst.SourceOffset,
                        dst.PrefixLength);

                    if (!CryptographicOperations.FixedTimeEquals(
                        prefixHash,
                        src.PrefixHash))
                    {
                        throw new InvalidDataException(
                            $"SHA-256 do prefixo NCZ divergente: {src.Name}");
                    }

                    hash = HashNczBlock(f, dst, AdvanceVerify);
                }
                else
                {
                    hash = HashRange(
                        f,
                        dst.SourceOffset,
                        dst.SourceSize,
                        AdvanceVerify);
                }

                if (!CryptographicOperations.FixedTimeEquals(hash, src.Hash))
                {
                    throw new InvalidDataException(
                        $"SHA-256 divergente: {src.Name}");
                }

                progress?.Invoke(
                    total == 0
                        ? 100
                        : (int)Math.Clamp(done * 100 / total, 0, 100),
                    "Verificando integridade");
            }
        }

        private static byte[] HashNczBlock(
            FileStream f,
            Entry e,
            Action<long> progress)
        {
            f.Position = e.SourceOffset + e.PrefixLength;

            byte[] bh = new byte[0x18];
            ReadExact(f, bh);

            if (!bh.AsSpan(0, 8).SequenceEqual("NCZBLOCK"u8))
            {
                throw new InvalidDataException("NCZBLOCK magic ausente.");
            }

            int exponent = bh[0xB];
            int count = BinaryPrimitives.ReadInt32LittleEndian(
                bh.AsSpan(0xC));
            long size = BinaryPrimitives.ReadInt64LittleEndian(
                bh.AsSpan(0x10));

            if (exponent < 14 || exponent > 30)
            {
                throw new InvalidDataException(
                    $"NCZBLOCK exponent invalido: {exponent}");
            }

            if (count <= 0 || size <= 0)
            {
                throw new InvalidDataException(
                    $"NCZBLOCK metadata invalida: blocks={count}, size={size}");
            }

            long blockSizeLong = 1L << exponent;
            long expectedBlocks =
                (size + blockSizeLong - 1) / blockSizeLong;

            if (expectedBlocks != count)
            {
                throw new InvalidDataException(
                    $"NCZBLOCK block count invalido: {count} != {expectedBlocks}");
            }

            int blockSize = checked((int)blockSizeLong);

            byte[] table = new byte[checked(count * 4)];
            ReadExact(f, table);

            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            long remaining = size;

            for (int i = 0; i < count; i++)
            {
                int rawSize =
                    (int)Math.Min(blockSize, remaining);

                int packedSize =
                    BinaryPrimitives.ReadInt32LittleEndian(
                        table.AsSpan(i * 4));

                if (packedSize <= 0 || packedSize > rawSize)
                {
                    throw new InvalidDataException(
                        $"NCZBLOCK tamanho invalido no bloco {i}: {packedSize}/{rawSize}");
                }

                byte[] packed = new byte[packedSize];
                ReadExact(f, packed);

                if (packedSize < rawSize)
                {
                    byte[] raw = new byte[rawSize];

                    int written =
                        NativeZstd.Decompress(packed, raw);

                    if (written != rawSize)
                    {
                        throw new InvalidDataException(
                            $"Bloco NCZBLOCK {i} invalido: {written}/{rawSize} bytes.");
                    }

                    hash.AppendData(raw);
                }
                else
                {
                    hash.AppendData(packed);
                }

                remaining -= rawSize;
                progress?.Invoke(rawSize);
            }

            if (remaining != 0)
            {
                throw new InvalidDataException(
                    $"NCZBLOCK terminou com {remaining} bytes pendentes.");
            }

            return hash.GetHashAndReset();
        }

        private static List<Entry> ReadEntries(FileStream f)
        {
            f.Position = 0;

            byte[] h = new byte[0x10];
            ReadExact(f, h);

            if (!h.AsSpan(0, 4).SequenceEqual("PFS0"u8))
            {
                throw new InvalidDataException("Arquivo nao e PFS0.");
            }

            int count = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(4));
            int stringSize = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(8));

            byte[] table = new byte[count * 0x18];
            byte[] strings = new byte[stringSize];

            ReadExact(f, table);
            ReadExact(f, strings);

            long dataBase = 0x10L + table.Length + strings.Length;

            List<Entry> result = new();

            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> e = table.AsSpan(i * 0x18, 0x18);

                long off = BinaryPrimitives.ReadInt64LittleEndian(e);
                long size = BinaryPrimitives.ReadInt64LittleEndian(e[8..]);
                int no = BinaryPrimitives.ReadInt32LittleEndian(e[0x10..]);

                int end = Array.IndexOf(strings, (byte)0, no);
                string name = Encoding.UTF8.GetString(strings, no, end - no);

                result.Add(new Entry
                {
                    Name = name,
                    SourceOffset = dataBase + off,
                    SourceSize = size,
                });
            }

            return result;
        }

        private static int GetPfsHeaderSize(List<Entry> entries)
        {
            int names = 0;

            foreach (Entry e in entries)
            {
                names += Encoding.UTF8.GetByteCount(e.Name) + 1;
            }

            int size = 0x10 + entries.Count * 0x18 + names;

            return (size + 0x1F) & ~0x1F;
        }

        private static byte[] BuildPfsHeader(
            List<Entry> entries,
            int headerSize)
        {
            byte[] h = new byte[headerSize];

            "PFS0"u8.CopyTo(h);

            BinaryPrimitives.WriteInt32LittleEndian(
                h.AsSpan(4),
                entries.Count);

            int tableStart = 0x10 + entries.Count * 0x18;

            BinaryPrimitives.WriteInt32LittleEndian(
                h.AsSpan(8),
                headerSize - tableStart);

            long dataOffset = 0;
            int nameOffset = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                Entry item = entries[i];
                Span<byte> e = h.AsSpan(0x10 + i * 0x18, 0x18);

                BinaryPrimitives.WriteInt64LittleEndian(e, dataOffset);
                BinaryPrimitives.WriteInt64LittleEndian(e[8..], item.OutputSize);
                BinaryPrimitives.WriteInt32LittleEndian(e[0x10..], nameOffset);

                byte[] name = Encoding.UTF8.GetBytes(item.Name);
                name.CopyTo(h.AsSpan(tableStart + nameOffset));

                nameOffset += name.Length + 1;
                dataOffset += item.OutputSize;
            }

            return h;
        }

        private static byte[] CopyAndHash(
            FileStream input,
            long offset,
            long size,
            FileStream output,
            Action<long> progress)
        {
            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            byte[] buffer = new byte[1 << 20];
            input.Position = offset;

            while (size > 0)
            {
                int count = (int)Math.Min(buffer.Length, size);
                ReadExact(input, buffer.AsSpan(0, count));

                output.Write(buffer, 0, count);
                hash.AppendData(buffer, 0, count);

                size -= count;
                progress?.Invoke(count);
            }

            return hash.GetHashAndReset();
        }

        private static void CopyRange(
            FileStream input,
            long offset,
            long size,
            FileStream output)
        {
            byte[] buffer = new byte[1 << 20];
            input.Position = offset;

            while (size > 0)
            {
                int count = (int)Math.Min(buffer.Length, size);
                ReadExact(input, buffer.AsSpan(0, count));

                output.Write(buffer, 0, count);
                size -= count;
            }
        }

        private static byte[] HashRange(
            FileStream input,
            long offset,
            long size,
            Action<long> progress = null)
        {
            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            byte[] buffer = new byte[1 << 20];
            input.Position = offset;

            while (size > 0)
            {
                int count = (int)Math.Min(buffer.Length, size);
                ReadExact(input, buffer.AsSpan(0, count));

                hash.AppendData(buffer, 0, count);
                size -= count;
                progress?.Invoke(count);
            }

            return hash.GetHashAndReset();
        }

        private static void ReadExact(Stream s, byte[] buffer)
            => ReadExact(s, buffer.AsSpan());

        private static void ReadExact(Stream s, Span<byte> buffer)
        {
            int done = 0;

            while (done < buffer.Length)
            {
                int read = s.Read(buffer[done..]);

                if (read <= 0)
                {
                    throw new EndOfStreamException();
                }

                done += read;
            }
        }

        private sealed class LimitedStream : Stream
        {
            private readonly FileStream _base;
            private readonly long _start;
            private readonly long _length;
            private long _position;

            public LimitedStream(FileStream source, long start, long length)
            {
                _base = source;
                _start = start;
                _length = length;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= _length)
                {
                    return 0;
                }

                int wanted = (int)Math.Min(count, _length - _position);

                _base.Position = _start + _position;
                int read = _base.Read(buffer, offset, wanted);

                _position += read;

                return read;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _length;

            public override long Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            public override void Flush() {}
            public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }

        private sealed class BlockWriter : Stream
        {
            private readonly FileStream _output;
            private readonly long _expected;
            private readonly Action<long> _progress;
            private readonly byte[] _raw = new byte[BlockSize];
            private readonly byte[] _packed =
                new byte[NativeZstd.CompressBound(BlockSize)];

            private readonly IncrementalHash _hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            private int _count;
            private long _total;

            public List<int> Sizes { get; } = new();
            public byte[] Hash { get; private set; }

            public BlockWriter(
                FileStream output,
                long expected,
                Action<long> progress)
            {
                _output = output;
                _expected = expected;
                _progress = progress;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                while (count > 0)
                {
                    int copy = Math.Min(BlockSize - _count, count);

                    Buffer.BlockCopy(buffer, offset, _raw, _count, copy);

                    _hash.AppendData(buffer, offset, copy);

                    _count += copy;
                    _total += copy;
                    offset += copy;
                    count -= copy;

                    _progress?.Invoke(copy);

                    if (_count == BlockSize)
                    {
                        FlushBlock();
                    }
                }
            }

            private void FlushBlock()
            {
                if (_count == 0)
                {
                    return;
                }

                int packed = NativeZstd.Compress(
                    _raw,
                    _count,
                    _packed,
                    ZstdLevel);

                if (packed < _count)
                {
                    _output.Write(_packed, 0, packed);
                    Sizes.Add(packed);
                }
                else
                {
                    _output.Write(_raw, 0, _count);
                    Sizes.Add(_count);
                }

                _count = 0;
            }

            public void Complete()
            {
                FlushBlock();

                if (_total != _expected)
                {
                    throw new InvalidDataException(
                        $"Tamanho descomprimido {_total} != {_expected}");
                }

                Hash = _hash.GetHashAndReset();
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _total;

            public override long Position
            {
                get => _total;
                set => throw new NotSupportedException();
            }

            public override void Flush() => _output.Flush();
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
        }
    }
}

#endif
