using LibKenjinx.Android;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Diagnostics;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.OpenGL;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.HLE;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LibKenjinx
{
    public static partial class LibKenjinx
    {
        private static bool _isActive;
        private static bool _isStopped;
        private static CancellationTokenSource? _gpuCancellationTokenSource;
        private static SwapBuffersCallback? _swapBuffersCallback;
        private static NativeGraphicsInterop _nativeGraphicsInterop;
        private static ManualResetEvent? _gpuDoneEvent;
        private static bool _enableGraphicsLogging;

        public delegate void SwapBuffersCallback();
        public delegate nint GetProcAddress(string name);
        public delegate nint CreateSurface(nint instance);

        public static IRenderer? Renderer { get; set; }
        public static GraphicsConfiguration GraphicsConfiguration { get; private set; }

        public static bool InitializeGraphics(GraphicsConfiguration graphicsConfiguration)
        {
            GraphicsConfig.ResScale = graphicsConfiguration.ResScale;
            GraphicsConfig.MaxAnisotropy = graphicsConfiguration.MaxAnisotropy;
            GraphicsConfig.FastGpuTime = graphicsConfiguration.FastGpuTime;
            GraphicsConfig.Fast2DCopy = graphicsConfiguration.Fast2DCopy;
            GraphicsConfig.EnableMacroJit = graphicsConfiguration.EnableMacroJit;
            GraphicsConfig.EnableMacroHLE = graphicsConfiguration.EnableMacroHLE;
            GraphicsConfig.EnableShaderCache = graphicsConfiguration.EnableShaderCache;
            GraphicsConfig.EnableShaderLoopSafety = graphicsConfiguration.EnableShaderLoopSafety;
            GraphicsConfig.EnableTextureRecompression = graphicsConfiguration.EnableTextureRecompression;
            GraphicsConfig.EnableVtgViaCompute = graphicsConfiguration.EnableVtgViaCompute;

            // NEOV2 AUTOFLUSH1: modo do Fast Draw Flush ja resolvido (global + override por jogo) na camada Android.
            DrawFlushMode drawFlushMode = graphicsConfiguration.DrawFlushMode switch
            {
                1 => DrawFlushMode.Reduced,
                2 => DrawFlushMode.Disabled,
                _ => DrawFlushMode.Default,
            };

            DrawFlushMode drawFlushGlobalMode = graphicsConfiguration.DrawFlushGlobalMode switch
            {
                1 => DrawFlushMode.Reduced,
                2 => DrawFlushMode.Disabled,
                _ => DrawFlushMode.Default,
            };

            NeoGraphicsSettings.DrawFlushMode = drawFlushMode;
            NeoGraphicsSettings.DrawFlushGlobalMode = drawFlushGlobalMode;
            NeoGraphicsSettings.DrawFlushGameOverride = graphicsConfiguration.DrawFlushGameOverride < 0
                ? null
                : drawFlushMode;

            Logger.Info?.Print(
                Ryujinx.Common.Logging.LogClass.Gpu,
                $"[NEOV2-DRAW-FLUSH] global={drawFlushGlobalMode} gameOverride={(graphicsConfiguration.DrawFlushGameOverride < 0 ? "Global" : drawFlushMode.ToString())} effective={drawFlushMode}");

            // NEOV2 BUFFER-READBACK1: modo de leitura de buffer ja resolvido (global + override por jogo).
            BufferReadbackMode bufferReadbackMode = graphicsConfiguration.BufferReadbackMode == 1
                ? BufferReadbackMode.Relaxed
                : BufferReadbackMode.Default;

            BufferReadbackMode bufferReadbackGlobalMode = graphicsConfiguration.BufferReadbackGlobalMode == 1
                ? BufferReadbackMode.Relaxed
                : BufferReadbackMode.Default;

            NeoGraphicsSettings.BufferReadbackMode = bufferReadbackMode;
            NeoGraphicsSettings.BufferReadbackGlobalMode = bufferReadbackGlobalMode;
            NeoGraphicsSettings.BufferReadbackGameOverride = graphicsConfiguration.BufferReadbackGameOverride < 0
                ? null
                : bufferReadbackMode;

            Logger.Info?.Print(
                Ryujinx.Common.Logging.LogClass.Gpu,
                $"[NEOV2-BUFFER-READBACK] global={bufferReadbackGlobalMode} gameOverride={(graphicsConfiguration.BufferReadbackGameOverride < 0 ? "Global" : bufferReadbackMode.ToString())} effective={bufferReadbackMode}");

            // NEOV2 STRICT-SYNC1: modo de sincronizacao estrita ja resolvido (global + override por jogo).
            StrictSyncMode strictSyncMode = graphicsConfiguration.StrictSyncMode == 1
                ? StrictSyncMode.Immediate
                : StrictSyncMode.Default;

            StrictSyncMode strictSyncGlobalMode = graphicsConfiguration.StrictSyncGlobalMode == 1
                ? StrictSyncMode.Immediate
                : StrictSyncMode.Default;

            NeoGraphicsSettings.StrictSyncMode = strictSyncMode;
            NeoGraphicsSettings.StrictSyncGlobalMode = strictSyncGlobalMode;
            NeoGraphicsSettings.StrictSyncGameOverride = graphicsConfiguration.StrictSyncGameOverride < 0
                ? null
                : strictSyncMode;

            Logger.Info?.Print(
                Ryujinx.Common.Logging.LogClass.Gpu,
                $"[NEOV2-STRICT-SYNC] global={strictSyncGlobalMode} gameOverride={(graphicsConfiguration.StrictSyncGameOverride < 0 ? "Global" : strictSyncMode.ToString())} effective={strictSyncMode}");

            Logger.Info?.Print(
                Ryujinx.Common.Logging.LogClass.Gpu,
                graphicsConfiguration.EnableShaderLoopSafety
                    ? "[NEOV2-LOOP-GUARD] enabled; limit=8192"
                    : "[NEOV2-LOOP-GUARD] disabled");

            GraphicsConfiguration = graphicsConfiguration;

            return true;
        }

        public static bool InitializeGraphicsRenderer(GraphicsBackend graphicsBackend, CreateSurface? createSurfaceFunc, string?[] requiredExtensions)
        {
            if (Renderer != null)
            {
                return false;
            }

            if (graphicsBackend == GraphicsBackend.OpenGl)
            {
                Renderer = new OpenGLRenderer();
            }
            else if (graphicsBackend == GraphicsBackend.Vulkan)
            {
                // Prefer the platform-provided Vulkan loader (if present), fall back to default.
                Vk? api = VulkanLoader?.GetApi() ?? Vk.GetApi();

                Renderer = new VulkanRenderer(
                    api,
                    (instance, _) =>
                    {
                        // use provided CreateSurface delegate (Android path will create ANativeWindow surface)
                        return new SurfaceKHR(createSurfaceFunc == null ? null : (ulong?)createSurfaceFunc(instance.Handle));
                    },
                    () => requiredExtensions,
                    null);
            }
            else
            {
                return false;
            }

            return true;
        }

        public static void SetRendererSize(int width, int height)
        {
            Renderer?.Window?.SetSize(width, height);
        }

        public static void SetVsyncState(Ryujinx.Common.Configuration.VSyncMode vSyncMode)
        {
            Switch device = SwitchDevice!.EmulationContext!;
            device.VSyncMode = vSyncMode;
            device.Gpu.Renderer.Window.ChangeVSyncMode((Ryujinx.Graphics.GAL.VSyncMode)vSyncMode);
        }

        public static void RunLoop()
        {
            if (Renderer == null)
            {
                return;
            }
            ARMeilleure.Optimizations.EcoFriendly = SwitchDevice!.EnableLowPowerPtc;
            ARMeilleure.Optimizations.CacheEviction = SwitchDevice.EnableJitCacheEviction;

            Switch device = SwitchDevice.EmulationContext!;
            _gpuDoneEvent = new ManualResetEvent(true);

            device.Gpu.Renderer.Initialize(_enableGraphicsLogging ? GraphicsDebugLevel.All : GraphicsDebugLevel.None);

            _gpuCancellationTokenSource = new CancellationTokenSource();

            device.Gpu.ShaderCacheStateChanged += LoadProgressStateChangedHandler;
            device.Processes.ActiveApplication.DiskCacheLoadState.StateChanged += LoadProgressStateChangedHandler;

            // NEOV2-GPUMEM1: Android RAM watchdog while the game runs.
            HostMemoryWatchdog.Start();

            try
            {
                device.Gpu.Renderer.RunLoop(() =>
                {
                    _gpuDoneEvent.Reset();
                    device.Gpu.SetGpuThread();
                    device.Gpu.InitializeShaderCache(_gpuCancellationTokenSource.Token);

                    _isActive = true;

                    if (PlatformInfo.IsBionic)
                    {
                        setRenderingThread();
                    }

                    while (_isActive)
                    {
                        if (_isStopped)
                        {
                            break;
                        }

                        if (device.WaitFifo())
                        {
                            device.Statistics.RecordFifoStart();
                            device.ProcessFrame();
                            device.Statistics.RecordFifoEnd();
                        }

                        while (device.ConsumeFrameAvailable())
                        {
                            device.PresentFrame(() =>
                            {
                                if (device.Gpu.Renderer is ThreadedRenderer threaded && threaded.BaseRenderer is VulkanRenderer vulkanRenderer)
                                {
                                    setCurrentTransform(_window, (int)vulkanRenderer.CurrentTransform);
                                }
                                _swapBuffersCallback?.Invoke();
                            });
                        }
                    }

                    if (device.Gpu.Renderer is ThreadedRenderer tr)
                    {
                        tr.FlushThreadedCommands();
                    }

                    _gpuDoneEvent.Set();
                });
            }
            finally
            {
                HostMemoryWatchdog.Stop();
                device.Gpu.ShaderCacheStateChanged -= LoadProgressStateChangedHandler;
                device.Processes.ActiveApplication.DiskCacheLoadState.StateChanged -= LoadProgressStateChangedHandler;
            }
        }

        private static void LoadProgressStateChangedHandler<T>(T state, int current, int total) where T : Enum
        {
            void SetInfo(string status, float value)
            {
                if (PlatformInfo.IsBionic)
                {
                    Interop.UpdateProgress(status, value);
                }
            }
            string status = $"{current} / {total}";
            float progress = current / (float)total;
            if (float.IsNaN(progress))
                progress = 0;

            switch (state)
            {
                case LoadState ptcState:
                    if (float.IsNaN(progress))
                        progress = 0;

                    switch (ptcState)
                    {
                        case LoadState.Unloaded:
                        case LoadState.Loading:
                            SetInfo($"Loading PTC {status}", progress);
                            break;
                        case LoadState.Loaded:
                            SetInfo($"PTC Loaded", -1);
                            break;
                    }
                    break;
                case ShaderCacheState shaderCacheState:
                    switch (shaderCacheState)
                    {
                        case ShaderCacheState.Start:
                        case ShaderCacheState.Loading:
                            SetInfo($"Compiling Shaders {status}", progress);
                            break;
                        case ShaderCacheState.Packaging:
                            SetInfo($"Packaging Shaders {status}", progress);
                            break;
                        case ShaderCacheState.Loaded:
                            SetInfo($"Shaders Loaded", -1);
                            break;
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown Progress Handler type {typeof(T)}");
            }
        }

        public static void SetSwapBuffersCallback(SwapBuffersCallback swapBuffersCallback)
        {
            _swapBuffersCallback = swapBuffersCallback;
        }

        // ===== Convenience wrapper for Vulkan re-attachment (usable from JNI) =====
        public static bool TryReattachSurface()
        {
            if (Renderer is VulkanRenderer vr)
            {
                return vr.RecreateSurface();
            }
            return false;
        }

        public static void ReleaseRendererSurface()
        {
            (Renderer as VulkanRenderer)?.ReleaseSurface();
        }

        public static void SetPresentEnabled(bool enabled)
        {
            (Renderer as VulkanRenderer)?.SetPresentEnabled(enabled);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GraphicsConfiguration
    {
        public float ResScale;
        public float MaxAnisotropy;
        public bool FastGpuTime;
        public bool Fast2DCopy;
        public bool EnableMacroJit;
        public bool EnableMacroHLE;
        public bool EnableShaderCache;
        public bool EnableShaderLoopSafety;
        public bool EnableTextureRecompression;
        public bool EnableVtgViaCompute;

        // NEOV2 AUTOFLUSH1: 0 = Padrao, 1 = Reduzido, 2 = Desativado. DrawFlushGameOverride usa -1 para
        // "usar configuracao global" e serve apenas para o log inicial.
        public int DrawFlushMode;
        public int DrawFlushGlobalMode;
        public int DrawFlushGameOverride;

        // NEOV2 BUFFER-READBACK1: 0 = Padrao, 1 = Relaxada. BufferReadbackGameOverride usa -1 para
        // "usar configuracao global" e serve apenas para o log inicial.
        public int BufferReadbackMode;
        public int BufferReadbackGlobalMode;
        public int BufferReadbackGameOverride;

        // NEOV2 STRICT-SYNC1: 0 = Padrao (adiado no Turnip), 1 = Imediata. StrictSyncGameOverride usa -1
        // para "usar configuracao global" e serve apenas para o log inicial.
        public int StrictSyncMode;
        public int StrictSyncGlobalMode;
        public int StrictSyncGameOverride;

        public BackendThreading BackendThreading;
        public AspectRatio AspectRatio; // <- important

        public GraphicsConfiguration()
        {
            ResScale = 1f;
            MaxAnisotropy = -1;
            FastGpuTime = true;
            Fast2DCopy = true;
            EnableMacroJit = false;
            EnableMacroHLE = true;
            EnableShaderCache = true;
            EnableShaderLoopSafety = false;
            EnableTextureRecompression = false;
            EnableVtgViaCompute = true;
            DrawFlushMode = 0;
            DrawFlushGlobalMode = 0;
            DrawFlushGameOverride = -1;
            BufferReadbackMode = 0;
            BufferReadbackGlobalMode = 0;
            BufferReadbackGameOverride = -1;
            StrictSyncMode = 0;
            StrictSyncGlobalMode = 0;
            StrictSyncGameOverride = -1;
            BackendThreading = BackendThreading.Auto;
            AspectRatio = AspectRatio.Fixed16x9;
        }
    }

    public struct NativeGraphicsInterop
    {
        public nint GlGetProcAddress;
        public nint VkNativeContextLoader;
        public nint VkCreateSurface;
        public nint VkRequiredExtensions;
        public int VkRequiredExtensionsCount;
    }
}
