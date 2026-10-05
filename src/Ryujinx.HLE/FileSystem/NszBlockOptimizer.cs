using Ryujinx.Common.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ZstdSharp;

namespace Ryujinx.HLE.FileSystem
{
    // Offline solid NSZ -> block NSZ. Keeps NCZ prefixes/crypto metadata, never changes the reader.
    // Based on the conversion design published in Ryujinx Neo commit 06b96f2a (NszBlockConverter).
    public static class NszBlockOptimizer
    {
        private const int HeaderSize = 0x4000;
        private const int BlockSize = 1 << 20;
        private static int _busy;
        private static volatile bool _cancel;
        private static volatile int _phase;
        private static long _done, _total;
        public static int Phase => _phase;
        public static int Permille
        {
            get
            {
                long total = Interlocked.Read(ref _total);
                return total > 0 ? (int)Math.Clamp(Interlocked.Read(ref _done) * 1000.0 / total, 0, 1000) : 0;
            }
        }
        public static void Cancel() => _cancel = true;

        private sealed class Entry
        {
            public string Name;
            public long Offset, Size, OutputSize, PrefixSize, BodySize;
            public bool Convert;
            public byte[] Hash, PrefixHash;
        }

        // >0: conservative required space for the new NSZ; 0: already block-only; -1: invalid.
        public static long GetRequiredSize(Stream input)
        {
            try
            {
                List<Entry> plan = ReadPlan(input);
                if (!plan.Any(e => e.Convert)) return 0;
                long size = PfsHeaderSize(plan);
                foreach (Entry e in plan)
                {
                    size = checked(size + (e.Convert
                        ? e.PrefixSize + 0x18 + BlockCount(e.BodySize) * 4L + e.BodySize : e.Size));
                }
                return size;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Loader, $"[NEO-QOL4-NSZBLOCK] Inspection failed: {ex.Message}");
                return -1;
            }
        }

        public static int Optimize(Stream input, Stream output)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return NczCompressor.ResultError;
            _cancel = false;
            _phase = NczCompressor.PhaseCompressing;
            Interlocked.Exchange(ref _done, 0);
            Interlocked.Exchange(ref _total, 0);
            try
            {
                if (ReferenceEquals(input, output) || !output.CanRead || !output.CanWrite || !output.CanSeek)
                    throw new NotSupportedException("A conversão precisa de arquivos distintos com busca e releitura.");
                List<Entry> plan = ReadPlan(input);
                if (!plan.Any(e => e.Convert)) return NczCompressor.ResultNoSavings;
                long total = 0;
                foreach (Entry e in plan) total = checked(total + (e.Convert ? e.BodySize : e.Size));
                Interlocked.Exchange(ref _total, checked(total * 2));
                int headerSize = PfsHeaderSize(plan);
                output.SetLength(0);
                output.Position = 0;
                output.Write(new byte[headerSize]);
                using Compressor compressor = new(3);
                foreach (Entry e in plan)
                {
                    CheckCanceled();
                    long start = output.Position;
                    if (e.Convert) ConvertSolid(input, output, e, compressor);
                    else e.Hash = CopyAndHash(input, e.Offset, e.Size, output, true);
                    e.OutputSize = output.Position - start;
                }
                long end = output.Position;
                output.Position = 0;
                output.Write(BuildHeader(plan, headerSize));
                output.Position = end;
                output.Flush();
                _phase = NczCompressor.PhaseVerifying;
                Verify(output, plan);
                CheckCanceled();
                Logger.Notice.Print(LogClass.Loader, $"[NEO-QOL4-NSZBLOCK] Verified files={plan.Count} converted={plan.Count(e => e.Convert)} input={input.Length} output={output.Length}");
                return NczCompressor.ResultOk;
            }
            catch (OperationCanceledException) { return NczCompressor.ResultCanceled; }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Loader, $"[NEO-QOL4-NSZBLOCK] Failed: {ex.Message}");
                return NczCompressor.ResultError;
            }
            finally { _phase = NczCompressor.PhaseIdle; Volatile.Write(ref _busy, 0); }
        }

        private static List<Entry> ReadPlan(Stream input)
        {
            List<Entry> entries = ReadEntries(input);
            bool hasNcz = false;
            foreach (Entry e in entries)
            {
                if (!e.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase)) continue;
                ParseNcz(input, e);
                hasNcz = true;
            }
            if (!hasNcz) throw new InvalidDataException("O pacote não contém NCZ.");
            return entries;
        }

        private static List<Entry> ReadEntries(Stream input)
        {
            if (!input.CanRead || !input.CanSeek) throw new NotSupportedException("A origem precisa permitir leitura e busca.");
            input.Position = 0;
            byte[] header = new byte[0x10];
            input.ReadExactly(header);
            if (!header.AsSpan(0, 4).SequenceEqual("PFS0"u8)) throw new InvalidDataException("Arquivo não é PFS0.");
            int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            int namesSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            if (count <= 0 || count > 16384 || namesSize <= 0 || namesSize > 16 * 1024 * 1024)
                throw new InvalidDataException("Metadados PFS0 inválidos ou excessivos.");
            long dataBase = checked(0x10L + count * 0x18L + namesSize);
            if (dataBase > input.Length) throw new InvalidDataException("Cabeçalho PFS0 truncado.");
            byte[] table = new byte[count * 0x18], names = new byte[namesSize];
            input.ReadExactly(table);
            input.ReadExactly(names);
            List<Entry> entries = new(count);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            UTF8Encoding utf8 = new(false, true);
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> item = table.AsSpan(i * 0x18, 0x18);
                long offset = BinaryPrimitives.ReadInt64LittleEndian(item);
                long size = BinaryPrimitives.ReadInt64LittleEndian(item[8..]);
                int nameOffset = BinaryPrimitives.ReadInt32LittleEndian(item[16..]);
                if (offset < 0 || size < 0 || offset > input.Length - dataBase || size > input.Length - dataBase - offset ||
                    nameOffset < 0 || nameOffset >= names.Length)
                    throw new InvalidDataException("Entrada PFS0 fora dos limites.");
                int nameEnd = Array.IndexOf(names, (byte)0, nameOffset);
                if (nameEnd <= nameOffset) throw new InvalidDataException("Nome PFS0 inválido.");
                string name = utf8.GetString(names, nameOffset, nameEnd - nameOffset);
                if (name.IndexOfAny(['/', '\\', '\0']) >= 0 || !seen.Add(name))
                    throw new InvalidDataException("Nome PFS0 inválido ou duplicado.");
                entries.Add(new Entry { Name = name, Offset = dataBase + offset, Size = size });
            }
            long end = dataBase;
            foreach (Entry e in entries.Where(e => e.Size > 0).OrderBy(e => e.Offset))
            {
                if (e.Offset < end) throw new InvalidDataException("Entradas PFS0 sobrepostas.");
                end = e.Offset + e.Size;
            }
            return entries;
        }

        private static void ParseNcz(Stream input, Entry e)
        {
            if (e.Size < HeaderSize + 0x10 + 0x40 + 8) throw new InvalidDataException("NCZ truncado.");
            input.Position = e.Offset + HeaderSize;
            byte[] header = new byte[0x10];
            input.ReadExactly(header);
            if (!header.AsSpan(0, 8).SequenceEqual("NCZSECTN"u8)) throw new InvalidDataException("NCZSECTN ausente.");
            long count = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
            if (count <= 0 || count > 0x100) throw new InvalidDataException("Seções NCZ inválidas.");
            e.PrefixSize = HeaderSize + 0x10 + count * 0x40;
            if (e.PrefixSize + 8 > e.Size) throw new InvalidDataException("Seções NCZ truncadas.");
            byte[] sections = new byte[(int)count * 0x40];
            input.ReadExactly(sections);
            long expected = HeaderSize;
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> section = sections.AsSpan(i * 0x40, 0x40);
                long offset = BinaryPrimitives.ReadInt64LittleEndian(section);
                long size = BinaryPrimitives.ReadInt64LittleEndian(section[8..]);
                if (offset < expected || size < 0) throw new InvalidDataException("Seções NCZ inválidas ou sobrepostas.");
                expected = checked(offset + size);
            }
            byte[] magic = new byte[8];
            input.ReadExactly(magic);
            e.Convert = !magic.AsSpan().SequenceEqual("NCZBLOCK"u8);
            e.BodySize = expected - HeaderSize;
            if (e.BodySize <= 0) throw new InvalidDataException("Corpo NCZ vazio.");
            if (!e.Convert) ValidateBlockTable(input, e);
            else _ = BlockCount(e.BodySize);
        }

        private static int BlockCount(long size)
        {
            long count = checked((size + BlockSize - 1) / BlockSize);
            if (count <= 0 || count > 4 * 1024 * 1024) throw new InvalidDataException("Quantidade de blocos excessiva.");
            return (int)count;
        }

        // Reads metadata only: detecting an already optimized file never creates the playback cache.
        private static (int BlockSize, int[] Sizes) ValidateBlockTable(Stream input, Entry e)
        {
            input.Position = e.Offset + e.PrefixSize;
            byte[] header = new byte[0x18];
            if (e.PrefixSize + header.Length > e.Size) throw new InvalidDataException("NCZBLOCK truncado.");
            input.ReadExactly(header);
            int exponent = header[11];
            int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
            long bodySize = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
            if (!header.AsSpan(0, 8).SequenceEqual("NCZBLOCK"u8) || header[8] != 2 || header[9] != 1 ||
                exponent < 14 || exponent > 30 || bodySize < e.BodySize || count <= 0 || count > 4 * 1024 * 1024 ||
                checked((bodySize + (1L << exponent) - 1) / (1L << exponent)) != count ||
                e.PrefixSize + 0x18 + count * 4L > e.Size)
                throw new InvalidDataException("Metadados NCZBLOCK inválidos.");
            e.BodySize = bodySize;
            byte[] table = new byte[count * 4];
            input.ReadExactly(table);
            int[] sizes = new int[count];
            long cursor = e.PrefixSize + 0x18 + table.Length;
            for (int i = 0; i < count; i++)
            {
                int packed = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * 4));
                long raw = Math.Min(1L << exponent, bodySize - (long)i * (1L << exponent));
                if (packed <= 0 || packed > raw || packed > e.Size - cursor)
                    throw new InvalidDataException("Tamanho de bloco NCZ inválido.");
                sizes[i] = packed;
                cursor += packed;
            }
            if (cursor != e.Size) throw new InvalidDataException("Comprimento NCZBLOCK diverge da tabela.");
            return (1 << exponent, sizes);
        }

        private static void ConvertSolid(Stream input, Stream output, Entry e, Compressor compressor)
        {
            e.PrefixHash = CopyAndHash(input, e.Offset, e.PrefixSize, output, false);
            int count = BlockCount(e.BodySize);
            byte[] header = new byte[0x18], sizes = new byte[count * 4];
            "NCZBLOCK"u8.CopyTo(header);
            header[8] = 2; header[9] = 1; header[11] = 20;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), count);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), e.BodySize);
            output.Write(header);
            long tablePosition = output.Position;
            output.Write(sizes);
            using LimitedStream frame = new(input, e.Offset + e.PrefixSize, e.Size - e.PrefixSize);
            using DecompressionStream decoded = new(frame);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] raw = new byte[BlockSize], packed = new byte[Compressor.GetCompressBound(BlockSize)];
            long remaining = e.BodySize;
            for (int i = 0; i < count; i++)
            {
                CheckCanceled();
                int length = (int)Math.Min(BlockSize, remaining);
                decoded.ReadExactly(raw.AsSpan(0, length));
                hash.AppendData(raw, 0, length);
                int written = compressor.Wrap(raw.AsSpan(0, length), packed.AsSpan());
                bool compressed = written > 0 && written < length;
                int stored = compressed ? written : length;
                output.Write(compressed ? packed.AsSpan(0, stored) : raw.AsSpan(0, stored));
                BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan(i * 4), stored);
                remaining -= length;
                Advance(length);
            }
            if (decoded.ReadByte() != -1) throw new InvalidDataException("NCZ contém dados descomprimidos excedentes.");
            e.Hash = hash.GetHashAndReset();
            long end = output.Position;
            output.Position = tablePosition;
            output.Write(sizes);
            output.Position = end;
        }

        private static void Verify(Stream output, List<Entry> source)
        {
            List<Entry> destination = ReadPlan(output);
            if (destination.Count != source.Count) throw new InvalidDataException("Quantidade de entradas diverge.");
            long expectedLength = PfsHeaderSize(source);
            for (int i = 0; i < source.Count; i++)
            {
                CheckCanceled();
                Entry src = source[i], dst = destination[i];
                expectedLength = checked(expectedLength + src.OutputSize);
                if (src.Name != dst.Name || src.OutputSize != dst.Size) throw new InvalidDataException("Entrada de destino diverge.");
                byte[] actual;
                if (src.Convert)
                {
                    if (dst.Convert || dst.BodySize != src.BodySize || dst.PrefixSize != src.PrefixSize)
                        throw new InvalidDataException("Destino NCZBLOCK diverge.");
                    byte[] prefix = CopyAndHash(output, dst.Offset, dst.PrefixSize, null, false);
                    if (!CryptographicOperations.FixedTimeEquals(prefix, src.PrefixHash))
                        throw new InvalidDataException("Prefixo NCZ alterado.");
                    actual = HashBlockBody(output, dst);
                }
                else actual = CopyAndHash(output, dst.Offset, dst.Size, null, true);
                if (!CryptographicOperations.FixedTimeEquals(actual, src.Hash)) throw new InvalidDataException("Verificação SHA-256 falhou.");
            }
            if (output.Length != expectedLength) throw new InvalidDataException("Comprimento NSZ incorreto.");
        }

        private static byte[] HashBlockBody(Stream input, Entry e)
        {
            (int blockSize, int[] sizes) = ValidateBlockTable(input, e);
            // Converted output is always 1 MiB; cap verification allocations to that expected format.
            if (blockSize != BlockSize) throw new InvalidDataException("Blocos do destino têm tamanho inesperado.");
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] raw = new byte[BlockSize];
            long remaining = e.BodySize;
            foreach (int packedSize in sizes)
            {
                CheckCanceled();
                int length = (int)Math.Min(blockSize, remaining);
                byte[] packed = new byte[packedSize];
                input.ReadExactly(packed);
                if (packedSize == length) hash.AppendData(packed);
                else
                {
                    using MemoryStream frame = new(packed, false);
                    using DecompressionStream decoded = new(frame);
                    decoded.ReadExactly(raw.AsSpan(0, length));
                    if (decoded.ReadByte() != -1) throw new InvalidDataException("Bloco descomprimido excede o tamanho esperado.");
                    hash.AppendData(raw, 0, length);
                }
                remaining -= length;
                Advance(length);
            }
            return hash.GetHashAndReset();
        }

        private static byte[] CopyAndHash(Stream input, long offset, long size, Stream output, bool progress)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[BlockSize];
            input.Position = offset;
            while (size > 0)
            {
                CheckCanceled();
                int count = (int)Math.Min(buffer.Length, size);
                input.ReadExactly(buffer.AsSpan(0, count));
                hash.AppendData(buffer, 0, count);
                output?.Write(buffer, 0, count);
                size -= count;
                if (progress) Advance(count);
            }
            return hash.GetHashAndReset();
        }

        private static int PfsHeaderSize(List<Entry> entries) => checked((0x10 + entries.Count * 0x18 +
            entries.Sum(e => Encoding.UTF8.GetByteCount(e.Name) + 1) + 31) & ~31);

        private static byte[] BuildHeader(List<Entry> entries, int size)
        {
            byte[] header = new byte[size];
            "PFS0"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), entries.Count);
            int strings = 0x10 + entries.Count * 0x18;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), size - strings);
            long dataOffset = 0;
            int nameOffset = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                Span<byte> entry = header.AsSpan(0x10 + i * 0x18, 0x18);
                BinaryPrimitives.WriteInt64LittleEndian(entry, dataOffset);
                BinaryPrimitives.WriteInt64LittleEndian(entry[8..], entries[i].OutputSize);
                BinaryPrimitives.WriteInt32LittleEndian(entry[16..], nameOffset);
                nameOffset += Encoding.UTF8.GetBytes(entries[i].Name, header.AsSpan(strings + nameOffset)) + 1;
                dataOffset = checked(dataOffset + entries[i].OutputSize);
            }
            return header;
        }

        private static void CheckCanceled() { if (_cancel) throw new OperationCanceledException(); }
        private static void Advance(int bytes) => Interlocked.Add(ref _done, bytes);

        private sealed class LimitedStream(Stream input, long start, long length) : Stream
        {
            private long _position;
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override int Read(Span<byte> buffer)
            {
                CheckCanceled();
                int count = (int)Math.Min(buffer.Length, length - _position);
                if (count <= 0) return 0;
                input.Position = start + _position;
                int read = input.Read(buffer[..count]);
                if (read <= 0) throw new EndOfStreamException();
                _position += read;
                return read;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
