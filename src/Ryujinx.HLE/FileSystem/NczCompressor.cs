using LibHac;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.Common.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZstdSharp;

namespace Ryujinx.HLE.FileSystem
{
    /// <summary>
    /// NEOV2-NCZ2: conversor NSP -> NSZ (NCZ em modo bloco, compativel com "nsz -B").
    /// Cada NCA (exceto .cnmt.nca) vira NCZ: 0x4000 bytes do cabecalho intactos, secoes AES-CTR
    /// decriptadas e comprimidas em blocos de 1 MiB com zstd. Os demais arquivos sao copiados.
    /// Ao final, o arquivo gerado e relido pelo leitor NCZ1 e o SHA-256 de cada arquivo
    /// e comparado com o do original. So retorna sucesso se tudo bater.
    /// </summary>
    public static class NczCompressor
    {
        public const int ResultOk = 0;
        public const int ResultError = 1;
        public const int ResultCanceled = 2;
        public const int ResultNoSavings = 3;

        public const int PhaseIdle = 0;
        public const int PhaseCompressing = 1;
        public const int PhaseVerifying = 2;

        private const int UncompressableHeaderSize = 0x4000;
        private const int BlockSizeExponent = 20;
        private const int BlockSize = 1 << BlockSizeExponent;

        private static long _done;
        private static long _total;
        private static volatile int _phase;
        private static volatile bool _cancel;

        public static int Phase => _phase;

        public static int Permille
        {
            get
            {
                long total = Interlocked.Read(ref _total);

                return total <= 0 ? 0 : (int)Math.Min(1000, Interlocked.Read(ref _done) * 1000 / total);
            }
        }

        public static void Cancel() => _cancel = true;

        private sealed class PlanEntry
        {
            public string SourceName;
            public string OutputName;
            public long SourceSize;
            public bool Compress;
            public long OutputSize;
            public byte[] Hash;
        }

        private sealed class Section
        {
            public long Offset;
            public long Size;
            public long CryptoType;
            public byte[] Key = new byte[16];
            public byte[] Counter = new byte[16];
            public Aes Aes;
        }

        public static int CompressNsp(Stream input, Stream output, VirtualFileSystem virtualFileSystem, int level)
        {
            _cancel = false;
            Interlocked.Exchange(ref _done, 0);
            Interlocked.Exchange(ref _total, 0);
            _phase = PhaseCompressing;

            level = Math.Clamp(level, 1, 22);

            try
            {
                KeySet keySet = virtualFileSystem.KeySet;

                PartitionFileSystem source = new();
                source.Initialize(input.AsStorage()).ThrowIfFailure();

                virtualFileSystem.ImportTickets(source);

                List<PlanEntry> plan = [];
                long totalBytes = 0;
                long originalSize = input.Length;

                foreach (DirectoryEntryEx entry in source.EnumerateEntries("/", "*"))
                {
                    if (entry.Type != DirectoryEntryType.File)
                    {
                        continue;
                    }

                    if (entry.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("O arquivo ja contem NCZ (ja esta comprimido).");
                    }

                    bool compress = entry.Name.EndsWith(".nca", StringComparison.OrdinalIgnoreCase) &&
                                    !entry.Name.EndsWith(".cnmt.nca", StringComparison.OrdinalIgnoreCase) &&
                                    entry.Size > UncompressableHeaderSize;

                    plan.Add(new PlanEntry
                    {
                        SourceName = entry.Name,
                        OutputName = compress ? entry.Name.Substring(0, entry.Name.Length - 4) + ".ncz" : entry.Name,
                        SourceSize = entry.Size,
                        Compress = compress,
                    });

                    totalBytes += entry.Size;
                }

                if (plan.Count == 0)
                {
                    throw new InvalidDataException("NSP vazio.");
                }

                // Compressao + verificacao (le cada arquivo de novo).
                Interlocked.Exchange(ref _total, totalBytes * 2);

                Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Compressing NSP: files={plan.Count} bytes={originalSize} zstdLevel={level}");

                int headerSize = GetPfs0HeaderSize(plan);

                output.SetLength(0);
                output.Position = 0;
                output.Write(new byte[headerSize]);

                foreach (PlanEntry item in plan)
                {
                    ThrowIfCanceled();

                    using UniqueRef<IFile> fileRef = new();

                    source.OpenFile(ref fileRef.Ref, ("/" + item.SourceName).ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    long start = output.Position;

                    item.Hash = item.Compress
                        ? CompressNca(fileRef.Get, item, output, keySet, level)
                        : CopyFile(fileRef.Get, item.SourceSize, output);

                    item.OutputSize = output.Position - start;
                    Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Entry {item.SourceName}: input={item.SourceSize} output={item.OutputSize} mode={(item.Compress ? "NCZBLOCK" : "copy")}");
                }

                output.Position = 0;
                output.Write(BuildPfs0Header(plan, headerSize));
                output.Flush();

                _phase = PhaseVerifying;

                Verify(output, plan);

                long outputSize = output.Length;

                if (outputSize >= originalSize)
                {
                    Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] No savings: input={originalSize} output={outputSize}; original must be preserved");
                    return ResultNoSavings;
                }

                Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] NSZ verified: input={originalSize} output={outputSize} saved={originalSize - outputSize}");

                return ResultOk;
            }
            catch (OperationCanceledException)
            {
                Logger.Info?.Print(LogClass.Loader, "[NEOV2-NCZ2] Compression canceled");

                return ResultCanceled;
            }
            catch (Exception ex)
            {
                Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Compression failed: phase={_phase} failure={ex.GetType().Name} message={ex.Message}; original preserved");

                return ResultError;
            }
            finally
            {
                _phase = PhaseIdle;
            }
        }

        private static void ThrowIfCanceled()
        {
            if (_cancel)
            {
                throw new OperationCanceledException();
            }
        }

        private static void ReadExact(IFile file, long offset, Span<byte> destination)
        {
            int done = 0;

            while (done < destination.Length)
            {
                file.Read(out long read, offset + done, destination[done..]).ThrowIfFailure();

                if (read <= 0)
                {
                    throw new EndOfStreamException("Leitura incompleta do arquivo de origem.");
                }

                done += (int)read;
            }
        }

        private static byte[] CopyFile(IFile file, long size, Stream output)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            byte[] buffer = new byte[BlockSize];
            long position = 0;

            while (position < size)
            {
                ThrowIfCanceled();

                int count = (int)Math.Min(buffer.Length, size - position);

                ReadExact(file, position, buffer.AsSpan(0, count));

                hash.AppendData(buffer, 0, count);
                output.Write(buffer, 0, count);

                position += count;
                Interlocked.Add(ref _done, count);
            }

            return hash.GetHashAndReset();
        }

        private static List<Section> BuildSections(IFile file, long size, KeySet keySet, string name)
        {
            Nca nca = new(keySet, file.AsStorage());

            List<Section> real = [];

            for (int i = 0; i < 4; i++)
            {
                if (!nca.Header.IsSectionEnabled(i))
                {
                    continue;
                }

                long offset = nca.Header.GetSectionStartOffset(i);
                long end = Math.Min(nca.Header.GetSectionEndOffset(i), size);

                if (offset < UncompressableHeaderSize || end <= offset)
                {
                    continue;
                }

                Section section = new() { Offset = offset, Size = end - offset, CryptoType = 1 };

                LibHac.Tools.FsSystem.NcaUtils.NcaFsHeader fsHeader = nca.GetFsHeader(i);
                NcaEncryptionType encryption = fsHeader.EncryptionType;

                // CTR-Ex uses subsection-specific counters. A single CTR transform is not sufficient.
                // Preserve ciphertext as type 1 until those subsection tables are supported.
                if (encryption == NcaEncryptionType.AesCtrEx)
                {
                    Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Section file={name} index={i} bytes={section.Size}: CTR-Ex preserved encrypted (unsupported subsection counters)");
                    real.Add(section);
                    continue;
                }

                if (encryption == NcaEncryptionType.AesCtr || encryption == NcaEncryptionType.AesCtrEx)
                {
                    try
                    {
                        byte[] key = nca.Header.HasRightsId ? nca.GetDecryptedTitleKey() : nca.GetDecryptedKey(2);

                        if (key == null || key.Length != 16)
                        {
                            throw new InvalidDataException($"Invalid content key for section {i}");
                        }

                        if (key.Length == 16)
                        {
                            key.CopyTo(section.Key, 0);
                            BinaryPrimitives.WriteUInt64BigEndian(section.Counter, fsHeader.Counter);

                            section.CryptoType = encryption == NcaEncryptionType.AesCtr ? 3 : 4;
                            section.Aes = Aes.Create();
                            section.Aes.Key = section.Key;

                            // Confere chave e contador com a decriptacao do proprio LibHac (somente AES-CTR simples).
                            if (section.CryptoType == 3 && !CheckSectionDecryption(nca, i, file, section))
                            {
                                throw new InvalidDataException($"CTR validation mismatch for {name} section {i}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        section.Aes?.Dispose();
                        Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Decryption rejected: file={name} section={i} encryption={encryption} failure={ex.GetType().Name}; original preserved");
                        throw new InvalidDataException($"Cannot safely compress {name} section {i}; decryption validation failed.");
                    }
                }

                real.Add(section);
                Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Section file={name} index={i} offset={section.Offset} bytes={section.Size} encryption={encryption} transform={(section.Aes != null ? "CTR" : "none")}");
            }

            real.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            // Secoes contiguas de 0x4000 ate o fim do NCA (lacunas como secoes sem criptografia, igual ao nsz).
            List<Section> sections = [];
            long cursor = UncompressableHeaderSize;

            foreach (Section section in real)
            {
                if (section.Offset < cursor)
                {
                    long overlap = cursor - section.Offset;

                    if (overlap >= section.Size)
                    {
                        continue;
                    }

                    section.Offset = cursor;
                    section.Size -= overlap;
                }

                if (section.Offset > cursor)
                {
                    sections.Add(new Section { Offset = cursor, Size = section.Offset - cursor, CryptoType = 1 });
                }

                sections.Add(section);
                cursor = section.Offset + section.Size;
            }

            if (cursor < size)
            {
                sections.Add(new Section { Offset = cursor, Size = size - cursor, CryptoType = 1 });
            }

            return sections;
        }

        private static bool CheckSectionDecryption(Nca nca, int index, IFile file, Section section)
        {
            try
            {
                int count = (int)Math.Min(0x200, section.Size);
                byte[] mine = new byte[count];
                byte[] reference = new byte[count];

                ReadExact(file, section.Offset, mine);
                NczFile.ApplyCtr(section.Aes, section.Counter, section.Offset, mine);

                nca.OpenRawStorage(index).Read(0, reference).ThrowIfFailure();

                return mine.AsSpan().SequenceEqual(reference);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"LibHac reference read failed for section {index} ({ex.GetType().Name})");
            }
        }

        private static void Transform(List<Section> sections, long ncaOffset, Span<byte> data)
        {
            long end = ncaOffset + data.Length;

            foreach (Section section in sections)
            {
                if (section.Aes == null)
                {
                    continue;
                }

                long start = Math.Max(ncaOffset, section.Offset);
                long stop = Math.Min(end, section.Offset + section.Size);

                if (start >= stop)
                {
                    continue;
                }

                NczFile.ApplyCtr(section.Aes, section.Counter, start, data.Slice((int)(start - ncaOffset), (int)(stop - start)));
            }
        }

        private static byte[] CompressNca(IFile file, PlanEntry item, Stream output, KeySet keySet, int level)
        {
            long size = item.SourceSize;
            List<Section> sections = BuildSections(file, size, keySet, item.SourceName);

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // Cabecalho do NCA (0x4000 bytes, intacto).
            byte[] header = new byte[UncompressableHeaderSize];
            ReadExact(file, 0, header);
            hash.AppendData(header);
            output.Write(header);
            Interlocked.Add(ref _done, header.Length);

            // NCZSECTN
            byte[] sectHeader = new byte[0x10 + (sections.Count * 0x40)];
            Encoding.ASCII.GetBytes("NCZSECTN").CopyTo(sectHeader, 0);
            BinaryPrimitives.WriteInt64LittleEndian(sectHeader.AsSpan(0x8), sections.Count);

            for (int i = 0; i < sections.Count; i++)
            {
                Span<byte> s = sectHeader.AsSpan(0x10 + (i * 0x40), 0x40);

                BinaryPrimitives.WriteInt64LittleEndian(s, sections[i].Offset);
                BinaryPrimitives.WriteInt64LittleEndian(s[0x8..], sections[i].Size);
                BinaryPrimitives.WriteInt64LittleEndian(s[0x10..], sections[i].CryptoType);
                sections[i].Key.CopyTo(s[0x20..]);
                sections[i].Counter.CopyTo(s[0x30..]);
            }

            output.Write(sectHeader);

            // NCZBLOCK
            long decompressedSize = size - UncompressableHeaderSize;
            int blockCount = (int)((decompressedSize + BlockSize - 1) / BlockSize);

            byte[] blockHeader = new byte[0x18];
            Encoding.ASCII.GetBytes("NCZBLOCK").CopyTo(blockHeader, 0);
            blockHeader[0x8] = 2; // versao
            blockHeader[0x9] = 1; // tipo
            blockHeader[0xA] = 0;
            blockHeader[0xB] = BlockSizeExponent;
            BinaryPrimitives.WriteInt32LittleEndian(blockHeader.AsSpan(0xC), blockCount);
            BinaryPrimitives.WriteInt64LittleEndian(blockHeader.AsSpan(0x10), decompressedSize);

            output.Write(blockHeader);

            long sizesPosition = output.Position;
            byte[] sizes = new byte[blockCount * 4];

            output.Write(sizes);

            int batchSize = Math.Max(2, Environment.ProcessorCount * 2);
            byte[][] raw = new byte[batchSize][];
            byte[][] packed = new byte[batchSize][];
            int[] rawLength = new int[batchSize];
            int[] packedLength = new int[batchSize];

            for (int i = 0; i < batchSize; i++)
            {
                raw[i] = new byte[BlockSize];
                packed[i] = new byte[Compressor.GetCompressBound(BlockSize)];
            }

            using ThreadLocal<Compressor> compressors = new(() => new Compressor(level), true);
            int rawBlocks = 0;

            try
            {
                for (int first = 0; first < blockCount; first += batchSize)
                {
                    ThrowIfCanceled();

                    int count = Math.Min(batchSize, blockCount - first);

                    // Leitura sequencial + hash do original + decriptacao.
                    for (int b = 0; b < count; b++)
                    {
                        long ncaOffset = UncompressableHeaderSize + ((long)(first + b) * BlockSize);
                        int length = (int)Math.Min(BlockSize, size - ncaOffset);

                        Span<byte> block = raw[b].AsSpan(0, length);

                        ReadExact(file, ncaOffset, block);
                        hash.AppendData(block);
                        Transform(sections, ncaOffset, block);

                        rawLength[b] = length;
                    }

                    Parallel.For(0, count, b =>
                    {
                        int written = compressors.Value.Wrap(new ReadOnlySpan<byte>(raw[b], 0, rawLength[b]), new Span<byte>(packed[b]));

                        packedLength[b] = written;
                    });

                    for (int b = 0; b < count; b++)
                    {
                        int blockIndex = first + b;

                        if (packedLength[b] > 0 && packedLength[b] < rawLength[b])
                        {
                            output.Write(packed[b], 0, packedLength[b]);
                            BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan(blockIndex * 4), packedLength[b]);
                        }
                        else
                        {
                            rawBlocks++;
                            // Bloco que nao comprime: guardado cru (o leitor reconhece pelo tamanho).
                            output.Write(raw[b], 0, rawLength[b]);
                            BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan(blockIndex * 4), rawLength[b]);
                        }

                        Interlocked.Add(ref _done, rawLength[b]);
                    }
                }
            }
            finally
            {
                foreach (Compressor compressor in compressors.Values)
                {
                    compressor.Dispose();
                }
                foreach (Section section in sections) section.Aes?.Dispose();
            }

            long end = output.Position;

            output.Position = sizesPosition;
            output.Write(sizes);
            output.Position = end;

            Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Blocks file={item.SourceName} total={blockCount} raw={rawBlocks} zstd={blockCount - rawBlocks}");

            return hash.GetHashAndReset();
        }

        private static void Verify(Stream output, List<PlanEntry> plan)
        {
            PartitionFileSystem written = new();
            written.Initialize(output.AsStorage()).ThrowIfFailure();

            IFileSystem view = NczSupport.WrapIfCompressed(written);

            byte[] buffer = new byte[BlockSize];

            foreach (PlanEntry item in plan)
            {
                ThrowIfCanceled();

                using UniqueRef<IFile> fileRef = new();

                view.OpenFile(ref fileRef.Ref, ("/" + item.SourceName).ToU8Span(), OpenMode.Read).ThrowIfFailure();

                fileRef.Get.GetSize(out long size).ThrowIfFailure();

                if (size != item.SourceSize)
                {
                    throw new InvalidDataException($"Verificacao falhou (tamanho) em {item.SourceName}: {size} != {item.SourceSize}");
                }

                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                long position = 0;

                while (position < size)
                {
                    ThrowIfCanceled();

                    int count = (int)Math.Min(buffer.Length, size - position);

                    ReadExact(fileRef.Get, position, buffer.AsSpan(0, count));
                    hash.AppendData(buffer, 0, count);

                    position += count;
                    Interlocked.Add(ref _done, count);
                }

                if (!hash.GetHashAndReset().AsSpan().SequenceEqual(item.Hash))
                {
                    throw new InvalidDataException($"Verificacao falhou (SHA-256) em {item.SourceName}");
                }
            }
        }

        private static int GetPfs0HeaderSize(List<PlanEntry> plan)
        {
            int names = 0;

            foreach (PlanEntry item in plan)
            {
                names += Encoding.UTF8.GetByteCount(item.OutputName) + 1;
            }

            int total = 0x10 + (0x18 * plan.Count) + names;

            return (total + 0x1F) & ~0x1F;
        }

        private static byte[] BuildPfs0Header(List<PlanEntry> plan, int headerSize)
        {
            byte[] header = new byte[headerSize];
            Span<byte> h = header;

            int tableStart = 0x10 + (0x18 * plan.Count);

            Encoding.ASCII.GetBytes("PFS0").CopyTo(h);
            BinaryPrimitives.WriteInt32LittleEndian(h[0x4..], plan.Count);
            BinaryPrimitives.WriteInt32LittleEndian(h[0x8..], headerSize - tableStart);

            long dataOffset = 0;
            int nameOffset = 0;

            for (int i = 0; i < plan.Count; i++)
            {
                Span<byte> e = h.Slice(0x10 + (0x18 * i), 0x18);

                BinaryPrimitives.WriteInt64LittleEndian(e, dataOffset);
                BinaryPrimitives.WriteInt64LittleEndian(e[0x8..], plan[i].OutputSize);
                BinaryPrimitives.WriteInt32LittleEndian(e[0x10..], nameOffset);

                byte[] name = Encoding.UTF8.GetBytes(plan[i].OutputName);
                name.CopyTo(h[(tableStart + nameOffset)..]);

                nameOffset += name.Length + 1;
                dataOffset += plan[i].OutputSize;
            }

            return header;
        }
    }
}
