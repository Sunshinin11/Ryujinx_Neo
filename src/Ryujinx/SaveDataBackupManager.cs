using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Ryujinx.Ava
{
    /// <summary>
    /// In-process replacement for the external save backup launcher hook.
    /// It runs before the emulated filesystem is created, rejects destructive
    /// state drops, restores the newest healthy snapshot when possible and
    /// keeps a rotating set of validated snapshots.
    /// </summary>
    internal static class SaveDataBackupManager
    {
        private const int MaximumBackups = 30;
        private const long MinimumHealthyBytes = 4096;
        private const long SizeDropReferenceBytes = 5L * 1024 * 1024;

        private readonly record struct SaveState(int ContainerCount, long ByteCount)
        {
            public bool HasData => ContainerCount > 0 && ByteCount >= MinimumHealthyBytes;
        }

        public static void RunPreflight()
        {
            string dataRoot = AppDataManager.BaseDirPath;
            string savePath = Path.Combine(dataRoot, "bis", "user", "save");
            string saveMetaPath = Path.Combine(dataRoot, "bis", "user", "saveMeta");
            string profilesPath = Path.Combine(dataRoot, "system", "Profiles.json");
            string backupRoot = AppDataManager.Mode == AppDataManager.LaunchMode.UserProfile
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Backups",
                    "Ryujinx-Saves")
                : Path.Combine(dataRoot, "save-backups");
            string statePath = Path.Combine(backupRoot, ".last-good-state");

            Directory.CreateDirectory(backupRoot);

            SaveState previousState = ReadState(statePath);
            SaveState currentState = Measure(savePath, saveMetaPath);

            bool hasProtectedHistory = previousState.HasData || EnumerateBackups(backupRoot).Any();

            if (hasProtectedHistory && IsSuspicious(currentState, previousState, out string reason))
            {
                Logger.Error?.Print(LogClass.Application, $"[SAVE-BACKUP] Anti-wipe detected: {reason}");

                string recoverySource = FindNewestHealthyBackup(backupRoot, previousState)
                    ?? throw new InvalidOperationException(
                        $"Ryujinx blocked save access after detecting '{reason}', but no healthy backup was available.");

                RestoreSaveTree(recoverySource, savePath, saveMetaPath);
                currentState = Measure(savePath, saveMetaPath);

                if (!currentState.HasData)
                {
                    throw new InvalidOperationException("Automatic save recovery completed, but the restored tree failed validation.");
                }

                Logger.Warning?.Print(
                    LogClass.Application,
                    $"[SAVE-BACKUP] Automatically restored saves from {recoverySource} ({currentState.ContainerCount} containers, {currentState.ByteCount} bytes).");
            }

            // A clean installation legitimately has no saves yet.
            if (!currentState.HasData)
            {
                Directory.CreateDirectory(savePath);
                return;
            }

            if (!NewestBackupMatches(backupRoot, currentState))
            {
                CreateValidatedBackup(backupRoot, savePath, saveMetaPath, profilesPath, currentState);
            }

            SaveState protectedState = new(
                Math.Max(previousState.ContainerCount, currentState.ContainerCount),
                Math.Max(previousState.ByteCount, currentState.ByteCount));

            AtomicWriteAllText(
                statePath,
                $"{protectedState.ContainerCount} {protectedState.ByteCount}{Environment.NewLine}");

            TrimBackups(backupRoot);
        }

        private static bool IsSuspicious(SaveState current, SaveState previous, out string reason)
        {
            List<string> reasons = new();

            if (!current.HasData)
            {
                reasons.Add($"save tree is empty or too small ({current.ContainerCount} containers, {current.ByteCount} bytes)");
            }

            if (previous.ContainerCount >= 10 && current.ContainerCount * 100L < previous.ContainerCount * 50L)
            {
                reasons.Add($"container count dropped from {previous.ContainerCount} to {current.ContainerCount}");
            }

            if (previous.ByteCount >= SizeDropReferenceBytes && current.ByteCount * 100L < previous.ByteCount * 35L)
            {
                reasons.Add($"save size dropped from {previous.ByteCount} to {current.ByteCount} bytes");
            }

            reason = string.Join("; ", reasons);
            return reasons.Count != 0;
        }

        private static SaveState Measure(string savePath, string saveMetaPath = null)
        {
            int containerCount = Directory.Exists(savePath)
                ? Directory.EnumerateDirectories(savePath, "*", SearchOption.TopDirectoryOnly).Count()
                : 0;
            long byteCount = GetDirectoryFileBytes(savePath) + GetDirectoryFileBytes(saveMetaPath);

            return new SaveState(containerCount, byteCount);
        }

        private static long GetDirectoryFileBytes(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return 0;
            }

            long total = 0;

            foreach (string filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(filePath).Length;
            }

            return total;
        }

        private static SaveState ReadState(string statePath)
        {
            try
            {
                string[] values = File.ReadAllText(statePath)
                    .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

                if (values.Length >= 2 &&
                    int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int count) &&
                    long.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes))
                {
                    return new SaveState(count, bytes);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return default;
        }

        private static IEnumerable<string> EnumerateBackups(string backupRoot)
        {
            if (!Directory.Exists(backupRoot))
            {
                return Enumerable.Empty<string>();
            }

            return Directory.EnumerateDirectories(backupRoot)
                .Where(path =>
                {
                    string name = Path.GetFileName(path);

                    return name.Length >= 19 && DateTime.TryParseExact(
                        name[..19],
                        "yyyy-MM-dd_HH-mm-ss",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _);
                })
                .OrderByDescending(Path.GetFileName);
        }

        private static string FindNewestHealthyBackup(string backupRoot, SaveState protectedState)
        {
            foreach (string backupPath in EnumerateBackups(backupRoot))
            {
                SaveState state = Measure(Path.Combine(backupPath, "save"), Path.Combine(backupPath, "saveMeta"));

                if (!state.HasData)
                {
                    continue;
                }

                bool enoughContainers = protectedState.ContainerCount < 10 ||
                    state.ContainerCount * 100L >= protectedState.ContainerCount * 50L;
                bool enoughBytes = protectedState.ByteCount < SizeDropReferenceBytes ||
                    state.ByteCount * 100L >= protectedState.ByteCount * 35L;

                if (enoughContainers && enoughBytes)
                {
                    return backupPath;
                }
            }

            return null;
        }

        private static bool NewestBackupMatches(string backupRoot, SaveState currentState)
        {
            string newest = EnumerateBackups(backupRoot).FirstOrDefault();

            if (newest == null || DateTime.UtcNow - Directory.GetLastWriteTimeUtc(newest) > TimeSpan.FromMinutes(2))
            {
                return false;
            }

            SaveState backupState = Measure(Path.Combine(newest, "save"), Path.Combine(newest, "saveMeta"));
            return backupState == currentState;
        }

        private static void RestoreSaveTree(string backupPath, string savePath, string saveMetaPath)
        {
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            RestoreDirectory(Path.Combine(backupPath, "save"), savePath, timestamp);

            string backupSaveMeta = Path.Combine(backupPath, "saveMeta");

            if (Directory.Exists(backupSaveMeta))
            {
                RestoreDirectory(backupSaveMeta, saveMetaPath, timestamp);
            }
        }

        private static void RestoreDirectory(string sourcePath, string destinationPath, string timestamp)
        {
            string parentPath = Path.GetDirectoryName(destinationPath)!;
            string stagingPath = destinationPath + $".restore-{Environment.ProcessId}";
            string damagedPath = destinationPath + $".corrupt-{timestamp}";

            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }

            CopyDirectory(sourcePath, stagingPath);

            if (Directory.Exists(destinationPath))
            {
                Directory.Move(destinationPath, damagedPath);
            }

            try
            {
                Directory.Move(stagingPath, destinationPath);
            }
            catch
            {
                if (!Directory.Exists(destinationPath) && Directory.Exists(damagedPath))
                {
                    Directory.Move(damagedPath, destinationPath);
                }

                throw;
            }
        }

        private static void CreateValidatedBackup(
            string backupRoot,
            string savePath,
            string saveMetaPath,
            string profilesPath,
            SaveState expectedState)
        {
            string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            string destinationPath = Path.Combine(backupRoot, name);

            if (Directory.Exists(destinationPath))
            {
                destinationPath += $"-{Environment.ProcessId}";
            }

            string stagingPath = Path.Combine(backupRoot, $".partial-{name}-{Environment.ProcessId}");

            try
            {
                CopyDirectory(savePath, Path.Combine(stagingPath, "save"));

                if (Directory.Exists(saveMetaPath))
                {
                    CopyDirectory(saveMetaPath, Path.Combine(stagingPath, "saveMeta"));
                }

                if (File.Exists(profilesPath))
                {
                    string systemPath = Path.Combine(stagingPath, "system");
                    Directory.CreateDirectory(systemPath);
                    File.Copy(profilesPath, Path.Combine(systemPath, "Profiles.json"), overwrite: false);
                }

                SaveState copiedState = Measure(Path.Combine(stagingPath, "save"), Path.Combine(stagingPath, "saveMeta"));

                if (copiedState != expectedState)
                {
                    throw new IOException(
                        $"Save backup validation failed: expected {expectedState}, copied {copiedState}.");
                }

                Directory.Move(stagingPath, destinationPath);
                Logger.Info?.Print(
                    LogClass.Application,
                    $"[SAVE-BACKUP] Created {destinationPath} ({expectedState.ContainerCount} containers, {expectedState.ByteCount} bytes).");
            }
            finally
            {
                if (Directory.Exists(stagingPath))
                {
                    Directory.Delete(stagingPath, recursive: true);
                }
            }
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);

            foreach (string directoryPath in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(
                    destinationPath,
                    Path.GetRelativePath(sourcePath, directoryPath)));
            }

            foreach (string filePath in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                string destinationFile = Path.Combine(
                    destinationPath,
                    Path.GetRelativePath(sourcePath, filePath));
                File.Copy(filePath, destinationFile, overwrite: false);
            }
        }

        private static void AtomicWriteAllText(string path, string contents)
        {
            string temporaryPath = path + $".{Environment.ProcessId}.tmp";

            try
            {
                using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new(stream))
                {
                    writer.Write(contents);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static void TrimBackups(string backupRoot)
        {
            foreach (string oldBackup in EnumerateBackups(backupRoot).Skip(MaximumBackups))
            {
                Directory.Delete(oldBackup, recursive: true);
            }
        }
    }
}
