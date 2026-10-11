using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// NEOV2 Etapa 6: persiste em disco o VkPipelineCache do pipeline principal.
    /// Na proxima execucao o driver reaproveita os pipelines ja compilados, reduzindo o
    /// stutter de compilacao. Dados incompativeis (outro driver/GPU) sao descartados pelo proprio driver.
    /// </summary>
    internal static class PipelineCachePersistence
    {
        private const long MaxFileBytes = 512L * 1024 * 1024;
        private const int SaveIntervalSeconds = 60;
        private const string FileName = "neov2_vk_pipeline_cache.bin";

        private static readonly object _lock = new();

        private static Vk _api;
        private static Device _device;
        private static PipelineCache _cache;
        private static bool _active;

        private static long _pipelinesCreated;
        private static long _pipelinesAtLastSave;
        private static long _lastSaveTimestamp;
        private static int _saving;

        private static string GetFilePath()
        {
            string baseDir = AppDataManager.BaseDirPath;

            if (string.IsNullOrEmpty(baseDir))
            {
                return null;
            }

            return Path.Combine(baseDir, "system", FileName);
        }

        private static byte[] TryLoad(string path)
        {
            if (path == null)
            {
                return null;
            }

            try
            {
                FileInfo info = new(path);

                if (!info.Exists || info.Length == 0)
                {
                    return null;
                }

                if (info.Length > MaxFileBytes)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[NEOV2-ETAPA6] Pipeline cache file too large ({info.Length / (1024 * 1024)} MiB), discarding.");
                    File.Delete(path);
                    return null;
                }

                return File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[NEOV2-ETAPA6] Failed to read pipeline cache: {ex.Message}");
                return null;
            }
        }

        public static unsafe PipelineCache Create(Vk api, Device device)
        {
            string path = GetFilePath();
            byte[] data = TryLoad(path);
            int loadedBytes = data?.Length ?? 0;

            PipelineCache cache;

            fixed (byte* pData = data)
            {
                PipelineCacheCreateInfo createInfo = new()
                {
                    SType = StructureType.PipelineCacheCreateInfo,
                    InitialDataSize = (nuint)loadedBytes,
                    PInitialData = loadedBytes != 0 ? pData : null,
                };

                Result result = api.CreatePipelineCache(device, in createInfo, null, out cache);

                if (result != Result.Success && loadedBytes != 0)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[NEOV2-ETAPA6] Driver rejected saved pipeline cache ({result}), starting empty.");

                    createInfo.InitialDataSize = 0;
                    createInfo.PInitialData = null;
                    loadedBytes = 0;

                    api.CreatePipelineCache(device, in createInfo, null, out cache).ThrowOnError();
                }
                else
                {
                    result.ThrowOnError();
                }
            }

            lock (_lock)
            {
                _api = api;
                _device = device;
                _cache = cache;
                _active = path != null;
                _pipelinesCreated = 0;
                _pipelinesAtLastSave = 0;
                _lastSaveTimestamp = Stopwatch.GetTimestamp();
            }

            Logger.Info?.Print(LogClass.Gpu, $"[NEOV2-ETAPA6] Pipeline cache loaded: {loadedBytes / 1024} KiB ({path ?? "no path"})");
            Logger.Info?.Print(LogClass.Gpu, "[NEOV2-PIPELINE-EXIT1] Periodic in-game pipeline cache saves disabled; cache will be saved on renderer shutdown.");

            return cache;
        }

        public static void RecordPipelineCreated()
        {
            Interlocked.Increment(ref _pipelinesCreated);
        }

        /// <summary>
        /// Chamado periodicamente. Salva em segundo plano no maximo a cada 60 s, e so se houver pipelines novos.
        /// </summary>
        public static void TryPeriodicSave()
        {
            if (!Volatile.Read(ref _active))
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();

            if (now - Volatile.Read(ref _lastSaveTimestamp) < Stopwatch.Frequency * SaveIntervalSeconds)
            {
                return;
            }

            if (Volatile.Read(ref _pipelinesCreated) == Volatile.Read(ref _pipelinesAtLastSave))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0)
            {
                return;
            }

            Volatile.Write(ref _lastSaveTimestamp, now);

            Task.Run(() =>
            {
                try
                {
                    lock (_lock)
                    {
                        if (_active)
                        {
                            SaveLocked();
                        }
                    }
                }
                finally
                {
                    Volatile.Write(ref _saving, 0);
                }
            });
        }

        /// <summary>
        /// Chamado antes de destruir o cache: salva o que for novo e desativa a persistencia.
        /// </summary>
        public static void SaveAndRelease(PipelineCache cache)
        {
            lock (_lock)
            {
                if (!_active || _cache.Handle != cache.Handle)
                {
                    return;
                }

                if (Volatile.Read(ref _pipelinesCreated) != _pipelinesAtLastSave)
                {
                    SaveLocked();
                }

                _active = false;
            }
        }

        private static unsafe void SaveLocked()
        {
            string path = GetFilePath();

            if (path == null)
            {
                return;
            }

            long created = Volatile.Read(ref _pipelinesCreated);
            long start = Stopwatch.GetTimestamp();

            try
            {
                nuint size = 0;
                Result result = _api.GetPipelineCacheData(_device, _cache, &size, null);

                if (result != Result.Success || size == 0 || (long)size > MaxFileBytes)
                {
                    return;
                }

                byte[] buffer = new byte[(long)size];

                fixed (byte* pBuffer = buffer)
                {
                    result = _api.GetPipelineCacheData(_device, _cache, &size, pBuffer);
                }

                if (result != Result.Success)
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));

                string tempPath = path + ".tmp";

                using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(buffer, 0, (int)size);
                    stream.Flush(true);
                }

                File.Move(tempPath, path, true);

                _pipelinesAtLastSave = created;

                double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                Logger.Info?.Print(LogClass.Gpu, $"[NEOV2-ETAPA6] Pipeline cache saved: {(long)size / 1024} KiB, {created} new pipelines, {ms:F1} ms");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[NEOV2-ETAPA6] Failed to save pipeline cache: {ex.Message}");
            }
        }
    }
}
