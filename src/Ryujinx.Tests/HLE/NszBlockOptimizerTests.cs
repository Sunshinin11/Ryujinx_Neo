using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;
using NUnit.Framework;
using Ryujinx.HLE.FileSystem;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using ZstdSharp;

namespace Ryujinx.Tests.HLE
{
    [TestFixture, NonParallelizable]
    public class NszBlockOptimizerTests
    {
        private const int PrefixSize = 0x4050;

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void SolidAndMixedPackagesPreserveNcaBytesAndOtherEntries(bool encrypted, bool mixed)
        {
            byte[] solid = Ncz(encrypted, false);
            byte[] block = Ncz(false, true);
            byte[] source = mixed
                ? Pfs(("content.ncz", solid), ("existing.ncz", block), ("ticket.tik", new byte[] { 7, 2, 5 }))
                : Pfs(("content.ncz", solid), ("ticket.tik", new byte[] { 7, 2, 5 }));
            using MemoryStream input = new(source), output = new();
            long required = NszBlockOptimizer.GetRequiredSize(input);
            Assert.That(required, Is.GreaterThan(0));
            Assert.That(NszBlockOptimizer.Optimize(input, output), Is.EqualTo(NczCompressor.ResultOk));
            Assert.That(output.Length, Is.LessThanOrEqualTo(required));
            Assert.That(input.ToArray(), Is.EqualTo(source));
            byte[] converted = Entry(output.ToArray(), "content.ncz");
            Assert.That(converted[..PrefixSize], Is.EqualTo(solid[..PrefixSize]));
            Assert.That(Encoding.ASCII.GetString(converted, PrefixSize, 8), Is.EqualTo("NCZBLOCK"));
            Assert.That(converted[PrefixSize + 11], Is.EqualTo(20));
            Assert.That(ExportNca(converted), Is.EqualTo(ExportNca(solid)));
            Assert.That(Entry(output.ToArray(), "ticket.tik"), Is.EqualTo(new byte[] { 7, 2, 5 }));
            if (mixed) Assert.That(Entry(output.ToArray(), "existing.ncz"), Is.EqualTo(block));
            using MemoryStream inspected = new(output.ToArray());
            Assert.That(NszBlockOptimizer.GetRequiredSize(inspected), Is.Zero);

            // Exercise the unchanged production reader, including AES restoration and block misses.
            using MemoryStream package = new(output.ToArray());
            using PartitionFileSystem pfs = new();
            pfs.Initialize(package.AsStorage(keepOpen: true)).ThrowIfFailure();
            Assert.That(NczSupport.TryWrap(pfs, out PartitionFileSystem view), Is.True);
            using (view)
            {
                using UniqueRef<IFile> file = new();
                view.OpenFile(ref file.Ref, "/content.nca".ToU8Span(), OpenMode.Read).ThrowIfFailure();
                byte[] expected = ExportNca(solid), actual = new byte[expected.Length];
                file.Get.Read(out long read, 0, actual).ThrowIfFailure();
                Assert.That(read, Is.EqualTo(actual.Length));
                Assert.That(actual, Is.EqualTo(expected));
            }
            Assert.That(input.CanRead && output.CanRead, Is.True);
            Assert.That(NszBlockOptimizer.Phase, Is.EqualTo(NczCompressor.PhaseIdle));
        }

        [Test]
        public void AlreadyBlockPackageLeavesTheDestinationUnchanged()
        {
            using MemoryStream input = new(Pfs(("content.ncz", Ncz(false, true)))), output = new();
            output.WriteByte(99);
            Assert.That(NszBlockOptimizer.GetRequiredSize(input), Is.Zero);
            Assert.That(NszBlockOptimizer.Optimize(input, output), Is.EqualTo(NczCompressor.ResultNoSavings));
            Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 99 }));
        }

        [Test]
        public void IncompressibleConvertedBlocksAreStoredRawAndReadCorrectly()
        {
            byte[] solid = Ncz(false, false, incompressible: true);
            using MemoryStream input = new(Pfs(("content.ncz", solid))), output = new();
            Assert.That(NszBlockOptimizer.Optimize(input, output), Is.EqualTo(NczCompressor.ResultOk));
            byte[] converted = Entry(output.ToArray(), "content.ncz");
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(converted.AsSpan(PrefixSize + 24)), Is.EqualTo(1 << 20));
            Assert.That(ExportNca(converted), Is.EqualTo(ExportNca(solid)));
        }

        [TestCase(0)] // Entry count.
        [TestCase(1)] // String table size.
        [TestCase(2)] // Negative entry offset.
        [TestCase(3)] // Entry outside file.
        [TestCase(4)] // Name outside table.
        [TestCase(5)] // Overlapping file entries.
        public void InvalidPfsMetadataIsRejectedBeforeWriting(int kind)
        {
            byte[] data = Pfs(("content.ncz", Ncz(false, false)), ("ticket.tik", new byte[] { 1 }));
            switch (kind)
            {
                case 0: BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), int.MaxValue); break;
                case 1: BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), -1); break;
                case 2: BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0x10), -1); break;
                case 3: BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0x10), long.MaxValue); break;
                case 4: BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x20), int.MaxValue); break;
                case 5: BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0x28), 0); break;
            }
            AssertRejectedBeforeWrite(data);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DuplicateNamesAndPackagesWithoutNczAreRejected(bool duplicate)
        {
            byte[] data = duplicate
                ? Pfs(("content.ncz", Ncz(false, false)), ("content.ncz", new byte[] { 1 }))
                : Pfs(("content.nca", new byte[] { 1 }));
            AssertRejectedBeforeWrite(data);
        }

        [TestCase(0)] // Invalid NCZSECTN magic.
        [TestCase(1)] // Invalid section offset.
        [TestCase(2)] // Overflowed section size.
        [TestCase(3)] // Invalid block size.
        [TestCase(4)] // Block count inconsistent with body.
        public void InvalidNczMetadataIsRejectedBeforeWriting(int kind)
        {
            byte[] ncz = Ncz(false, kind >= 3);
            switch (kind)
            {
                case 0: ncz[0x4000] = 0; break;
                case 1: BinaryPrimitives.WriteInt64LittleEndian(ncz.AsSpan(0x4010), 1); break;
                case 2: BinaryPrimitives.WriteInt64LittleEndian(ncz.AsSpan(0x4018), long.MaxValue); break;
                case 3: BinaryPrimitives.WriteInt32LittleEndian(ncz.AsSpan(PrefixSize + 0x18), int.MaxValue); break;
                case 4: BinaryPrimitives.WriteInt32LittleEndian(ncz.AsSpan(PrefixSize + 12), 100); break;
            }
            AssertRejectedBeforeWrite(Pfs(("content.ncz", ncz)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TruncatedOrExcessSolidPayloadFailsVerification(bool excess)
        {
            byte[] ncz = Ncz(false, false);
            if (excess) BinaryPrimitives.WriteInt64LittleEndian(ncz.AsSpan(0x4018), 7);
            else Array.Resize(ref ncz, ncz.Length - 3);
            byte[] source = Pfs(("content.ncz", ncz));
            using MemoryStream input = new(source), output = new();
            Assert.That(NszBlockOptimizer.Optimize(input, output), Is.EqualTo(NczCompressor.ResultError));
            Assert.That(input.ToArray(), Is.EqualTo(source));
        }

        [TestCase(1)]
        [TestCase(3)]
        public void CancellationDuringWritingKeepsTheSourceAndResetsBusy(int cancelAfter)
        {
            byte[] source = Pfs(("content.ncz", Ncz(false, false)));
            using MemoryStream input = new(source);
            using CancelOnWrite output = new(cancelAfter);
            Assert.That(NszBlockOptimizer.Optimize(input, output), Is.EqualTo(NczCompressor.ResultCanceled));
            Assert.That(input.ToArray(), Is.EqualTo(source));
            Assert.That(NszBlockOptimizer.Phase, Is.EqualTo(NczCompressor.PhaseIdle));
            using MemoryStream retry = new();
            Assert.That(NszBlockOptimizer.Optimize(input, retry), Is.EqualTo(NczCompressor.ResultOk));
        }

        [Test]
        public void CorruptedDestinationIsRejectedByReadback()
        {
            byte[] source = Pfs(("content.ncz", Ncz(false, false)), ("ticket.tik", new byte[] { 1, 2, 3 }));
            using MemoryStream input = new(source);
            using CorruptReadback output = new();
            Assert.That(NszBlockOptimizer.Optimize(input, output), Is.EqualTo(NczCompressor.ResultError));
            Assert.That(input.ToArray(), Is.EqualTo(source));
        }

        private static void AssertRejectedBeforeWrite(byte[] source)
        {
            using MemoryStream input = new(source), output = new();
            output.WriteByte(99);
            Assert.That(NszBlockOptimizer.GetRequiredSize(input), Is.EqualTo(-1));
            Assert.That(NszBlockOptimizer.Optimize(input, output), Is.EqualTo(NczCompressor.ResultError));
            Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 99 }));
            Assert.That(input.ToArray(), Is.EqualTo(source));
        }

        private static byte[] ExportNca(byte[] ncz)
        {
            using MemoryStream input = new(ncz), output = new();
            Assert.That(NczDecompressor.Decompress(input, output, true, "content.ncz"), Is.EqualTo(NczCompressor.ResultOk));
            return output.ToArray();
        }

        private static byte[] Entry(byte[] data, string name)
        {
            using MemoryStream input = new(data);
            using PartitionFileSystem pfs = new();
            pfs.Initialize(input.AsStorage(keepOpen: true)).ThrowIfFailure();
            using UniqueRef<IFile> file = new();
            pfs.OpenFile(ref file.Ref, ("/" + name).ToU8Span(), OpenMode.Read).ThrowIfFailure();
            file.Get.GetSize(out long size).ThrowIfFailure();
            byte[] result = new byte[checked((int)size)];
            file.Get.Read(out long read, 0, result).ThrowIfFailure();
            Assert.That(read, Is.EqualTo(size));
            return result;
        }

        private static byte[] Ncz(bool encrypted, bool block, bool incompressible = false)
        {
            int length = block ? 0x4000 + 17 : 2 * 1024 * 1024 + 137;
            byte[] body = Enumerable.Range(0, length).Select(i => (byte)(i % 13)).ToArray();
            if (incompressible) new Random(42).NextBytes(body);
            byte[] prefix = new byte[PrefixSize];
            for (int i = 0; i < 0x4000; i++) prefix[i] = (byte)(i % 251);
            "NCZSECTN"u8.CopyTo(prefix.AsSpan(0x4000));
            BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(0x4008), 1);
            BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(0x4010), 0x4000);
            BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(0x4018), length);
            BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(0x4020), encrypted ? 3 : 1);
            for (int i = 0; i < 32; i++) prefix[0x4030 + i] = (byte)(i + 3);
            using MemoryStream result = new();
            result.Write(prefix);
            if (block)
            {
                byte[] header = new byte[0x20];
                "NCZBLOCK"u8.CopyTo(header);
                header[8] = 2; header[9] = 1; header[11] = 14;
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 2);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), length);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), 0x4000);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 17);
                result.Write(header);
                result.Write(body); // Existing raw blocks must be copied byte-for-byte.
            }
            else
            {
                using Compressor compressor = new(3);
                byte[] packed = new byte[Compressor.GetCompressBound(body.Length)];
                int count = compressor.Wrap(body.AsSpan(), packed.AsSpan());
                result.Write(packed, 0, count);
            }
            return result.ToArray();
        }

        private static byte[] Pfs(params (string Name, byte[] Data)[] files)
        {
            byte[] names = Encoding.UTF8.GetBytes(string.Join('\0', files.Select(f => f.Name)) + '\0');
            int size = (0x10 + files.Length * 0x18 + names.Length + 31) & ~31;
            byte[] header = new byte[size];
            "PFS0"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), files.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), size - 0x10 - files.Length * 0x18);
            int name = 0;
            long offset = 0;
            for (int i = 0; i < files.Length; i++)
            {
                Span<byte> e = header.AsSpan(0x10 + i * 0x18, 0x18);
                BinaryPrimitives.WriteInt64LittleEndian(e, offset);
                BinaryPrimitives.WriteInt64LittleEndian(e[8..], files[i].Data.Length);
                BinaryPrimitives.WriteInt32LittleEndian(e[16..], name);
                name += Encoding.UTF8.GetByteCount(files[i].Name) + 1;
                offset += files[i].Data.Length;
            }
            names.CopyTo(header, 0x10 + files.Length * 0x18);
            using MemoryStream result = new();
            result.Write(header);
            foreach (var f in files) result.Write(f.Data);
            return result.ToArray();
        }

        private sealed class CancelOnWrite(int cancelAfter) : MemoryStream
        {
            private int _writes;
            public override void Write(ReadOnlySpan<byte> data)
            {
                base.Write(data);
                if (++_writes == cancelAfter) NszBlockOptimizer.Cancel();
            }
        }

        private sealed class CorruptReadback : MemoryStream
        {
            private bool _corrupted;
            public override void Flush()
            {
                if (!_corrupted)
                {
                    long end = Position;
                    Position = Length - 1;
                    int last = ReadByte();
                    Position--;
                    WriteByte((byte)(last ^ 1));
                    Position = end;
                    _corrupted = true;
                }
                base.Flush();
            }
        }
    }
}
