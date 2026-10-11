namespace Ryujinx.Graphics.Gpu
{
#pragma warning disable CA2211 // Non-constant fields should not be visible
    /// <summary>
    /// General GPU and graphics configuration.
    /// </summary>
    public static class GraphicsConfig
    {
        /// <summary>
        /// Resolution scale.
        /// </summary>
        public static float ResScale = 1f;

        /// <summary>
        /// Max Anisotropy. Values range from 0 - 16. Set to -1 to let the game decide.
        /// </summary>
        public static float MaxAnisotropy = -1;

        /// <summary>
        /// Base directory used to write shader code dumps.
        /// Set to null to disable code dumping.
        /// </summary>
        public static string ShadersDumpPath;

        /// <summary>
        /// Fast GPU time calculates the internal GPU time ticks as if the GPU was capable of
        /// processing commands almost instantly, instead of using the host timer.
        /// This can avoid lower resolution on some games when GPU performance is poor.
        /// </summary>
        public static bool FastGpuTime = true;

        /// <summary>
        /// Enables or disables fast 2d engine texture copies entirely on CPU when possible.
        /// Reduces stuttering and # of textures in games that copy textures around for streaming,
        /// as textures will not need to be created for the copy, and the data does not need to be
        /// flushed from GPU.
        /// </summary>
        public static bool Fast2DCopy = true;

        /// <summary>
        /// Enables or disables the Just-in-Time compiler for GPU Macro code.
        /// </summary>
        public static bool EnableMacroJit = true;

        /// <summary>
        /// Enables or disables high-level emulation of common GPU Macro code.
        /// </summary>
        public static bool EnableMacroHLE = true;

        /// <summary>
        /// Title id of the current running game.
        /// Used by the shader cache.
        /// </summary>
        private static string _titleId;

        public static string TitleId
        {
            get => _titleId;
            set
            {
                _titleId = value;
                Ryujinx.Graphics.GAL.GraphicsCompatibility.TitleId = value;
            }
        }

        /// <summary>
        /// Enables or disables the shader cache.
        /// </summary>
        public static bool EnableShaderCache;

        /// <summary>
        /// Limits shader loops to a finite per-invocation budget. This is a compatibility
        /// workaround for malformed shaders and is disabled by default because legitimate
        /// long-running loops can otherwise produce graphical corruption.
        /// </summary>
        public static bool EnableShaderLoopSafety;

        /// <summary>
        /// Enables or disables shader SPIR-V compilation.
        /// </summary>
        public static bool EnableSpirvCompilationOnVulkan = true;

        /// <summary>
        /// Enables or disables recompression of compressed textures that are not natively supported by the host.
        /// </summary>
        public static bool EnableTextureRecompression = false;

        /// <summary>
        /// Enables or disables color space passthrough, if available.
        /// </summary>
        public static bool EnableColorSpacePassthrough = false;

        /// <summary>
        /// Executes vertex and geometry pipelines via Compute when required.
        /// </summary>
        public static bool EnableVtgViaCompute = true;
        /// <summary>
        /// NEOV2 DMA-RELAX1: mantém DMA copies no lado GPU quando habilitado.
        /// Desligado por padrão pois pode expor dados stale em texturas sobrepostas.
        /// </summary>
        public static bool RelaxedDmaCopy;

        /// <summary>
        /// Rejects texture descriptors pointing to unmapped guest memory.
        /// OFF by default globally in Current. Descriptor address 0 is always rejected regardless of this setting.
        /// </summary>
        public static bool RejectUnmappedTextureDescriptors;

        /// <summary>
        /// Enables asynchronous conversion and upload of large 3D textures (>= 4 MiB), like Eden's QueueAsyncUnswizzle.
        /// ON by default.
        /// </summary>
        public static bool AsyncLarge3DTextureUpload = true;

    }
#pragma warning restore CA2211
}
