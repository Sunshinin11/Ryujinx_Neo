using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.FsSystem;
using Ryujinx.Common.Logging;
using System;
using System.Buffers.Binary;
using System.IO;
using Path = LibHac.Fs.Path;

namespace Ryujinx.HLE.FileSystem
{
    /// <summary>
    /// Protects LibHac's save-data indexes from interrupted replacement.
    ///
    /// FlatMapKeyValueStore replaces imkvdb.arc by deleting it, creating a
    /// pre-sized file and then writing the new archive. If the process is
    /// terminated between those operations the index is left empty/zeroed.
    /// Keep the last structurally valid archive beside it and restore that
    /// archive before LibHac starts when an interrupted write is detected.
    /// </summary>
    internal sealed class SaveDataProtectingFileSystem : LocalFileSystem
    {
        private const string IndexFileName = "imkvdb.arc";
        private const string PreviousSuffix = ".ryujinx-prev";
        private const uint ArchiveMagic = 0x564B4D49;
        private const uint EntryMagic = 0x4E454D49;
        private const int ArchiveHeaderSize = 12;
        private const int EntryHeaderSize = 12;
        private const int MaxEntryCount = 4224;
        private const long MaxArchiveSize = 64L * 1024 * 1024;

        private readonly string _rootPath;
        private readonly string _rootPrefix;

        public SaveDataProtectingFileSystem(string rootPath)
            : base(useUnixTimeStamps: true)
        {
            _rootPath = System.IO.Path.GetFullPath(rootPath);
            _rootPrefix = _rootPath.EndsWith(System.IO.Path.DirectorySeparatorChar)
                ? _rootPath
                : _rootPath + System.IO.Path.DirectorySeparatorChar;

            RecoverInterruptedIndexWrites();
        }

        protected override Result DoDeleteFile(ref readonly Path path)
        {
            string relativePath = path.ToString();

            if (relativePath.EndsWith('/' + IndexFileName, StringComparison.Ordinal))
            {
                string fullPath = GetSafeHostPath(relativePath);

                if (fullPath != null && File.Exists(fullPath) && IsValidArchive(fullPath))
                {
                    try
                    {
                        AtomicCopy(fullPath, fullPath + PreviousSuffix);
                        Logger.Info?.Print(LogClass.ServiceFs, $"[SAVE-PROTECT] Preserved valid index before replacement: {relativePath}");
                    }
                    catch (Exception exception)
                    {
                        Logger.Error?.Print(LogClass.ServiceFs, $"[SAVE-PROTECT] Refusing to replace {relativePath}: could not preserve the current index. {exception.Message}");
                        return ResultFs.UnexpectedInLocalFileSystemA;
                    }
                }
            }

            return base.DoDeleteFile(in path);
        }

        private void RecoverInterruptedIndexWrites()
        {
            if (!Directory.Exists(_rootPath))
            {
                return;
            }

            try
            {
                foreach (string previousPath in Directory.EnumerateFiles(
                    _rootPath,
                    IndexFileName + PreviousSuffix,
                    SearchOption.AllDirectories))
                {
                    string currentPath = previousPath[..^PreviousSuffix.Length];

                    if (!IsValidArchive(previousPath) || IsValidArchive(currentPath))
                    {
                        continue;
                    }

                    if (File.Exists(currentPath))
                    {
                        string corruptPath = currentPath + $".corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                        File.Move(currentPath, corruptPath, overwrite: false);
                        Logger.Warning?.Print(LogClass.ServiceFs, $"[SAVE-PROTECT] Quarantined interrupted save index: {corruptPath}");
                    }

                    AtomicCopy(previousPath, currentPath);
                    Logger.Warning?.Print(LogClass.ServiceFs, $"[SAVE-PROTECT] Automatically restored save index: {currentPath}");
                }
            }
            catch (Exception exception)
            {
                Logger.Error?.Print(LogClass.ServiceFs, $"[SAVE-PROTECT] Save index recovery scan failed: {exception}");
            }
        }

        private string GetSafeHostPath(string emulatedPath)
        {
            string relativePath = emulatedPath.TrimStart('/', '\\')
                .Replace('/', System.IO.Path.DirectorySeparatorChar)
                .Replace('\\', System.IO.Path.DirectorySeparatorChar);
            string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_rootPath, relativePath));

            return fullPath.StartsWith(_rootPrefix, StringComparison.Ordinal) ? fullPath : null;
        }

        private static bool IsValidArchive(string path)
        {
            try
            {
                FileInfo fileInfo = new(path);

                if (!fileInfo.Exists || fileInfo.Length < ArchiveHeaderSize || fileInfo.Length > MaxArchiveSize)
                {
                    return false;
                }

                byte[] data = File.ReadAllBytes(path);
                ReadOnlySpan<byte> archive = data;

                if (BinaryPrimitives.ReadUInt32LittleEndian(archive) != ArchiveMagic)
                {
                    return false;
                }

                int entryCount = BinaryPrimitives.ReadInt32LittleEndian(archive.Slice(8, 4));

                if ((uint)entryCount > MaxEntryCount)
                {
                    return false;
                }

                int offset = ArchiveHeaderSize;

                for (int index = 0; index < entryCount; index++)
                {
                    if (archive.Length - offset < EntryHeaderSize)
                    {
                        return false;
                    }

                    ReadOnlySpan<byte> entryHeader = archive.Slice(offset, EntryHeaderSize);

                    if (BinaryPrimitives.ReadUInt32LittleEndian(entryHeader) != EntryMagic)
                    {
                        return false;
                    }

                    int keySize = BinaryPrimitives.ReadInt32LittleEndian(entryHeader.Slice(4, 4));
                    int valueSize = BinaryPrimitives.ReadInt32LittleEndian(entryHeader.Slice(8, 4));
                    long nextOffset = (long)offset + EntryHeaderSize + keySize + valueSize;

                    if (keySize < 0 || valueSize < 0 || nextOffset > archive.Length)
                    {
                        return false;
                    }

                    offset = (int)nextOffset;
                }

                return offset == archive.Length;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void AtomicCopy(string sourcePath, string destinationPath)
        {
            string directory = System.IO.Path.GetDirectoryName(destinationPath)!;
            string temporaryPath = System.IO.Path.Combine(
                directory,
                $".{System.IO.Path.GetFileName(destinationPath)}.{Environment.ProcessId}.tmp");

            try
            {
                using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream destination = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(destination);
                    destination.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
