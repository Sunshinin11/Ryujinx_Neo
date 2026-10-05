using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using Ryujinx.Common.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Ryujinx.HLE.FileSystem
{
    /// <summary>Offline NSZ to NSP and standalone NCZ to NCA. Does not modify the input.</summary>
    public static class NczDecompressor
    {
        public const int PhaseDecompressing = 3;
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
            internal string SourceName, OutputName;
            internal long Size;
            internal byte[] Hash;
            internal bool Compressed;
        }

        public static long GetExpectedSize(Stream input, bool standalone)
        {
            try
            {
                if (!input.CanRead || !input.CanSeek) throw new NotSupportedException("A origem precisa permitir leitura e busca.");
                if (standalone)
                {
                    using NczFile file = OwnNcz(new ReadOnlyStreamFile(input), "standalone.ncz");
                    return file.NcaSize;
                }
                using PartitionFileSystem source = OpenPfs(input);
                List<Entry> plan = CreatePlan(source);
                return checked(HeaderSize(plan) + plan.Sum(entry => entry.Size));
            }
            catch (Exception exception)
            {
                Logger.Error?.Print(LogClass.Loader, $"[NEO-QOL1-DECOMPRESS] Cannot inspect source: {exception.Message}");
                return -1;
            }
        }

        public static int Decompress(Stream input, Stream output, bool standalone, string sourceName = "standalone.ncz")
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return NczCompressor.ResultError;
            _cancel = false;
            _phase = PhaseDecompressing;
            Interlocked.Exchange(ref _done, 0);
            Interlocked.Exchange(ref _total, 0);
            try
            {
                if (ReferenceEquals(input, output) || !input.CanRead || !input.CanSeek ||
                    !output.CanRead || !output.CanWrite || !output.CanSeek)
                    throw new NotSupportedException("A conversão precisa de arquivos distintos com suporte a busca e releitura.");
                if (standalone) ExportSingle(input, output, sourceName);
                else ExportPackage(input, output);
                CheckCanceled();
                Logger.Notice.Print(LogClass.Loader, $"[NEO-QOL1-DECOMPRESS] Verified kind={(standalone ? "NCA" : "NSP")} bytes={output.Length}");
                return NczCompressor.ResultOk;
            }
            catch (OperationCanceledException) { return NczCompressor.ResultCanceled; }
            catch (Exception exception)
            {
                Logger.Error?.Print(LogClass.Loader, $"[NEO-QOL1-DECOMPRESS] Failed: {exception.Message}");
                return NczCompressor.ResultError;
            }
            finally { _phase = NczCompressor.PhaseIdle; Volatile.Write(ref _busy, 0); }
        }

        private static PartitionFileSystem OpenPfs(Stream input)
        {
            input.Position = 0;
            PartitionFileSystem pfs = new();
            try { pfs.Initialize(input.AsStorage(keepOpen: true)).ThrowIfFailure(); return pfs; }
            catch { pfs.Dispose(); throw; }
        }

        private static List<Entry> CreatePlan(PartitionFileSystem source)
        {
            List<Entry> plan = [];
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (DirectoryEntryEx entry in source.EnumerateEntries("/", "*"))
            {
                if (entry.Type != DirectoryEntryType.File) continue;
                bool compressed = entry.Name.EndsWith(".ncz", StringComparison.OrdinalIgnoreCase);
                string name = compressed ? entry.Name[..^4] + ".nca" : entry.Name;
                if (name.Length == 0 || name.IndexOfAny(['/', '\\', '\0']) >= 0 || !names.Add(name))
                    throw new InvalidDataException("Nome inválido ou duplicado no pacote.");
                long size = entry.Size;
                if (compressed)
                {
                    using UniqueRef<IFile> file = new();
                    source.OpenFile(ref file.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                    using NczFile ncz = OwnNcz(file.Release(), entry.Name);
                    size = ncz.NcaSize;
                }
                if (size < 0) throw new InvalidDataException("Tamanho inválido no pacote.");
                plan.Add(new Entry { SourceName = entry.Name, OutputName = name, Size = size, Compressed = compressed });
            }
            if (!plan.Any(entry => entry.Compressed)) throw new InvalidDataException("O pacote não contém NCZ.");
            return plan;
        }

        private static void ExportSingle(Stream input, Stream output, string name)
        {
            using NczFile file = OwnNcz(new ReadOnlyStreamFile(input), name);
            Interlocked.Exchange(ref _total, checked(file.NcaSize * 2));
            output.SetLength(0);
            output.Position = 0;
            byte[] expected;
            using (HashOnlyStream reference = new(output))
            {
                file.ExportNca(reference, CheckCanceled, AddProgress);
                expected = reference.Digest();
            }
            output.Flush();
            ValidateContentId(name, expected);
            _phase = NczCompressor.PhaseVerifying;
            output.Position = 0;
            byte[] actual = HashStream(output, file.NcaSize);
            if (!actual.AsSpan().SequenceEqual(expected) || output.Length != file.NcaSize)
                throw new InvalidDataException("Verificação SHA-256 do NCA falhou.");
        }

        private static void ExportPackage(Stream input, Stream output)
        {
            using PartitionFileSystem source = OpenPfs(input);
            List<Entry> plan = CreatePlan(source);
            int headerSize = HeaderSize(plan);
            Interlocked.Exchange(ref _total, checked(plan.Sum(entry => entry.Size) * 2));
            output.SetLength(0);
            output.Position = 0;
            output.Write(BuildHeader(plan, headerSize));
            foreach (Entry entry in plan)
            {
                CheckCanceled();
                using UniqueRef<IFile> file = new();
                source.OpenFile(ref file.Ref, ("/" + entry.SourceName).ToU8Span(), OpenMode.Read).ThrowIfFailure();
                using HashOnlyStream hash = new(output);
                if (entry.Compressed)
                {
                    using NczFile ncz = OwnNcz(file.Release(), entry.SourceName);
                    ncz.ExportNca(hash, CheckCanceled, AddProgress);
                }
                else CopyFile(file.Get, entry.Size, hash, true);
                entry.Hash = hash.Digest();
                if (entry.Compressed) ValidateContentId(entry.SourceName, entry.Hash);
            }
            output.Flush();
            _phase = NczCompressor.PhaseVerifying;
            using PartitionFileSystem destination = OpenPfs(output);
            foreach (Entry entry in plan)
            {
                CheckCanceled();
                using UniqueRef<IFile> file = new();
                destination.OpenFile(ref file.Ref, ("/" + entry.OutputName).ToU8Span(), OpenMode.Read).ThrowIfFailure();
                file.Get.GetSize(out long size).ThrowIfFailure();
                if (size != entry.Size) throw new InvalidDataException("Tamanho do arquivo exportado diverge.");
                using HashOnlyStream hash = new();
                CopyFile(file.Get, size, hash, true);
                if (!hash.Digest().AsSpan().SequenceEqual(entry.Hash)) throw new InvalidDataException("Verificação SHA-256 do NSP falhou.");
            }
            if (output.Length != checked(headerSize + plan.Sum(entry => entry.Size)))
                throw new InvalidDataException("Tamanho do NSP exportado diverge.");
        }

        private static void CopyFile(IFile input, long size, Stream output, bool progress)
        {
            byte[] buffer = new byte[1 << 20];
            long position = 0;
            while (position < size)
            {
                CheckCanceled();
                int count = (int)Math.Min(buffer.Length, size - position);
                input.Read(out long read, position, buffer.AsSpan(0, count)).ThrowIfFailure();
                if (read <= 0 || read > count) throw new EndOfStreamException();
                output.Write(buffer, 0, (int)read);
                position += read;
                if (progress) AddProgress((int)read);
            }
        }

        private static byte[] HashStream(Stream input, long size)
        {
            using HashOnlyStream hash = new();
            byte[] buffer = new byte[1 << 20];
            long remaining = size;
            while (remaining > 0)
            {
                CheckCanceled();
                int count = (int)Math.Min(buffer.Length, remaining);
                input.ReadExactly(buffer.AsSpan(0, count));
                hash.Write(buffer, 0, count);
                AddProgress(count);
                remaining -= count;
            }
            return hash.Digest();
        }

        private static void ValidateContentId(string name, byte[] hash)
        {
            string id = name.Split('.')[0];
            if (id.Length == 32 && id.All(Uri.IsHexDigit) &&
                !Convert.ToHexString(hash.AsSpan(0, 16)).Equals(id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O NCA reconstruído não corresponde ao content ID do arquivo.");
        }

        private static int HeaderSize(List<Entry> plan) => checked((0x10 + plan.Count * 0x18 +
            plan.Sum(entry => Encoding.UTF8.GetByteCount(entry.OutputName) + 1) + 31) & ~31);

        private static byte[] BuildHeader(List<Entry> plan, int size)
        {
            byte[] header = new byte[size];
            Encoding.ASCII.GetBytes("PFS0").CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), plan.Count);
            int strings = checked(0x10 + plan.Count * 0x18);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), size - strings);
            long offset = 0;
            int nameOffset = 0;
            for (int i = 0; i < plan.Count; i++)
            {
                Span<byte> entry = header.AsSpan(0x10 + i * 0x18, 0x18);
                BinaryPrimitives.WriteInt64LittleEndian(entry, offset);
                BinaryPrimitives.WriteInt64LittleEndian(entry[8..], plan[i].Size);
                BinaryPrimitives.WriteInt32LittleEndian(entry[16..], nameOffset);
                nameOffset += Encoding.UTF8.GetBytes(plan[i].OutputName, header.AsSpan(strings + nameOffset)) + 1;
                offset = checked(offset + plan[i].Size);
            }
            return header;
        }

        private static void CheckCanceled() { if (_cancel) throw new OperationCanceledException(); }
        private static void AddProgress(int bytes) => Interlocked.Add(ref _done, bytes);

        private static NczFile OwnNcz(IFile file, string name)
        {
            try { return new NczFile(file, name); }
            catch { file.Dispose(); throw; }
        }

        private sealed class ReadOnlyStreamFile(Stream stream) : IFile
        {
            protected override Result DoRead(out long read, long offset, Span<byte> destination, in ReadOption option)
            { stream.Position = offset; read = stream.Read(destination); return Result.Success; }
            protected override Result DoGetSize(out long size) { size = stream.Length; return Result.Success; }
            protected override Result DoFlush() => Result.Success;
            protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option) => throw new NotSupportedException();
            protected override Result DoSetSize(long size) => throw new NotSupportedException();
            protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer) => throw new NotSupportedException();
        }

        private sealed class HashOnlyStream(Stream destination = null) : Stream
        {
            private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            internal byte[] Digest() => _hash.GetHashAndReset();
            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void Write(ReadOnlySpan<byte> buffer) { _hash.AppendData(buffer); destination?.Write(buffer); }
            protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => destination?.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
