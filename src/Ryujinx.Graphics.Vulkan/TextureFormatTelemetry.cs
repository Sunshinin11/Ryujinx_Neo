using Ryujinx.Common.Diagnostics;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    /// <summary>
    /// NEOV2 Etapa 4A: telemetria somente de leitura. Mede quais formatos de view cada imagem recebe,
    /// para decidir com seguranca onde VkImageFormatListCreateInfo pode devolver o UBWC no Turnip.
    /// Nao altera nenhum comportamento de renderizacao.
    /// </summary>
    internal static class TextureFormatTelemetry
    {
        // Bits de TextureStorage.TelemetryFlags.
        public const int FlagSrgbPair = 1;   // view difere apenas em UNORM/SRGB
        public const int FlagSwap = 2;       // view difere em ordem de componentes (RGBA/BGRA)
        public const int FlagOther = 4;      // view com formato de outra natureza (reinterpretacao)
        public const int FlagAliased = 8;    // imagem aliased na mesma memoria com outro formato

        // Buckets: 0 = formato unico, 1 = so par UNORM/SRGB, 2 = inseguro para lista curta.
        private const int BucketCount = 3;

        private static readonly long[] _liveCount = new long[BucketCount];
        private static readonly long[] _liveBytes = new long[BucketCount];
        private static readonly long[] _liveRtCount = new long[BucketCount];
        private static readonly long[] _liveRtBytes = new long[BucketCount];

        private static long _storagesCreated;
        private static long _rtCreated;
        private static long _aliasImagesCreated;
        private static long _eventsSrgbPair;
        private static long _eventsSwap;
        private static long _eventsOther;

        private static readonly Dictionary<(VkFormat, VkFormat), int> _pairs = new();
        private static readonly Lock _pairsLock = new();

        private static long _lastLogTimestamp = Stopwatch.GetTimestamp();
        private static int _isLogging;

        public static int Bucket(int flags)
        {
            if ((flags & (FlagSwap | FlagOther | FlagAliased)) != 0)
            {
                return 2;
            }

            return (flags & FlagSrgbPair) != 0 ? 1 : 0;
        }

        public static void RecordStorageCreated(long bytes)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            Interlocked.Increment(ref _storagesCreated);
            Interlocked.Increment(ref _liveCount[0]);
            Interlocked.Add(ref _liveBytes[0], bytes);
        }

        /// <summary>
        /// Chamado na primeira vez que a imagem e usada como attachment de um render pass.
        /// </summary>
        public static void RecordBecameRenderTarget(long bytes, int flags)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            int bucket = Bucket(flags);

            Interlocked.Increment(ref _rtCreated);
            Interlocked.Increment(ref _liveRtCount[bucket]);
            Interlocked.Add(ref _liveRtBytes[bucket], bytes);
        }

        public static void RecordAliasImageCreated()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            Interlocked.Increment(ref _aliasImagesCreated);
        }

        public static void RecordStorageDisposed(bool isRenderTarget, long bytes, int flags)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            int bucket = Bucket(flags);

            Interlocked.Decrement(ref _liveCount[bucket]);
            Interlocked.Add(ref _liveBytes[bucket], -bytes);

            if (isRenderTarget)
            {
                Interlocked.Decrement(ref _liveRtCount[bucket]);
                Interlocked.Add(ref _liveRtBytes[bucket], -bytes);
            }
        }

        public static void RecordFlagsChanged(bool isRenderTarget, long bytes, int oldFlags, int newFlags)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            int oldBucket = Bucket(oldFlags);
            int newBucket = Bucket(newFlags);

            if (oldBucket == newBucket)
            {
                return;
            }

            Interlocked.Decrement(ref _liveCount[oldBucket]);
            Interlocked.Add(ref _liveBytes[oldBucket], -bytes);
            Interlocked.Increment(ref _liveCount[newBucket]);
            Interlocked.Add(ref _liveBytes[newBucket], bytes);

            if (isRenderTarget)
            {
                Interlocked.Decrement(ref _liveRtCount[oldBucket]);
                Interlocked.Add(ref _liveRtBytes[oldBucket], -bytes);
                Interlocked.Increment(ref _liveRtCount[newBucket]);
                Interlocked.Add(ref _liveRtBytes[newBucket], bytes);
            }
        }

        /// <summary>
        /// Classifica a diferenca entre o formato da imagem e o formato de uma view.
        /// Retorna 0 quando sao iguais.
        /// </summary>
        public static int Classify(VkFormat storageFormat, VkFormat viewFormat)
        {
            if (storageFormat == viewFormat)
            {
                return 0;
            }

            lock (_pairsLock)
            {
                var key = (storageFormat, viewFormat);
                _pairs.TryGetValue(key, out int count);
                _pairs[key] = count + 1;
            }

            string a = storageFormat.ToString().Replace("Srgb", "Unorm");
            string b = viewFormat.ToString().Replace("Srgb", "Unorm");

            if (a == b)
            {
                Interlocked.Increment(ref _eventsSrgbPair);
                return FlagSrgbPair;
            }

            if (a.Replace("B8G8R8A8", "R8G8B8A8") == b.Replace("B8G8R8A8", "R8G8B8A8"))
            {
                Interlocked.Increment(ref _eventsSwap);
                return FlagSwap;
            }

            Interlocked.Increment(ref _eventsOther);
            return FlagOther;
        }

        public static void TryFlush()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            long now = Stopwatch.GetTimestamp();

            if (now - Volatile.Read(ref _lastLogTimestamp) < Stopwatch.Frequency * 5)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _isLogging, 1, 0) != 0)
            {
                return;
            }

            try
            {
                if (now - Volatile.Read(ref _lastLogTimestamp) >= Stopwatch.Frequency * 5)
                {
                    Volatile.Write(ref _lastLogTimestamp, now);
                    Log();
                }
            }
            finally
            {
                Volatile.Write(ref _isLogging, 0);
            }
        }

        private static double Mb(long bytes) => bytes / (1024.0 * 1024.0);

        private static void Log()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            StringBuilder top = new();

            lock (_pairsLock)
            {
                List<KeyValuePair<(VkFormat, VkFormat), int>> list = new(_pairs);
                list.Sort((x, y) => y.Value.CompareTo(x.Value));

                for (int i = 0; i < list.Count && i < 8; i++)
                {
                    if (i != 0)
                    {
                        top.Append(',');
                    }

                    top.Append(list[i].Key.Item1).Append("->").Append(list[i].Key.Item2).Append(':').Append(list[i].Value);
                }
            }

            string message = string.Format(
                CultureInfo.InvariantCulture,
                "[NEOV2-ETAPA4A] created={0} rtUsed={1} aliasImages={2} viewEvents srgbPair={3} swap={4} other={5} " +
                "liveRt unique={6}/{7:F1}MB srgbPair={8}/{9:F1}MB unsafe={10}/{11:F1}MB " +
                "liveAll unique={12}/{13:F1}MB srgbPair={14}/{15:F1}MB unsafe={16}/{17:F1}MB top=[{18}]",
                Volatile.Read(ref _storagesCreated),
                Volatile.Read(ref _rtCreated),
                Volatile.Read(ref _aliasImagesCreated),
                Volatile.Read(ref _eventsSrgbPair),
                Volatile.Read(ref _eventsSwap),
                Volatile.Read(ref _eventsOther),
                Volatile.Read(ref _liveRtCount[0]), Mb(Volatile.Read(ref _liveRtBytes[0])),
                Volatile.Read(ref _liveRtCount[1]), Mb(Volatile.Read(ref _liveRtBytes[1])),
                Volatile.Read(ref _liveRtCount[2]), Mb(Volatile.Read(ref _liveRtBytes[2])),
                Volatile.Read(ref _liveCount[0]), Mb(Volatile.Read(ref _liveBytes[0])),
                Volatile.Read(ref _liveCount[1]), Mb(Volatile.Read(ref _liveBytes[1])),
                Volatile.Read(ref _liveCount[2]), Mb(Volatile.Read(ref _liveBytes[2])),
                top.ToString());

            Logger.Info?.Print(LogClass.Gpu, message);
        }
    }
}
