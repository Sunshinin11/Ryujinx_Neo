namespace Ryujinx.Common.Diagnostics
{
    /// <summary>
    /// Compile-time switches for the experimental renderer path.
    /// Telemetry remains opt-in while the synchronization and pipeline safeguards
    /// are enabled for every title.
    /// </summary>
    public static class GezinjinxPerformanceMode
    {
        // Enabled in the experimental branch so ADB runs expose queue, pipeline
        // and synchronization behavior. Stable/release variants keep this off.
        public static readonly bool DetailedTelemetry = false;

        /// <summary>
        /// Keep strict host sync markers attached to the command buffer that is already
        /// being recorded instead of forcing an immediate command-buffer split.  The
        /// marker still becomes a real GPU waitable; a consumer that needs it flushes the
        /// in-use command buffer before waiting.  This is intentionally enabled for the
        /// experimental Neo path because strict syncpoint traffic was turning into a
        /// submit/flush bubble on every marker.
        /// </summary>
        public const bool AggressiveSyncMarkers = true;

        /// <summary>
        /// Prevent the first draw from racing the program's background pipeline compile.
        /// The exact state is still compiled on demand when it differs from the warm-up key.
        /// </summary>
        public const bool AggressivePipelineWarmup = true;

        private static bool? _enableA21StateDeduplication;

        /// <summary>
        /// A21: Enable semantic-state generation guard for external StateUpdater deduplication.
        /// When disabled, StateUpdater always re-emits shader bindings and program (A20 unconditional behavior).
        /// </summary>
        public static bool EnableA21StateDeduplication
        {
            get
            {
                if (_enableA21StateDeduplication.HasValue)
                {
                    return _enableA21StateDeduplication.Value;
                }

                bool enabled = true;
                try
                {
                    string env = System.Environment.GetEnvironmentVariable("RYUJINX_A21_DEDUP");
                    if (env is "0" or "false" or "off")
                    {
                        enabled = false;
                    }
                    else
                    {
                        string flagsPath = "/storage/emulated/0/Android/data/org.kenjinx.android/files/neo_flags.txt";
                        if (System.IO.File.Exists(flagsPath))
                        {
                            string text = System.IO.File.ReadAllText(flagsPath);
                            if (text.Contains("a21_dedup=0", System.StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("a21_dedup=false", System.StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("a21_dedup=off", System.StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("dedup=0", System.StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("dedup=false", System.StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("dedup=off", System.StringComparison.OrdinalIgnoreCase))
                            {
                                enabled = false;
                            }
                        }
                    }
                }
                catch
                {
                }

                _enableA21StateDeduplication = enabled;
                return enabled;
            }
            set => _enableA21StateDeduplication = value;
        }
    }
}
