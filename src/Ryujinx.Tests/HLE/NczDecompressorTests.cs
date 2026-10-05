using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using NUnit.Framework;
using Ryujinx.HLE.FileSystem;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;

namespace Ryujinx.Tests.HLE
{
    [TestFixture, NonParallelizable]
    public class NczDecompressorTests
    {
        // Synthetic headers/payloads only: no game assets, tickets or console keys.
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void StandaloneSolidAndMixedBlocksReconstructExactNca(bool blocks, bool encrypted)
        {
            (byte[] ncz, byte[] expected) = Fixture(blocks, encrypted);
            using MemoryStream input = new(ncz);
            using MemoryStream output = new();
            Assert.That(NczDecompressor.GetExpectedSize(input, true), Is.EqualTo(expected.Length));
            string name = Convert.ToHexString(SHA256.HashData(expected).AsSpan(0, 16)) + ".ncz";
            Assert.That(NczDecompressor.Decompress(input, output, true, name), Is.EqualTo(NczCompressor.ResultOk));
            Assert.That(output.ToArray(), Is.EqualTo(expected));
            Assert.That(input.ToArray(), Is.EqualTo(ncz));
            Assert.That(input.CanRead && output.CanRead, Is.True, "The converter borrows streams.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NszExportsNcaAndPreservesOtherEntries(bool blocks)
        {
            (byte[] ncz, byte[] expected) = Fixture(blocks, true);
            byte[] original = Pfs(("content.ncz", ncz), ("content.tik", new byte[] { 1, 2, 3 }), ("metadata.cnmt.nca", new byte[] { 7, 8 }));
            using MemoryStream input = new(original);
            using MemoryStream output = new();
            long required = NczDecompressor.GetExpectedSize(input, false);
            Assert.That(NczDecompressor.Decompress(input, output, false), Is.EqualTo(NczCompressor.ResultOk));
            Assert.That(output.Length, Is.EqualTo(required));
            output.Position = 0;
            using PartitionFileSystem package = new();
            package.Initialize(output.AsStorage(keepOpen: true)).ThrowIfFailure();
            Assert.That(ReadEntry(package, "content.nca"), Is.EqualTo(expected));
            Assert.That(ReadEntry(package, "content.tik"), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(ReadEntry(package, "metadata.cnmt.nca"), Is.EqualTo(new byte[] { 7, 8 }));
            Assert.That(input.ToArray(), Is.EqualTo(original));
        }

        [Test]
        public void DuplicateReconstructedNameFailsBeforeOutputIsChanged()
        {
            (byte[] ncz, _) = Fixture(true, false);
            using MemoryStream input = new(Pfs(("content.ncz", ncz), ("content.nca", new byte[] { 42 })));
            using MemoryStream output = new();
            output.WriteByte(99);
            Assert.That(NczDecompressor.Decompress(input, output, false), Is.EqualTo(NczCompressor.ResultError));
            Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 99 }));
        }

        [Test]
        public void TruncatedNczAndWrongContentIdAreRejected()
        {
            (byte[] ncz, _) = Fixture(false, false);
            using MemoryStream truncated = new(ncz[..^3]);
            using MemoryStream output = new();
            Assert.That(NczDecompressor.Decompress(truncated, output, true), Is.EqualTo(NczCompressor.ResultError));
            using MemoryStream input = new(ncz);
            Assert.That(NczDecompressor.Decompress(input, output, true, new string('0', 32) + ".ncz"), Is.EqualTo(NczCompressor.ResultError));
        }

        [Test]
        public void CancellationStopsAtChunkBoundaryAndKeepsSource()
        {
            (byte[] ncz, _) = Fixture(false, false);
            using MemoryStream input = new(ncz);
            using CancelOnWrite output = new();
            Assert.That(NczDecompressor.Decompress(input, output, true), Is.EqualTo(NczCompressor.ResultCanceled));
            Assert.That(input.ToArray(), Is.EqualTo(ncz));
            Assert.That(NczDecompressor.Phase, Is.EqualTo(NczCompressor.PhaseIdle));
        }

        [Test]
        public void ReadbackDetectsCorruptedDestination()
        {
            (byte[] ncz, _) = Fixture(true, false);
            using MemoryStream input = new(ncz);
            using CorruptOnWrite output = new();
            Assert.That(NczDecompressor.Decompress(input, output, true), Is.EqualTo(NczCompressor.ResultError));
            Assert.That(input.ToArray(), Is.EqualTo(ncz));
        }

        private static byte[] ReadEntry(PartitionFileSystem package, string name)
        {
            using UniqueRef<IFile> file = new();
            package.OpenFile(ref file.Ref, ("/" + name).ToU8Span(), OpenMode.Read).ThrowIfFailure();
            file.Get.GetSize(out long size).ThrowIfFailure();
            byte[] bytes = new byte[checked((int)size)];
            file.Get.Read(out long read, 0, bytes).ThrowIfFailure();
            Assert.That(read, Is.EqualTo(size));
            return bytes;
        }

        private static (byte[], byte[]) Fixture(bool blocks, bool encrypted)
        {
            byte[] header = Enumerable.Range(0, 0x4000).Select(index => (byte)(index % 251)).ToArray();
            byte[] body = Enumerable.Range(0, 2 * 0x4000 + 37).Select(index => (byte)(index % 19)).ToArray();
            byte[] key = Enumerable.Range(0, 16).Select(index => (byte)index).ToArray();
            byte[] counter = Enumerable.Range(0, 16).Select(index => (byte)(index + 9)).ToArray();
            using MemoryStream ncz = new();
            ncz.Write(header);
            byte[] sections = new byte[0x50];
            Encoding.ASCII.GetBytes("NCZSECTN").CopyTo(sections, 0);
            BinaryPrimitives.WriteInt64LittleEndian(sections.AsSpan(8), 1);
            BinaryPrimitives.WriteInt64LittleEndian(sections.AsSpan(16), 0x4000);
            BinaryPrimitives.WriteInt64LittleEndian(sections.AsSpan(24), body.Length);
            BinaryPrimitives.WriteInt64LittleEndian(sections.AsSpan(32), encrypted ? 3 : 1);
            key.CopyTo(sections, 0x30);
            counter.CopyTo(sections, 0x40);
            ncz.Write(sections);
            using Compressor compressor = new(3);
            byte[] Pack(ReadOnlySpan<byte> raw)
            {
                byte[] packed = new byte[Compressor.GetCompressBound(raw.Length)];
                int count = compressor.Wrap(raw, packed.AsSpan());
                return packed[..count];
            }
            if (blocks)
            {
                List<byte[]> frames = [];
                for (int offset = 0; offset < body.Length; offset += 0x4000)
                {
                    byte[] raw = body.Skip(offset).Take(0x4000).ToArray();
                    byte[] packed = Pack(raw);
                    frames.Add(offset == 0x4000 || packed.Length >= raw.Length ? raw : packed);
                }
                byte[] blockHeader = new byte[0x18];
                Encoding.ASCII.GetBytes("NCZBLOCK").CopyTo(blockHeader, 0);
                blockHeader[8] = 2; blockHeader[9] = 1; blockHeader[11] = 14;
                BinaryPrimitives.WriteInt32LittleEndian(blockHeader.AsSpan(12), frames.Count);
                BinaryPrimitives.WriteInt64LittleEndian(blockHeader.AsSpan(16), body.Length);
                ncz.Write(blockHeader);
                foreach (byte[] frame in frames)
                {
                    byte[] size = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(size, frame.Length);
                    ncz.Write(size);
                }
                foreach (byte[] frame in frames) ncz.Write(frame);
            }
            else ncz.Write(Pack(body));
            byte[] restored = (byte[])body.Clone();
            if (encrypted)
            {
                using Aes aes = Aes.Create();
                aes.Key = key;
                for (int offset = 0; offset < restored.Length; offset += 16)
                {
                    byte[] blockCounter = new byte[16];
                    counter.AsSpan(0, 8).CopyTo(blockCounter);
                    BinaryPrimitives.WriteInt64BigEndian(blockCounter.AsSpan(8), (0x4000L + offset) / 16);
                    byte[] mask = aes.EncryptEcb(blockCounter, PaddingMode.None);
                    for (int i = 0; i < Math.Min(16, restored.Length - offset); i++) restored[offset + i] ^= mask[i];
                }
            }
            return (ncz.ToArray(), header.Concat(restored).ToArray());
        }

        private static byte[] Pfs(params (string Name, byte[] Data)[] files)
        {
            byte[] strings = Encoding.UTF8.GetBytes(string.Join('\0', files.Select(file => file.Name)) + '\0');
            int size = (0x10 + files.Length * 0x18 + strings.Length + 31) & ~31;
            byte[] header = new byte[size];
            Encoding.ASCII.GetBytes("PFS0").CopyTo(header, 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), files.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), size - 0x10 - files.Length * 0x18);
            int nameOffset = 0;
            long dataOffset = 0;
            for (int i = 0; i < files.Length; i++)
            {
                Span<byte> entry = header.AsSpan(0x10 + i * 0x18, 0x18);
                BinaryPrimitives.WriteInt64LittleEndian(entry, dataOffset);
                BinaryPrimitives.WriteInt64LittleEndian(entry[8..], files[i].Data.Length);
                BinaryPrimitives.WriteInt32LittleEndian(entry[16..], nameOffset);
                nameOffset += Encoding.UTF8.GetByteCount(files[i].Name) + 1;
                dataOffset += files[i].Data.Length;
            }
            strings.CopyTo(header, 0x10 + files.Length * 0x18);
            return header.Concat(files.SelectMany(file => file.Data)).ToArray();
        }

        private sealed class CancelOnWrite : MemoryStream
        {
            public override void Write(ReadOnlySpan<byte> buffer) { base.Write(buffer); NczDecompressor.Cancel(); }
        }
        private sealed class CorruptOnWrite : MemoryStream
        {
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                byte[] copy = buffer.ToArray();
                if (copy.Length > 0) copy[0] ^= 1;
                base.Write(copy.AsSpan());
            }
        }
    }
}
