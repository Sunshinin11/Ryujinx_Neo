namespace Ryujinx.Common.Diagnostics
{
    public enum DrawFlushMode
    {
        Default = 0,
        Reduced = 1,
        Disabled = 2,
    }

    public enum BufferReadbackMode
    {
        Default = 0,
        Relaxed = 1,
        Hybrid = 2,
    }

    public enum StrictSyncMode
    {
        Default = 0,
        Immediate = 1,
    }

    public static class NeoGraphicsSettings
    {
        private static bool _experimentalGameFixesEnabled;

        public static bool ExperimentalGameFixesEnabled
        {
            get => System.Threading.Volatile.Read(ref _experimentalGameFixesEnabled);
            set => System.Threading.Volatile.Write(ref _experimentalGameFixesEnabled, value);
        }

        public static DrawFlushMode DrawFlushMode { get; set; } = DrawFlushMode.Default;
        public static DrawFlushMode DrawFlushGlobalMode { get; set; } = DrawFlushMode.Default;
        public static DrawFlushMode? DrawFlushGameOverride { get; set; }

        public static BufferReadbackMode BufferReadbackMode { get; set; } = BufferReadbackMode.Default;
        public static BufferReadbackMode BufferReadbackGlobalMode { get; set; } = BufferReadbackMode.Default;
        public static BufferReadbackMode? BufferReadbackGameOverride { get; set; }

        public static StrictSyncMode StrictSyncMode { get; set; } = StrictSyncMode.Default;
        public static StrictSyncMode StrictSyncGlobalMode { get; set; } = StrictSyncMode.Default;
        public static StrictSyncMode? StrictSyncGameOverride { get; set; }

        public static bool TurnipFenceEnabled { get; set; } = true;
        public static bool TurnipTimelineBatchingEnabled { get; set; }
        public static bool ConditionalRenderNoWaitEnabled { get; set; } = true;
        public static bool CommandFetchWithoutFlushEnabled { get; set; } = true;
        public static bool TexelBufferExactOffsetEnabled { get; set; } = true;
        public static bool QueueHysteresisEnabled { get; set; } = true;
        public static bool TimelineLazyEnabled { get; set; } = true;
        public static bool StagingBufferLockEnabled { get; set; } = true;
        private static bool _nifmOfflineEnabled;

        public static bool NifmOfflineEnabled
        {
            get => _nifmOfflineEnabled;
            set => _nifmOfflineEnabled = value;
        }

        public static bool TexturePoolKeepInvalidEnabled { get; set; } = true;
    }
}
