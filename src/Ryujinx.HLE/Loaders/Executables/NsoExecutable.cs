using LibHac.Common.FixedArrays;
using LibHac.Fs;
using LibHac.Loader;
using LibHac.Tools.FsSystem;
using Ryujinx.Common.Logging;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ryujinx.HLE.Loaders.Executables
{
    partial class NsoExecutable : IExecutable
    {
        // NEOV2-ZBIC1: SDK 22+ NSOs may set NsoFlags_UseZbicCompression (bit 7). Their segments are
        // ZBIC (Zstandard variant) instead of LZ4, which LibHac's NsoReader cannot decode.
        private const int NsoHeaderSize = 0x100;
        private const uint FlagUseZbicCompression = 1u << 7;

        public byte[] Program { get; }
        public Span<byte> Text => Program.AsSpan((int)TextOffset, (int)TextSize);
        public Span<byte> Ro => Program.AsSpan((int)RoOffset, (int)RoSize);
        public Span<byte> Data => Program.AsSpan((int)DataOffset, (int)DataSize);

        public uint TextOffset { get; }
        public uint RoOffset { get; }
        public uint DataOffset { get; }
        public uint BssOffset => DataOffset + (uint)Data.Length;

        public uint TextSize { get; }
        public uint RoSize { get; }
        public uint DataSize { get; }
        public uint BssSize { get; }

        public string Name;
        public Array32<byte> BuildId;

        [GeneratedRegex(@"[a-z]:[\\/][ -~]{5,}\.nss", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ModuleRegex();
        [GeneratedRegex(@"sdk_version: ([0-9.]*)")]
        private static partial Regex FsSdkRegex();
        [GeneratedRegex(@"SDK MW[ -~]*")]
        private static partial Regex SdkMwRegex();

        public NsoExecutable(IStorage inStorage, string name = null)
        {
            NsoReader reader = new();

            reader.Initialize(inStorage.AsFile(OpenMode.Read)).ThrowIfFailure();

            TextOffset = reader.Header.Segments[0].MemoryOffset;
            RoOffset = reader.Header.Segments[1].MemoryOffset;
            DataOffset = reader.Header.Segments[2].MemoryOffset;
            BssSize = reader.Header.BssSize;

            reader.GetSegmentSize(NsoReader.SegmentType.Data, out uint uncompressedSize).ThrowIfFailure();

            Program = new byte[DataOffset + uncompressedSize];

            byte[] rawHeader = new byte[NsoHeaderSize];
            inStorage.Read(0, rawHeader).ThrowIfFailure();

            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(rawHeader.AsSpan(0x0C));

            if ((flags & FlagUseZbicCompression) != 0)
            {
                TextSize = DecompressZbicSection(inStorage, reader, rawHeader, flags, NsoReader.SegmentType.Text, 0, TextOffset, name);
                RoSize = DecompressZbicSection(inStorage, reader, rawHeader, flags, NsoReader.SegmentType.Ro, 1, RoOffset, name);
                DataSize = DecompressZbicSection(inStorage, reader, rawHeader, flags, NsoReader.SegmentType.Data, 2, DataOffset, name);

                Logger.Notice.Print(LogClass.Loader, $"[NEOV2-ZBIC1] {name}: ZBIC NSO decoded (flags=0x{flags:X2}, text=0x{TextSize:X}, ro=0x{RoSize:X}, data=0x{DataSize:X})");
            }
            else
            {
                TextSize = DecompressSection(reader, NsoReader.SegmentType.Text, TextOffset);
                RoSize = DecompressSection(reader, NsoReader.SegmentType.Ro, RoOffset);
                DataSize = DecompressSection(reader, NsoReader.SegmentType.Data, DataOffset);
            }

            Name = name;
            BuildId = reader.Header.ModuleId;

            PrintRoSectionInfo();
        }

        private uint DecompressSection(NsoReader reader, NsoReader.SegmentType segmentType, uint offset)
        {
            reader.GetSegmentSize(segmentType, out uint uncompressedSize).ThrowIfFailure();

            Span<byte> span = Program.AsSpan((int)offset, (int)uncompressedSize);

            reader.ReadSegment(segmentType, span).ThrowIfFailure();

            return uncompressedSize;
        }

        private uint DecompressZbicSection(IStorage storage, NsoReader reader, byte[] header, uint flags, NsoReader.SegmentType segmentType, int segment, uint offset, string name)
        {

            reader.GetSegmentSize(segmentType, out uint uncompressedSize).ThrowIfFailure();

            uint fileOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x10 + segment * 0x10));
            uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x60 + segment * 4));
            bool isCompressed = (flags & (1u << segment)) != 0;
            bool checkHash = (flags & (8u << segment)) != 0;

            Span<byte> span = Program.AsSpan((int)offset, (int)uncompressedSize);

            if (!isCompressed)
            {
                storage.Read(fileOffset, span).ThrowIfFailure();
            }
            else
            {
                byte[] compressed = new byte[compressedSize];

                storage.Read(fileOffset, compressed).ThrowIfFailure();

                if (!ZstdBicDecoder.IsSupportedFrame(compressed))
                {
                    throw new InvalidDataException($"[NEOV2-ZBIC1] {name}: segment {segmentType} is flagged ZBIC but has no ZBIC/zstd frame");
                }

                int written = ZstdBicDecoder.Decompress(compressed, span);

                if (written != span.Length)
                {
                    throw new InvalidDataException($"[NEOV2-ZBIC1] {name}: segment {segmentType} decoded 0x{written:X} bytes, expected 0x{span.Length:X}");
                }
            }

            if (checkHash)
            {
                byte[] hash = SHA256.HashData(span);

                if (!hash.AsSpan().SequenceEqual(header.AsSpan(0xA0 + segment * 0x20, 0x20)))
                {
                    throw new InvalidDataException($"[NEOV2-ZBIC1] {name}: segment {segmentType} hash mismatch after ZBIC decode");
                }
            }

            return uncompressedSize;
        }

        private void PrintRoSectionInfo()
        {
            string rawTextBuffer = Encoding.ASCII.GetString(Ro);
            StringBuilder stringBuilder = new();

            string modulePath = null;

            if (BitConverter.ToInt32(Ro[..4]) == 0)
            {
                int length = BitConverter.ToInt32(Ro.Slice(4, 4));
                if (length > 0)
                {
                    modulePath = Encoding.UTF8.GetString(Ro.Slice(8, length));
                }
            }

            if (string.IsNullOrEmpty(modulePath))
            {
                Match moduleMatch = ModuleRegex().Match(rawTextBuffer);
                if (moduleMatch.Success)
                {
                    modulePath = moduleMatch.Value;
                }
            }

            stringBuilder.AppendLine($"    Module: {modulePath}");

            Match fsSdkMatch = FsSdkRegex().Match(rawTextBuffer);
            if (fsSdkMatch.Success)
            {
                stringBuilder.AppendLine($"    FS SDK Version: {fsSdkMatch.Value.Replace("sdk_version: ", "")}");
            }

            MatchCollection sdkMwMatches = SdkMwRegex().Matches(rawTextBuffer);
            if (sdkMwMatches.Count != 0)
            {
                string libHeader = "    SDK Libraries: ";
                string libContent = string.Join($"\n{new string(' ', libHeader.Length)}", sdkMwMatches);

                stringBuilder.AppendLine($"{libHeader}{libContent}");
            }

            if (stringBuilder.Length > 0)
            {
                Logger.Info?.Print(LogClass.Loader, $"{Name}:\n{stringBuilder.ToString().TrimEnd('\r', '\n')}");
            }
        }
    }
}
