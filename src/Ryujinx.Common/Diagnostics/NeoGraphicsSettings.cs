namespace Ryujinx.Common.Diagnostics
{
    public enum DrawFlushMode
    {
        /// <summary>Current behavior: Turnip keeps the 4 draw flushes per frame guard, other drivers stay uncapped.</summary>
        Default = 0,

        /// <summary>At most one draw flush per frame, on every driver. All other filters stay in place.</summary>
        Reduced = 1,

        /// <summary>ShouldFlushDraw never forces a flush. Attachment/query/sync/barrier flushes stay active.</summary>
        Disabled = 2,
    }

    public enum BufferReadbackMode
    {
        /// <summary>Current behavior: the CPU waits for the GPU sync before the buffer data is read back.</summary>
        Default = 0,

        /// <summary>The coherency wait is skipped. The readback, the range bookkeeping and every other sync stay in place.</summary>
        Relaxed = 1,
    }

    /// <summary>
    /// Strict host sync experiment mode.
    /// </summary>
    public enum StrictSyncMode
    {
        Default = 0,
        Immediate = 1,
    }

    /// <summary>
    /// Effective (global + per game) experimental graphics settings resolved before the renderer starts.
    /// </summary>
    public static class NeoGraphicsSettings
    {
        // Chave por jogo "Compatibilidade grafica 2D" (correcoes do MARVEL Cosmic Invasion).
        // Definida antes de carregar o jogo. Desligada por padrao em todos os jogos.
        private static bool _experimentalGameFixesEnabled;

        public static bool ExperimentalGameFixesEnabled
        {
            get => System.Threading.Volatile.Read(ref _experimentalGameFixesEnabled);
            set => System.Threading.Volatile.Write(ref _experimentalGameFixesEnabled, value);
        }

        /// <summary>Value the renderer uses.</summary>
        public static DrawFlushMode DrawFlushMode { get; set; } = DrawFlushMode.Default;

        /// <summary>Global value, kept only for the startup log line.</summary>
        public static DrawFlushMode DrawFlushGlobalMode { get; set; } = DrawFlushMode.Default;

        /// <summary>Per game override as selected by the user, or null when "use global" is selected. Log only.</summary>
        public static DrawFlushMode? DrawFlushGameOverride { get; set; }

        /// <summary>Value the GPU buffer cache uses.</summary>
        public static BufferReadbackMode BufferReadbackMode { get; set; } = BufferReadbackMode.Default;

        /// <summary>Global value, kept only for the startup log line.</summary>
        public static BufferReadbackMode BufferReadbackGlobalMode { get; set; } = BufferReadbackMode.Default;

        /// <summary>Per game override as selected by the user, or null when "use global" is selected. Log only.</summary>
        public static BufferReadbackMode? BufferReadbackGameOverride { get; set; }

        /// <summary>Value the Vulkan sync manager uses.</summary>
        public static StrictSyncMode StrictSyncMode { get; set; } = StrictSyncMode.Default;

        /// <summary>Global value, kept only for the startup log line.</summary>
        public static StrictSyncMode StrictSyncGlobalMode { get; set; } = StrictSyncMode.Default;

        /// <summary>Per game override as selected by the user, or null when "use global" is selected. Log only.</summary>
        public static StrictSyncMode? StrictSyncGameOverride { get; set; }

        // Opcao por jogo "Otimizacoes de GPU": desligada, estas otimizacoes voltam ao comportamento anterior.
        // Todas com padrao ligado. Lidas quando o renderer e criado.

        /// <summary>TURNIP-FENCE1: VkFence no lugar da timeline no Turnip.</summary>
        public static bool TurnipFenceEnabled { get; set; } = true;

        /// <summary>CONDRENDER1: renderizacao condicional sem esperar a leitura na CPU (Turnip/Qualcomm).</summary>
        public static bool ConditionalRenderNoWaitEnabled { get; set; } = true;

        /// <summary>CMDFETCH1: listas de comando do GPFIFO lidas sem flush.</summary>
        public static bool CommandFetchWithoutFlushEnabled { get; set; } = true;

        /// <summary>TEXBUF-ALIGN1: offset exato da view de texture buffer no Turnip (correcao; o app mantem sempre ligado).</summary>
        public static bool TexelBufferExactOffsetEnabled { get; set; } = true;

        /// <summary>QUEUE-HYST1: histerese na fila do ThreadedRenderer.</summary>
        public static bool QueueHysteresisEnabled { get; set; } = true;

        /// <summary>TIMELINE-LAZY1: consulta oportunista de fence so pelo valor ja conhecido.</summary>
        public static bool TimelineLazyEnabled { get; set; } = true;

        /// <summary>Trava do StagingBuffer.</summary>
        public static bool StagingBufferLockEnabled { get; set; } = true;

        private static bool _nifmOfflineEnabled;

        public static bool NifmOfflineEnabled
        {
            get => _nifmOfflineEnabled;
            set => _nifmOfflineEnabled = value;
        }

        /// <summary>WEBOFF1: opcao por jogo "Termos offline" (applet offline conclui a tela de termos, ex.: BioShock). Desligada por padrao.</summary>
        public static bool WebOfflineTermsEnabled { get; set; }

        /// <summary>TexturePool mantem invalido o slot com endereco 0 ao ser modificado.</summary>
        public static bool TexturePoolKeepInvalidEnabled { get; set; } = true;

        /// <summary>
        /// NEOV2-VP9EDEN1: chave geral "VP9 pelo FFmpeg (Eden)". Decodifica os videos VP9 com o FFmpeg,
        /// recompondo os cabecalhos como o Eden. Desligada por padrao; desligada, usa o decodificador em C#.
        /// </summary>
        public static bool Vp9FFmpegEnabled { get; set; }
    }
}