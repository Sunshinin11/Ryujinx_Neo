using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Common.Diagnostics
{
    /// <summary>
    /// NEO-HITCH (diagnostic): per-second counters of work that can cause frame hitches.
    /// Enabled with RYUJINX_NEO_FPS_LOG=1; read and reset by the FPS log in AppHost.
    /// </summary>
    public static class NeoHitchStats
    {
        public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("RYUJINX_NEO_FPS_LOG") == "1";

        private static long _gfxPipelines;
        private static long _gfxPipelineTicks;
        private static long _shaderTranslations;
        private static long _textures;
        private static long _textureUploads;
        private static long _textureUploadBytes;
        private static long _convertTicks;
        private static long _createTicks;
        private static long _beUploadTicks;
        private static long _beCreateTicks;

        public static void AddBackendUpload(long startTimestamp) => Interlocked.Add(ref _beUploadTicks, Stopwatch.GetTimestamp() - startTimestamp);

        public static void AddBackendCreate(long startTimestamp) => Interlocked.Add(ref _beCreateTicks, Stopwatch.GetTimestamp() - startTimestamp);

        public static void AddConvert(long startTimestamp) => Interlocked.Add(ref _convertTicks, Stopwatch.GetTimestamp() - startTimestamp);

        public static void AddCreate(long startTimestamp) => Interlocked.Add(ref _createTicks, Stopwatch.GetTimestamp() - startTimestamp);

        public static void AddGraphicsPipeline(long startTimestamp)
        {
            Interlocked.Increment(ref _gfxPipelines);
            Interlocked.Add(ref _gfxPipelineTicks, Stopwatch.GetTimestamp() - startTimestamp);
        }

        public static void AddShaderTranslation() => Interlocked.Increment(ref _shaderTranslations);

        public static void AddTexture() => Interlocked.Increment(ref _textures);

        public static void AddTextureUpload(long bytes)
        {
            Interlocked.Increment(ref _textureUploads);
            Interlocked.Add(ref _textureUploadBytes, bytes);
        }

        private static readonly long[] _stepTicks = new long[6];

        /// <summary>Backend texture creation steps: 0=CreateImage 1=Allocate 2=Bind 3=Transition 4=Views 5=Other.</summary>
        public static void AddStep(int step, long startTimestamp) => Interlocked.Add(ref _stepTicks[step], Stopwatch.GetTimestamp() - startTimestamp);

        public static string SnapshotAndReset()
        {
            long p = Interlocked.Exchange(ref _gfxPipelines, 0);
            long pt = Interlocked.Exchange(ref _gfxPipelineTicks, 0);
            long s = Interlocked.Exchange(ref _shaderTranslations, 0);
            long t = Interlocked.Exchange(ref _textures, 0);
            long u = Interlocked.Exchange(ref _textureUploads, 0);
            long ub = Interlocked.Exchange(ref _textureUploadBytes, 0);
            long ct = Interlocked.Exchange(ref _convertTicks, 0);
            long cr = Interlocked.Exchange(ref _createTicks, 0);
            long bu = Interlocked.Exchange(ref _beUploadTicks, 0);
            long bc = Interlocked.Exchange(ref _beCreateTicks, 0);
            string steps = "";
            for (int i = 0; i < _stepTicks.Length; i++)
            {
                steps += $"{(i == 0 ? " steps=" : "/")}{Interlocked.Exchange(ref _stepTicks[i], 0) * 1000.0 / Stopwatch.Frequency:F1}";
            }

            return $"pipe={p} pipems={pt * 1000.0 / Stopwatch.Frequency:F1} shaders={s} tex={t} up={u} upKB={ub / 1024} convms={ct * 1000.0 / Stopwatch.Frequency:F1} createms={cr * 1000.0 / Stopwatch.Frequency:F1} beupms={bu * 1000.0 / Stopwatch.Frequency:F1} becreatems={bc * 1000.0 / Stopwatch.Frequency:F1}{steps}";
        }
    }
}
