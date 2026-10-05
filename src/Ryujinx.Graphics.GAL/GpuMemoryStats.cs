using System.Threading;

namespace Ryujinx.Graphics.GAL
{
    /// <summary>
    /// NEOV2-GPUMEM1: device memory currently allocated by the host backend, split by resource kind.
    /// Used by the Eden-style GPU memory budget ("Memoria de GPU estilo Eden").
    /// </summary>
    public static class GpuMemoryStats
    {
        public const long MiB = 1024L * 1024;
        public const long GiB = 1024L * MiB;

        // Same budget Eden computes for integrated GPUs (4 GiB, vulkan_device.cpp CollectPhysicalMemoryInfo):
        // the caches start collecting at 1.6 GiB (expected) and become aggressive at 3.2 GiB (critical).
        public const long ExpectedBytes = 1638L * MiB;
        public const long CriticalBytes = 3277L * MiB;

        private static long _bufferBytes;
        private static long _imageBytes;

        // 0 = normal, 1 = Android is low on RAM (collect aggressively), 2 = critical (game is being closed).
        private static int _hostPressureLevel;

        public static long BufferBytes => Interlocked.Read(ref _bufferBytes);
        public static long ImageBytes => Interlocked.Read(ref _imageBytes);
        public static long TotalBytes => BufferBytes + ImageBytes;

        public static int HostPressureLevel
        {
            get => Volatile.Read(ref _hostPressureLevel);
            set => Volatile.Write(ref _hostPressureLevel, value);
        }

        public static void Reset()
        {
            Interlocked.Exchange(ref _bufferBytes, 0);
            Interlocked.Exchange(ref _imageBytes, 0);
        }

        public static void Add(long bytes, bool isBuffer)
        {
            if (isBuffer)
            {
                Interlocked.Add(ref _bufferBytes, bytes);
            }
            else
            {
                Interlocked.Add(ref _imageBytes, bytes);
            }

        }

        /// <summary>
        /// NEOV2-RAMGUARD1: when enabled (with "Memoria de GPU estilo Eden"), a device memory allocation that would
        /// leave Android with less than <see cref="HostRamGuardMinAvailableBytes"/> free fails immediately. The emulator
        /// closes with an error instead of the whole device freezing (AYN Thor with DOOM Eternal needed a reboot and
        /// the freeze corrupted the save index).
        /// </summary>
        public static volatile bool HostRamGuardEnabled;
        public const long HostRamGuardMinAvailableBytes = 1200L * MiB;
        public const long HostRamGuardMinAllocationBytes = 0;

        // NEOV2-RAMGUARD2: MemAvailable is re-read at most every 20 ms, so every allocation can be checked.
        private static long _hostAvailableCache = -1;
        private static long _hostAvailableTicks;

        public static long ReadHostAvailableBytesCached()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();

            if (_hostAvailableTicks != 0 && now - Volatile.Read(ref _hostAvailableTicks) < System.Diagnostics.Stopwatch.Frequency / 50)
            {
                return Volatile.Read(ref _hostAvailableCache);
            }

            long value = ReadHostAvailableBytes();

            Volatile.Write(ref _hostAvailableCache, value);
            Volatile.Write(ref _hostAvailableTicks, now);

            return value;
        }

        /// <summary>
        /// Reads MemAvailable from /proc/meminfo, in bytes. Returns -1 when unavailable.
        /// </summary>
        public static long ReadHostAvailableBytes()
        {
            try
            {
                foreach (string line in System.IO.File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemAvailable:", System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string value = line.Substring("MemAvailable:".Length).Trim();
                    int space = value.IndexOf(' ');

                    if (space > 0)
                    {
                        value = value.Substring(0, space);
                    }

                    return long.TryParse(value, out long kb) ? kb * 1024 : -1;
                }
            }
            catch
            {
            }

            return -1;
        }

        public static string Describe()
        {
            return $"total={TotalBytes / MiB}MB buffers={BufferBytes / MiB}MB images={ImageBytes / MiB}MB";
        }
    }
}
