using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;
using Path = System.IO.Path;

namespace Ryujinx.HLE.FileSystem
{
    /// <summary>
    /// NEOV2-NCZ1: leitura de NSZ/XCZ.
    /// O container (PFS0 do NSZ ou particao secure do XCZ) e reconstruido como um PFS0 virtual
    /// em que cada "xxx.ncz" aparece como "xxx.nca" com o conteudo original (descomprimido e recriptografado).
    /// O resto do emulador continua enxergando um NSP comum.
    /// </summary>
    public static class NczSupport
    {
        public static bool IsXciExtension(string extension)
        {
            string ext = (extension ?? string.Empty).TrimStart('.').ToLowerInvariant();

            return ext is "xci" or "xcz";
        }

        /// <summary>Converte "nsz" em "nsp" e "xcz" em "xci" (com ou sem ponto, preserva o ponto).</summary>
        public static string NormalizeExtension(string extension)
        {
            if (extension == null)
            {
                return null;
            }

            bool hasDot = extension.StartsWith('.');
            string ext = extension.TrimStart('.').ToLowerInvariant();

            ext = ext switch
            {
                "nsz" => "nsp",
                "xcz" => "xci",
                _ => ext,
            };

            return hasDot ? "." + ext : ext;
        }

        public static bool HasNczEntries(IFileSystem fileSystem)
        {
            if (fileSystem == null)
            {
                return false;
            }

            foreach (DirectoryEntryEx entry in fileSystem.EnumerateEntries("/", "*"))
            {
                if (entry.Type == DirectoryEntryType.File && entry.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static IFileSystem WrapIfCompressed(IFileSystem source)
        {
            return TryWrap(source, out PartitionFileSystem wrapped) ? wrapped : source;
        }

        public static bool TryWrap(IFileSystem source, out PartitionFileSystem wrapped)
        {
#if !ANDROID
            return DesktopNczSupport.TryWrap(source, out wrapped);
#else
            wrapped = null;

            if (!HasNczEntries(source))
            {
                return false;
            }

            List<VirtualPfsFile.Entry> entries = [];
            int nczCount = 0;

            foreach (DirectoryEntryEx entry in source.EnumerateEntries("/", "*"))
            {
                if (entry.Type != DirectoryEntryType.File)
                {
                    continue;
                }

                using UniqueRef<IFile> fileRef = new();

                source.OpenFile(ref fileRef.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                IFile file = fileRef.Release();

                if (entry.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase))
                {
                    NczFile ncz = new(file, entry.Name);
                    string ncaName = entry.Name.Substring(0, entry.Name.Length - 4) + ".nca";

                    entries.Add(new VirtualPfsFile.Entry(ncaName, ncz, ncz.NcaSize));
                    nczCount++;
                }
                else
                {
                    entries.Add(new VirtualPfsFile.Entry(entry.Name, file, entry.Size));
                }
            }

            VirtualPfsFile pfsFile = new(entries);
            PartitionFileSystem pfs = new();

            pfs.Initialize(pfsFile.AsStorage()).ThrowIfFailure();

            Logger.Info?.Print(LogClass.Loader, $"[NEOV2-NCZ1] Compressed container opened: {nczCount} NCZ, {entries.Count} files");

            wrapped = pfs;

            return true;
#endif
        }
    }

    /// <summary>
    /// NEOV2-NCZ1: arquivo PFS0 montado em memoria (cabecalho) + arquivos de origem lidos sob demanda.
    /// </summary>
    internal sealed class VirtualPfsFile : IFile
    {
        internal readonly struct Entry
        {
            public readonly string Name;
            public readonly IFile File;
            public readonly long Size;

            public Entry(string name, IFile file, long size)
            {
                Name = name;
                File = file;
                Size = size;
            }
        }

        private readonly byte[] _header;
        private readonly Entry[] _entries;
        private readonly long[] _starts;
        private readonly long _totalSize;

        public override void Dispose()
        {
            foreach (Entry entry in _entries) entry.File.Dispose();
            base.Dispose();
        }

        public VirtualPfsFile(List<Entry> entries)
        {
            _entries = entries.ToArray();

            using MemoryStream names = new();
            int[] nameOffsets = new int[_entries.Length];

            for (int i = 0; i < _entries.Length; i++)
            {
                nameOffsets[i] = (int)names.Length;

                byte[] nameBytes = Encoding.UTF8.GetBytes(_entries[i].Name);

                names.Write(nameBytes, 0, nameBytes.Length);
                names.WriteByte(0);
            }

            int headerBase = 0x10 + (0x18 * _entries.Length);
            int stringTableSize = (int)names.Length;
            int totalHeader = headerBase + stringTableSize;
            int alignedHeader = (totalHeader + 0x1F) & ~0x1F;

            stringTableSize += alignedHeader - totalHeader;

            _header = new byte[alignedHeader];

            Span<byte> h = _header;

            Encoding.ASCII.GetBytes("PFS0").CopyTo(h);
            BinaryPrimitives.WriteInt32LittleEndian(h[0x4..], _entries.Length);
            BinaryPrimitives.WriteInt32LittleEndian(h[0x8..], stringTableSize);

            _starts = new long[_entries.Length];

            long dataOffset = 0;

            for (int i = 0; i < _entries.Length; i++)
            {
                Span<byte> e = h.Slice(0x10 + (0x18 * i), 0x18);

                BinaryPrimitives.WriteInt64LittleEndian(e, dataOffset);
                BinaryPrimitives.WriteInt64LittleEndian(e[0x8..], _entries[i].Size);
                BinaryPrimitives.WriteInt32LittleEndian(e[0x10..], nameOffsets[i]);

                _starts[i] = _header.Length + dataOffset;
                dataOffset += _entries[i].Size;
            }

            names.ToArray().CopyTo(h[headerBase..]);

            _totalSize = _header.Length + dataOffset;
        }

        protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination, in ReadOption option)
        {
            bytesRead = 0;

            if (offset >= _totalSize || destination.Length == 0)
            {
                return Result.Success;
            }

            long toRead = Math.Min(destination.Length, _totalSize - offset);
            long position = offset;
            int written = 0;

            while (written < toRead)
            {
                int remaining = (int)(toRead - written);

                if (position < _header.Length)
                {
                    int count = (int)Math.Min(remaining, _header.Length - position);

                    _header.AsSpan((int)position, count).CopyTo(destination.Slice(written, count));

                    position += count;
                    written += count;

                    continue;
                }

                int index = FindEntry(position);
                Entry entry = _entries[index];
                long inFile = position - _starts[index];
                int chunk = (int)Math.Min(remaining, entry.Size - inFile);

                if (chunk <= 0)
                {
                    break;
                }

                Result rc = entry.File.Read(out long fileRead, inFile, destination.Slice(written, chunk));

                if (rc.IsFailure())
                {
                    return rc;
                }

                if (fileRead <= 0)
                {
                    break;
                }

                position += fileRead;
                written += (int)fileRead;
            }

            bytesRead = written;

            return Result.Success;
        }

        private int FindEntry(long position)
        {
            int lo = 0;
            int hi = _entries.Length - 1;

            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;

                if (_starts[mid] <= position)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            // Pula arquivos de tamanho zero.
            while (lo < _entries.Length - 1 && position >= _starts[lo] + _entries[lo].Size)
            {
                lo++;
            }

            return lo;
        }

        protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option) => throw new NotSupportedException();

        protected override Result DoFlush() => Result.Success;

        protected override Result DoSetSize(long size) => throw new NotSupportedException();

        protected override Result DoGetSize(out long size)
        {
            size = _totalSize;

            return Result.Success;
        }

        protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer) => throw new NotSupportedException();
    }

    /// <summary>
    /// NEOV2-NCZ1: NCZ exposto como o NCA original.
    /// Layout NCZ: 0x4000 bytes do NCA intactos, "NCZSECTN" com as secoes, opcional "NCZBLOCK", dados zstd.
    /// Os dados descomprimidos equivalem ao NCA a partir de 0x4000, com as secoes decriptadas;
    /// aqui sao recriptografados em AES-CTR (tipos 3 e 4), como o nsz faz na descompressao.
    /// Modo bloco: leitura aleatoria por bloco. Modo solido: descomprime uma vez para um cache em disco.
    /// </summary>
    internal sealed class NczFile : IFile
    {
        private const int UncompressableHeaderSize = 0x4000;
        private const int BlockCacheCapacity = 8;

        private readonly struct Section
        {
            public readonly long Offset;
            public readonly long Size;
            public readonly long CryptoType;
            public readonly byte[] Key;
            public readonly byte[] Counter;

            public Section(long offset, long size, long cryptoType, byte[] key, byte[] counter)
            {
                Offset = offset;
                Size = size;
                CryptoType = cryptoType;
                Key = key;
                Counter = counter;
            }
        }

        private readonly object _lock = new();
        private readonly IFile _base;
        private readonly string _name;
        private readonly long _baseSize;
        private readonly byte[] _header;
        private readonly Section[] _sections;
        private readonly Aes[] _aes;
        private readonly long _dataOffset;

        // Modo bloco
        private readonly bool _isBlock;
        private int _blockSize;
        private long[] _blockOffsets;
        private int[] _blockCompressedSizes;
        private readonly Dictionary<int, byte[]> _blockCache = new();
        private readonly LinkedList<int> _blockOrder = new();
        private Decompressor _decompressor;

        // Modo solido
        private System.IO.FileStream _solidCache;
        private static readonly object SolidCacheLock = new();
        private static readonly Dictionary<string, int> ActiveSolidCaches = new();
        private string _solidCachePath;
        private bool _disposed;
        private const long SolidCacheBudget = 4L * 1024 * 1024 * 1024;
        private long _cacheHits;
        private long _cacheMisses;

        public long NcaSize { get; }

        // Offline export only. Read solid data sequentially rather than creating the playback
        // cache, and stream block frames without retaining full decompressed blocks in RAM.
        internal void ExportNca(Stream output, Action checkCanceled, Action<int> progress)
        {
            checkCanceled();
            long previousEnd = UncompressableHeaderSize;
            foreach (Section section in _sections)
            {
                long end = checked(section.Offset + section.Size);
                if (section.Offset < previousEnd || end > NcaSize || section.CryptoType is not (0 or 1 or 3 or 4))
                    throw new InvalidDataException("NCZ sections overlap or use unsupported encryption.");
                previousEnd = end;
            }
            if (NcaSize < UncompressableHeaderSize) throw new InvalidDataException("NCZ output size is invalid.");
            output.Write(_header);
            progress(_header.Length);
            byte[] buffer = new byte[1 << 20];
            long position = UncompressableHeaderSize;

            void CopyBody(Stream body, long length)
            {
                while (length > 0)
                {
                    checkCanceled();
                    int count = (int)Math.Min(buffer.Length, length);
                    body.ReadExactly(buffer.AsSpan(0, count));
                    Encrypt(position, buffer.AsSpan(0, count));
                    output.Write(buffer, 0, count);
                    position += count;
                    length -= count;
                    progress(count);
                }
                if (body.ReadByte() != -1) throw new InvalidDataException("NCZ contains extra decompressed data.");
            }

            if (_isBlock)
            {
                for (int i = 0; i < _blockOffsets.Length; i++)
                {
                    checkCanceled();
                    long length = Math.Min(_blockSize, NcaSize - position);
                    using IFileReadStream frame = new(_base, _blockOffsets[i], _blockOffsets[i] + _blockCompressedSizes[i]);
                    if (_blockCompressedSizes[i] == length) CopyBody(frame, length);
                    else
                    {
                        using DecompressionStream decoded = new(frame);
                        CopyBody(decoded, length);
                    }
                }
            }
            else
            {
                using IFileReadStream frame = new(_base, _dataOffset, _baseSize);
                using DecompressionStream decoded = new(frame);
                CopyBody(decoded, NcaSize - position);
            }
        }

        public NczFile(IFile baseFile, string name)
        {
            _base = baseFile;
            _name = name;

            _base.GetSize(out _baseSize).ThrowIfFailure();

            _header = new byte[UncompressableHeaderSize];
            ReadBaseExact(0, _header);

            byte[] sectHeader = new byte[0x10];
            ReadBaseExact(UncompressableHeaderSize, sectHeader);

            if (Encoding.ASCII.GetString(sectHeader, 0, 8) != "NCZSECTN")
            {
                throw new InvalidDataException($"NCZ invalido (sem NCZSECTN): {name}");
            }

            long sectionCount = BinaryPrimitives.ReadInt64LittleEndian(sectHeader.AsSpan(8));

            if (sectionCount <= 0 || sectionCount > 0x100)
            {
                throw new InvalidDataException($"NCZ invalido (secoes={sectionCount}): {name}");
            }

            byte[] sectionData = new byte[sectionCount * 0x40];
            ReadBaseExact(UncompressableHeaderSize + 0x10, sectionData);

            _sections = new Section[sectionCount];
            _aes = new Aes[sectionCount];

            long expected = UncompressableHeaderSize;

            for (int i = 0; i < sectionCount; i++)
            {
                ReadOnlySpan<byte> s = sectionData.AsSpan(i * 0x40, 0x40);

                long offset = BinaryPrimitives.ReadInt64LittleEndian(s);
                long size = BinaryPrimitives.ReadInt64LittleEndian(s[0x8..]);
                long cryptoType = BinaryPrimitives.ReadInt64LittleEndian(s[0x10..]);

                if (offset < UncompressableHeaderSize || size < 0)
                {
                    throw new InvalidDataException($"NCZ com secao invalida: {name}");
                }

                _sections[i] = new Section(offset, size, cryptoType, s.Slice(0x20, 0x10).ToArray(), s.Slice(0x30, 0x10).ToArray());

                if (cryptoType is 3 or 4)
                {
                    Aes aes = Aes.Create();
                    aes.Key = _sections[i].Key;
                    _aes[i] = aes;
                }

                expected = Math.Max(expected, offset + size);
            }

            long position = UncompressableHeaderSize + 0x10 + sectionData.Length;
            byte[] blockHeader = new byte[0x18];

            if (position + blockHeader.Length <= _baseSize)
            {
                ReadBaseExact(position, blockHeader);
            }

            if (Encoding.ASCII.GetString(blockHeader, 0, 8) == "NCZBLOCK")
            {
                int exponent = blockHeader[0xB];
                int blockCount = BinaryPrimitives.ReadInt32LittleEndian(blockHeader.AsSpan(0xC));
                long decompressedSize = BinaryPrimitives.ReadInt64LittleEndian(blockHeader.AsSpan(0x10));

                if (exponent < 14 || exponent > 30 || blockCount <= 0 || UncompressableHeaderSize + decompressedSize < expected)
                {
                    throw new InvalidDataException($"NCZBLOCK invalido: {name}");
                }
                if (decompressedSize <= 0 || blockCount != ((decompressedSize - 1) / (1L << exponent)) + 1 ||
                    blockCount > (_baseSize - position - 0x18) / 4)
                    throw new InvalidDataException($"NCZBLOCK table invalid: {name}");

                byte[] sizes = new byte[blockCount * 4];
                ReadBaseExact(position + 0x18, sizes);

                _isBlock = true;
                _blockSize = 1 << exponent;
                _blockCompressedSizes = new int[blockCount];
                _blockOffsets = new long[blockCount];

                long dataStart = position + 0x18 + sizes.Length;
                long current = dataStart;

                for (int i = 0; i < blockCount; i++)
                {
                    _blockCompressedSizes[i] = BinaryPrimitives.ReadInt32LittleEndian(sizes.AsSpan(i * 4));
                    long rawSize = Math.Min(_blockSize, decompressedSize - (long)i * _blockSize);
                    if (_blockCompressedSizes[i] <= 0 || _blockCompressedSizes[i] > rawSize ||
                        _blockCompressedSizes[i] > _baseSize - current)
                        throw new InvalidDataException($"NCZBLOCK size invalid: {name} block={i}");
                    _blockOffsets[i] = current;
                    current += _blockCompressedSizes[i];
                }

                _dataOffset = dataStart;
                NcaSize = UncompressableHeaderSize + decompressedSize;
            }
            else
            {
                _isBlock = false;
                _dataOffset = position;
                NcaSize = expected;
            }
            Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Open {_name}: mode={(_isBlock ? "NCZBLOCK" : "solid")} compressed={_baseSize} nca={NcaSize}");
        }

        private void ReadBaseExact(long offset, Span<byte> destination)
        {
            int done = 0;

            while (done < destination.Length)
            {
                _base.Read(out long read, offset + done, destination[done..]).ThrowIfFailure();

                if (read <= 0)
                {
                    throw new EndOfStreamException($"NCZ truncado: {_name}");
                }

                done += (int)read;
            }
        }

        protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination, in ReadOption option)
        {
            bytesRead = 0;

            if (offset >= NcaSize || destination.Length == 0)
            {
                return Result.Success;
            }

            int toRead = (int)Math.Min(destination.Length, NcaSize - offset);
            Span<byte> output = destination[..toRead];

            lock (_lock)
            {
                long position = offset;
                int written = 0;

                if (position < UncompressableHeaderSize)
                {
                    int count = (int)Math.Min(toRead, UncompressableHeaderSize - position);

                    _header.AsSpan((int)position, count).CopyTo(output);

                    position += count;
                    written += count;
                }

                if (written < toRead)
                {
                    Span<byte> body = output[written..];

                    ReadDecompressed(position - UncompressableHeaderSize, body);
                    Encrypt(position, body);
                }
            }

            bytesRead = toRead;

            return Result.Success;
        }

        private void ReadDecompressed(long offset, Span<byte> destination)
        {
            if (!_isBlock)
            {
                EnsureSolidCache();
            }

            if (_isBlock || _solidCache != null)
            {
                int done = 0;

                while (done < destination.Length)
                {
                    long current = offset + done;
                    int blockIndex = (int)(current / _blockSize);
                    int inBlock = (int)(current % _blockSize);
                    byte[] block = GetBlock(blockIndex);
                    int count = Math.Min(destination.Length - done, block.Length - inBlock);

                    block.AsSpan(inBlock, count).CopyTo(destination[done..]);
                    done += count;
                }
            }
        }

        private byte[] GetBlock(int index)
        {
            if (_blockCache.TryGetValue(index, out byte[] cached))
            {
                _cacheHits++;
                _blockOrder.Remove(index);
                _blockOrder.AddFirst(index);

                return cached;
            }

            long totalDecompressed = NcaSize - UncompressableHeaderSize;
            _cacheMisses++;
            int decompressedSize = (int)Math.Min(_blockSize, totalDecompressed - ((long)index * _blockSize));
            int compressedSize = _blockCompressedSizes[index];

            byte[] compressed = new byte[compressedSize];
            if (_solidCache != null)
            {
                _solidCache.Position = _blockOffsets[index];
                _solidCache.ReadExactly(compressed);
            }
            else
            {
                ReadBaseExact(_blockOffsets[index], compressed);
            }

            byte[] block;

            if (compressedSize < decompressedSize)
            {
                _decompressor ??= new Decompressor();

                block = new byte[decompressedSize];

                int produced = _decompressor.Unwrap(new ReadOnlySpan<byte>(compressed), new Span<byte>(block));

                if (produced != decompressedSize)
                {
                    throw new InvalidDataException($"NCZ bloco {index} descomprimiu {produced} de {decompressedSize} bytes: {_name}");
                }
            }
            else
            {
                block = compressed;
            }

            _blockCache[index] = block;
            _blockOrder.AddFirst(index);

            if (_blockOrder.Count > BlockCacheCapacity)
            {
                int oldest = _blockOrder.Last!.Value;

                _blockOrder.RemoveLast();
                _blockCache.Remove(oldest);
            }

            return block;
        }

        // Internal cache: NCZBLOCK header/table/data plus SHA-256 sidecar. The source remains untouched.
        private void EnsureSolidCache()
        {
            if (_solidCache != null) return;
            lock (SolidCacheLock)
            {
                string cacheDir = Path.Combine(AppDataManager.BaseDirPath, "ncz_cache");
                System.IO.Directory.CreateDirectory(cacheDir);
                // Full compressed-source identity avoids name/length collisions.
                string identity;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Checking solid source {_name}: bytes={_baseSize}");
                using (IFileReadStream source = new(_base, 0, _baseSize))
                    identity = Convert.ToHexString(SHA256.HashData(source));
                string path = Path.Combine(cacheDir, identity + ".ncb");
                string digestPath = path + ".sha256";
                foreach (string stale in System.IO.Directory.GetFiles(cacheDir, identity + ".*.part*"))
                    TryDeleteCache(stale);
                bool reused = false;
                if (System.IO.File.Exists(path) && System.IO.File.Exists(digestPath))
                {
                    try
                    {
                        using System.IO.FileStream candidate = System.IO.File.OpenRead(path);
                        string actual = Convert.ToHexString(SHA256.HashData(candidate));
                        if (!string.Equals(actual, System.IO.File.ReadAllText(digestPath).Trim(), StringComparison.Ordinal))
                            throw new InvalidDataException("Compressed cache checksum mismatch");
                        candidate.Position = 0;
                        LoadSolidBlockTable(candidate);
                        reused = true;
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is OverflowException)
                    {
                        Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Cache rejected {_name}: {ex.GetType().Name}; rebuilding");
                    }
                }
                if (!reused)
                {
                    string part = path + "." + Guid.NewGuid().ToString("N") + ".part";
                    string digestPart = part + ".sha256";
                    try
                    {
                        long total = NcaSize - UncompressableHeaderSize;
                        const int blockSize = 1 << 20;
                        int count = checked((int)((total + blockSize - 1) / blockSize));
                        byte[] header = new byte[0x18];
                        Encoding.ASCII.GetBytes("NCZBLOCK").CopyTo(header, 0);
                        header[8] = 2;
                        header[9] = 1;
                        header[11] = 20;
                        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), count);
                        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), total);
                        byte[] sizes = new byte[checked(count * 4)];
                        Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Solid conversion begin {_name}: bytes={total} blockBytes={blockSize} diskBudget={SolidCacheBudget}");
                        using (System.IO.FileStream output = new(part, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                        using (IFileReadStream input = new(_base, _dataOffset, _baseSize))
                        using (DecompressionStream zstd = new(input))
                        using (Compressor compressor = new(3))
                        {
                            output.Write(header);
                            output.Write(sizes);
                            byte[] raw = new byte[blockSize];
                            byte[] packed = new byte[Compressor.GetCompressBound(blockSize)];
                            long done = 0;
                            int rawBlocks = 0;
                            for (int i = 0; i < count; i++)
                            {
                                int length = (int)Math.Min(blockSize, total - done);
                                zstd.ReadExactly(raw.AsSpan(0, length));
                                int written = compressor.Wrap(raw.AsSpan(0, length), packed.AsSpan());
                                bool compressed = written > 0 && written < length;
                                int stored = compressed ? written : length;
                                output.Write(compressed ? packed.AsSpan(0, stored) : raw.AsSpan(0, stored));
                                BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan(i * 4), stored);
                                if (!compressed) rawBlocks++;
                                done += length;
                                if ((i & 255) == 0)
                                    Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Solid conversion {_name}: processed={done}/{total} stored={output.Length}");
                            }
                            if (zstd.ReadByte() != -1) throw new InvalidDataException("Solid NCZ contains extra decompressed data");
                            output.Position = 0x18;
                            output.Write(sizes);
                            output.Flush(true);
                            output.Position = 0;
                            string digest = Convert.ToHexString(SHA256.HashData(output));
                            System.IO.File.WriteAllText(digestPart, digest);
                            Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Solid conversion complete {_name}: body={total} stored={output.Length} rawBlocks={rawBlocks}/{count}");
                        }
                        System.IO.File.Move(part, path, true);
                        System.IO.File.Move(digestPart, digestPath, true);
                    }
                    finally
                    {
                        System.IO.File.Delete(part);
                        System.IO.File.Delete(digestPart);
                    }
                }
                _solidCache = new System.IO.FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try { LoadSolidBlockTable(_solidCache); }
                catch { _solidCache.Dispose(); _solidCache = null; throw; }
                _solidCachePath = path;
                ActiveSolidCaches.TryGetValue(path, out int users);
                ActiveSolidCaches[path] = users + 1;
                System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Solid cache {_name}: reused={reused} stored={_solidCache.Length} residentLimit={BlockCacheCapacity * (long)_blockSize}");
                Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Solid preparation {_name}: elapsedMs={watch.ElapsedMilliseconds}");
                // Remove only this source's legacy materialized cache, after a verified replacement exists.
                string legacy = Path.Combine(cacheDir, $"{_name}_{_baseSize}.bin");
                TryDeleteCache(legacy);
                TryDeleteCache(legacy + ".part");
                PruneSolidCache(cacheDir, path);
            }
        }

        private void LoadSolidBlockTable(System.IO.FileStream file)
        {
            file.Position = 0;
            byte[] header = new byte[0x18];
            file.ReadExactly(header);
            long total = NcaSize - UncompressableHeaderSize;
            int count = checked((int)((total + (1 << 20) - 1) / (1 << 20)));
            if (Encoding.ASCII.GetString(header, 0, 8) != "NCZBLOCK" || header[8] != 2 || header[9] != 1 || header[11] != 20 ||
                BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)) != count ||
                BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16)) != total)
                throw new InvalidDataException("Invalid solid block cache header");
            byte[] sizes = new byte[checked(count * 4)];
            file.ReadExactly(sizes);
            int[] lengths = new int[count];
            long[] offsets = new long[count];
            long cursor = file.Position;
            for (int i = 0; i < count; i++)
            {
                int size = BinaryPrimitives.ReadInt32LittleEndian(sizes.AsSpan(i * 4));
                long expected = Math.Min(1 << 20, total - ((long)i << 20));
                if (size <= 0 || size > expected || size > file.Length - cursor)
                    throw new InvalidDataException("Invalid solid block cache size");
                lengths[i] = size;
                offsets[i] = cursor;
                cursor += size;
            }
            if (cursor != file.Length) throw new InvalidDataException("Invalid solid block cache length");
            _blockSize = 1 << 20;
            _blockOffsets = offsets;
            _blockCompressedSizes = lengths;
        }

        private static void TryDeleteCache(string path)
        {
            try { System.IO.File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void PruneSolidCache(string directory, string current)
        {
            var files = new System.IO.DirectoryInfo(directory).GetFiles("*.ncb");
            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            long total = 0;
            foreach (var file in files) total += file.Length;
            // A single active title may exceed the budget; never prevent it from loading.
            long budget = Math.Max(SolidCacheBudget, new System.IO.FileInfo(current).Length);
            foreach (var file in files)
            {
                if (total <= budget) break;
                if (file.FullName == current || ActiveSolidCaches.ContainsKey(file.FullName)) continue;
                long size = file.Length;
                TryDeleteCache(file.FullName);
                if (!System.IO.File.Exists(file.FullName))
                {
                    total -= size;
                    TryDeleteCache(file.FullName + ".sha256");
                }
            }
            Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Disk cache bytes={total} budget={budget} defaultBudget={SolidCacheBudget}");
        }

        public override void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                Logger.Notice.Print(LogClass.Loader, $"[NEOV2-NCZ3] Close {_name}: hits={_cacheHits} misses={_cacheMisses} residentBlocks={_blockCache.Count}");
                _solidCache?.Dispose();
                _solidCache = null;
                _decompressor?.Dispose();
                foreach (Aes aes in _aes) aes?.Dispose();
                _blockCache.Clear();
                _blockOrder.Clear();
                _base.Dispose();
                lock (SolidCacheLock)
                {
                    if (_solidCachePath != null && ActiveSolidCaches.TryGetValue(_solidCachePath, out int users))
                    {
                        if (users <= 1) ActiveSolidCaches.Remove(_solidCachePath);
                        else ActiveSolidCaches[_solidCachePath] = users - 1;
                    }
                }
            }
            base.Dispose();
        }

        private void Encrypt(long ncaOffset, Span<byte> data)
        {
            long end = ncaOffset + data.Length;

            for (int i = 0; i < _sections.Length; i++)
            {
                Section section = _sections[i];

                if (_aes[i] == null)
                {
                    continue;
                }

                long start = Math.Max(ncaOffset, section.Offset);
                long stop = Math.Min(end, section.Offset + section.Size);

                if (start >= stop)
                {
                    continue;
                }

                ApplyCtr(_aes[i], section.Counter, start, data.Slice((int)(start - ncaOffset), (int)(stop - start)));
            }
        }

        internal static void ApplyCtr(Aes aes, byte[] sectionCounter, long ncaOffset, Span<byte> data)
        {
            long firstBlock = ncaOffset >> 4;
            int skip = (int)(ncaOffset & 0xF);
            int blockCount = (skip + data.Length + 15) >> 4;

            byte[] counters = new byte[blockCount * 16];

            for (int b = 0; b < blockCount; b++)
            {
                Span<byte> c = counters.AsSpan(b * 16, 16);

                sectionCounter.AsSpan(0, 8).CopyTo(c);
                BinaryPrimitives.WriteInt64BigEndian(c[8..], firstBlock + b);
            }

            byte[] keystream = aes.EncryptEcb(counters, PaddingMode.None);

            for (int i = 0; i < data.Length; i++)
            {
                data[i] ^= keystream[skip + i];
            }
        }

        protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option) => throw new NotSupportedException();

        protected override Result DoFlush() => Result.Success;

        protected override Result DoSetSize(long size) => throw new NotSupportedException();

        protected override Result DoGetSize(out long size)
        {
            size = NcaSize;

            return Result.Success;
        }

        protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer) => throw new NotSupportedException();
    }

    /// <summary>
    /// NEOV2-NCZ1: Stream sequencial (somente leitura) sobre um IFile, a partir de um offset.
    /// </summary>
    internal sealed class IFileReadStream : Stream
    {
        private readonly IFile _file;
        private readonly long _end;
        private long _position;

        public IFileReadStream(IFile file, long start, long end)
        {
            _file = file;
            _position = start;
            _end = end;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, _end - _position);

            if (count <= 0)
            {
                return 0;
            }

            _file.Read(out long read, _position, buffer[..count]).ThrowIfFailure();

            _position += read;

            return (int)read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
