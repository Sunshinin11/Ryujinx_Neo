using LibKenjinx.Android;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
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
            GraphicsConfig.EnableTextureRecompression = graphicsConfiguration.EnableTextureRecompression;

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
        public bool EnableTextureRecompression;
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
            EnableTextureRecompression = false;
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
