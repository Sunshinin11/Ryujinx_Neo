using LibKenjinx.Android;
using LibKenjinx.Jni.Pointers;
using Ryujinx.Audio.Backends.OpenAL;
using Ryujinx.Audio.Integration;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.Input;
using Silk.NET.Core.Loader;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Ryujinx.Graphics.Vulkan;
using System.IO;
using System.Reflection;

namespace LibKenjinx
{
    public static partial class LibKenjinx
    {
        private static long _surfacePtr;
        private static long _window = 0;

        // Remembers the last set renderer size (for the jiggle)
        private static int _lastRenderWidth = 0;
        private static int _lastRenderHeight = 0;

        // NEW: Rotation Debounce + Pending Buffer
        private static int _lastRotationDegrees = -1;
        private static int _pendingRotationDegrees = -1;

        public static VulkanLoader? VulkanLoader { get; private set; }

        // ==== Audio Foreground/Background State (reflection-safe) ====
        private static bool _audioPaused = false;
        private static bool _audioMuted  = false;

        // strong reference if OpenAL backend is used
        private static OpenALHardwareDeviceDriver? _openAl;

        // ---------- helpers for broad reflection coverage ----------
        private static readonly string[] PauseMethodCandidates =
        {
            "Pause", "SetPaused", "SetPause", "PauseAll", "RequestPause",
            "SetIsPaused", "SetPauseState", "Suspend", "SetSuspended",
            "PauseEmulation", "SetEmulationPaused", "SetRunning" // some implementations invert bool
        };

        private static readonly string[] VolumeMethodCandidates =
        {
            "SetVolumeMultiplier", "SetVolume", "SetMasterVolume", "SetGain"
        };

        private static readonly string[] VolumePropertyCandidates =
        {
            "VolumeMultiplier", "MasterVolume", "Volume", "Gain", "OutputVolume"
        };

        // Try call method with single bool
        private static bool TryCallBool(object? target, string[] names, bool arg)
        {
            if (target == null) return false;
            Type t = target.GetType();
            foreach (string name in names)
            {
                MethodInfo? m = t.GetMethod(name, new[] { typeof(bool) });
                if (m != null)
                {
                    try { m.Invoke(target, new object[] { arg }); return true; } catch { }
                }
            }
            return false;
        }

        // Try call method with single float
        private static bool TryCallFloat(object? target, string[] names, float arg)
        {
            if (target == null) return false;
            Type t = target.GetType();
            foreach (string name in names)
            {
                MethodInfo? m = t.GetMethod(name, new[] { typeof(float) });
                if (m != null)
                {
                    try { m.Invoke(target, new object[] { arg }); return true; } catch { }
                }
            }
            return false;
        }

        // Try set property (float/double)
        private static bool TrySetFloatProp(object? target, string[] names, float value)
        {
            if (target == null) return false;
            Type t = target.GetType();
            foreach (string name in names)
            {
                PropertyInfo? p = t.GetProperty(name);
                if (p != null && p.CanWrite)
                {
                    try
                    {
                        object v = value;
                        if (p.PropertyType == typeof(double)) v = (double)value;
                        p.SetValue(target, v);
                        return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        // Try set property (bool) for Mute/IsMuted etc.
        private static bool TrySetBoolProp(object target, params string[] names)
        {
            if (target == null) return false;
            Type t = target.GetType();
            foreach (string name in names)
            {
                PropertyInfo? p = t.GetProperty(name);
                if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
                {
                    try { p.SetValue(target, true); return true; } catch { }
                }
            }
            return false;
        }

        // breadth-first walk through "audio-ish" objects accessible from a root
        private static IEnumerable<object?> WalkAudioObjects(object? root, int depth = 2)
        {
            if (root == null || depth < 0) yield break;

            yield return root;

            Type t = root.GetType();
            PropertyInfo[] props = t.GetProperties();
            foreach (PropertyInfo p in props)
            {
                object? val = null;
                try { val = p.GetValue(root); } catch { /* ignore */ }

                if (val == null) continue;

                // Prefer properties that look audio-related or are common containers
                if (p.Name.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.Name.IndexOf("Sound", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.Name.IndexOf("Mixer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.Name.IndexOf("Output", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.PropertyType.Name.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    foreach (object? x in WalkAudioObjects(val, depth - 1))
                        yield return x;
                }
            }
        }

        /// <summary>
        /// Attempts to pause the entire emulation (strongest guarantee to stop audio).
        /// We probe several likely targets via reflection to be compatible across forks.
        /// </summary>
        private static bool TryPauseEmulation(bool pause)
        {
            int hits = 0;

            try
            {
                // 1) Try SwitchDevice wrapper itself
                SwitchDevice? dev = SwitchDevice;
                if (dev != null && TryCallBool(dev, PauseMethodCandidates, pause)) hits++;

                // 2) Try underlying Switch or similar inner object
                object? inner =
                    dev?.GetType().GetProperty("Switch")?.GetValue(dev) ??
                    dev?.GetType().GetProperty("Device")?.GetValue(dev);
                if (inner != null && TryCallBool(inner, PauseMethodCandidates, pause)) hits++;

                // 3) Try EmulationContext and its "System" (kernel/front controller etc.)
                Switch? ctx = dev?.EmulationContext;
                if (ctx != null)
                {
                    if (TryCallBool(ctx, PauseMethodCandidates, pause)) hits++;

                    object? sys = ctx.GetType().GetProperty("System")?.GetValue(ctx);
                    if (sys != null && TryCallBool(sys, PauseMethodCandidates, pause)) hits++;

                    // 4) walk all audio-ish descendants and try pause
                    foreach (object? node in WalkAudioObjects(ctx, depth: 2))
                    {
                        if (node != null && TryCallBool(node, PauseMethodCandidates, pause))
                            hits++;
                    }
                }
            }
            catch { /* ignore */ }

            if (hits > 0)
                Logger.Info?.Print(LogClass.Application, $"[PauseGate] Emulation pause={pause} hits={hits}");

            return hits > 0;
        }

        /// <summary>
        /// Applies paused/muted state. Tries many backends/locations to be
        /// resilient across Ryujinx revisions.
        /// </summary>
        private static void ApplyAudioState()
        {
            bool shouldMute = _audioPaused || _audioMuted;
            float vol = shouldMute ? 0f : 1f;
            int hits = 0;

            // A) Direct: OpenAL driver (if used)
            try
            {
                OpenALHardwareDeviceDriver? oal = _openAl;
                if (oal != null)
                {
                    bool p = TryCallBool(oal, PauseMethodCandidates, _audioPaused);
                    bool v = TryCallFloat(oal, VolumeMethodCandidates, vol) || TrySetFloatProp(oal, VolumePropertyCandidates, vol);
                    if (p || v) { hits++; Logger.Trace?.Print(LogClass.Application, $"[AudioGate] OpenAL applied p={p} v={v} vol={vol}"); }
                }
            }
            catch { }

            // B) EmulationContext managers (AudioRendererManager/AudioManager/AudioOutManager/…)
            try
            {
                Switch? ctx = SwitchDevice?.EmulationContext;
                if (ctx != null)
                {
                    foreach (object? node in WalkAudioObjects(ctx, depth: 2))
                    {
                        if (node == null) continue;

                        bool p = TryCallBool(node, PauseMethodCandidates, _audioPaused);
                        bool v = TryCallFloat(node, VolumeMethodCandidates, vol) || TrySetFloatProp(node, VolumePropertyCandidates, vol);

                        // also try boolean mute-style properties (IsMuted/Mute)
                        if (shouldMute)
                            v = v || TrySetBoolProp(node, "IsMuted", "Muted", "Mute");

                        if (p || v) hits++;
                    }
                }
            }
            catch { }

            // C) Generic driver fallback (whatever AudioDriver actually is)
            try
            {
                IHardwareDeviceDriver? drv = AudioDriver;
                if (drv != null)
                {
                    bool p = TryCallBool(drv, PauseMethodCandidates, _audioPaused);
                    bool v = TryCallFloat(drv, VolumeMethodCandidates, vol) || TrySetFloatProp(drv, VolumePropertyCandidates, vol);
                    if (shouldMute) v = v || TrySetBoolProp(drv, "IsMuted", "Muted", "Mute");

                    if (p || v) hits++;
                }
            }
            catch { }

            Logger.Info?.Print(LogClass.Application, $"[AudioGate] applied (hits={hits}) paused={_audioPaused} muted={_audioMuted} → vol={vol}");

            // D) fallback: try to pause whole emulation if audio controls not hit
            if (hits == 0 && _audioPaused)
            {
                if (TryPauseEmulation(true))
                    Logger.Info?.Print(LogClass.Application, "[AudioGate] escalated: Emulation paused");
            }
            else if (hits == 0 && !_audioPaused)
            {
                // try resume if we previously paused emulation
                if (TryPauseEmulation(false))
                    Logger.Info?.Print(LogClass.Application, "[AudioGate] escalated: Emulation resumed");
            }
        }
        // =============================================================

        [DllImport("libkenjinxjni")]
        internal extern static void setRenderingThread();

        [DllImport("libkenjinxjni")]
        internal extern static void debug_break(int code);

        [DllImport("libkenjinxjni")]
        internal extern static void setCurrentTransform(long native_window, int transform);

        public delegate nint JniCreateSurface(nint native_surface, nint instance);

        [UnmanagedCallersOnly(EntryPoint = "javaInitialize")]
        public unsafe static bool JniInitialize(nint jpathId, nint jniEnv)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            PlatformInfo.IsBionic = true;

            Logger.AddTarget(
                new AsyncLogTargetWrapper(
                    new AndroidLogTarget("RyujinxLog"),
                    1000,
                    AsyncLogTargetOverflowAction.Block
                ));

            string? path = Marshal.PtrToStringAnsi(jpathId);

            bool init = Initialize(path);

            Interop.Initialize(new JEnvRef(jniEnv));

            Interop.Test();

            return init;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceReloadFilesystem")]
        public static void JnaReloadFileSystem()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call: deviceReloadFilesystem");
            SwitchDevice?.ReloadFileSystem();
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceInitialize")]
        public static bool JnaDeviceInitialize(int memoryManagerMode,
                                                    bool useNce,
                                                    int memoryConfiguration,
                                                    int memorySlackGiB,
                                                    int systemLanguage,
                                                    int regionCode,
                                                    int vSyncMode,
                                                    bool enableDockedMode,
                                                    bool enablePtc,
                                                    bool enableLowPowerPtc,
                                                    bool enableJitCacheEviction,
                                                    bool enableInternetAccess,
                                                    bool enableFsIntegrityChecks,
                                                    int fsGlobalAccessLogMode,
                                                    nint timeZonePtr,
                                                    bool ignoreMissingServices)
        {
            debug_break(4);
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            AudioDriver = new OpenALHardwareDeviceDriver();
            _openAl = AudioDriver as OpenALHardwareDeviceDriver; // <-- audio patch: keep a strong ref

            string? timezone = Marshal.PtrToStringAnsi(timeZonePtr);
            ulong memorySlack = (ulong)memorySlackGiB * 1024 * 1024 * 1024;
            return InitializeDevice((MemoryManagerMode)memoryManagerMode,
                                    useNce,
                                    (MemoryConfiguration)memoryConfiguration,
                                    memorySlack,
                                    (SystemLanguage)systemLanguage,
                                    (RegionCode)regionCode,
                                    (VSyncMode)vSyncMode,
                                    enableDockedMode,
                                    enablePtc,
                                    enableLowPowerPtc,
                                    enableJitCacheEviction,
                                    enableInternetAccess,
                                    enableFsIntegrityChecks,
                                    fsGlobalAccessLogMode,
                                    timezone,
                                    ignoreMissingServices);
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetGameFifo")]
        public static double JnaGetGameFifo()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            double stats = SwitchDevice?.EmulationContext?.Statistics.GetFifoPercent() ?? 0;

            return stats;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetGameFrameTime")]
        public static double JnaGetGameFrameTime()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            double stats = SwitchDevice?.EmulationContext?.Statistics.GetGameFrameTime() ?? 0;

            return stats;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetGameFrameRate")]
        public static double JnaGetGameFrameRate()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            double stats = SwitchDevice?.EmulationContext?.Statistics.GetGameFrameRate() ?? 0;

            return stats;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceLaunchMiiEditor")]
        public static bool JNALaunchMiiEditApplet()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            if (SwitchDevice?.EmulationContext == null)
            {
                return false;
            }

            return LaunchMiiEditApplet();
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetDlcContentList")]
        public static nint JniGetDlcContentListNative(nint pathPtr, long titleId)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            List<string> list = GetDlcContentList(Marshal.PtrToStringAnsi(pathPtr) ?? "", (ulong)titleId);

            return CreateStringArray(list);
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetDlcTitleId")]
        public static long JniGetDlcTitleIdNative(nint pathPtr, nint ncaPath)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            return Marshal.StringToHGlobalAnsi(GetDlcTitleId(Marshal.PtrToStringAnsi(pathPtr) ?? "", Marshal.PtrToStringAnsi(ncaPath) ?? ""));
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceSignalEmulationClose")]
        public static void JniSignalEmulationCloseNative()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call: deviceSignalEmulationClose");
            SignalEmulationClose();
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceCloseEmulation")]
        public static void JniCloseEmulationNative()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call: deviceCloseEmulation");
            CloseEmulation();
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceReinitEmulation")]
        public static void JniReinitEmulationNative()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call: deviceReinitEmulation");
            ReinitEmulation();
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceLoadDescriptor")]
        public static bool JnaLoadApplicationNative(int descriptor, int type, int updateDescriptor)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            if (SwitchDevice?.EmulationContext == null)
            {
                return false;
            }

            FileStream stream = OpenFile(descriptor);
            FileStream? update = updateDescriptor == -1 ? null : OpenFile(updateDescriptor);

            return LoadApplication(stream, (FileType)type, update);
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceVerifyFirmware")]
        public static nint JniVerifyFirmware(int descriptor, bool isXci)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");

            FileStream stream = OpenFile(descriptor);

            nint stringHandle = 0;
            string? version = "0.0";

            try
            {
                version = VerifyFirmware(stream, isXci)?.VersionString;
            }
            catch (Exception _)
            {
                Logger.Error?.Print(LogClass.Service, $"Unable to verify firmware. Exception: {_}");
            }

            if (version != null)
            {
                stringHandle = Marshal.StringToHGlobalAnsi(version);
            }

            return stringHandle;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceInstallFirmware")]
        public static void JniInstallFirmware(int descriptor, bool isXci)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");

            FileStream stream = OpenFile(descriptor);

            InstallFirmware(stream, isXci);
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetInstalledFirmwareVersion")]
        public static nint JniGetInstalledFirmwareVersion()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");

            string version = GetInstalledFirmwareVersion() ?? "0.0";
            return Marshal.StringToHGlobalAnsi(version);
        }

        [UnmanagedCallersOnly(EntryPoint = "graphicsInitialize")]
        public static bool JnaGraphicsInitialize(float resScale,
                float maxAnisotropy,
                bool fastGpuTime,
                bool fast2DCopy,
                bool enableMacroJit,
                bool enableMacroHLE,
                bool enableShaderCache,
                bool enableShaderLoopSafety,
                bool enableTextureRecompression,
                bool enableVtgViaCompute,
                int drawFlushMode,
                int drawFlushGlobalMode,
                int drawFlushGameOverride,
                int bufferReadbackMode,
                int bufferReadbackGlobalMode,
                int bufferReadbackGameOverride,
                int strictSyncMode,
                int strictSyncGlobalMode,
                int strictSyncGameOverride,
                int backendThreading)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SearchPathContainer.Platform = UnderlyingPlatform.Android;
            return InitializeGraphics(new GraphicsConfiguration()
            {
                ResScale = resScale,
                MaxAnisotropy = maxAnisotropy,
                FastGpuTime = fastGpuTime,
                Fast2DCopy = fast2DCopy,
                EnableMacroJit = enableMacroJit,
                EnableMacroHLE = enableMacroHLE,
                EnableShaderCache = enableShaderCache,
                EnableShaderLoopSafety = enableShaderLoopSafety,
                EnableTextureRecompression = enableTextureRecompression,
                EnableVtgViaCompute = enableVtgViaCompute,
                DrawFlushMode = drawFlushMode,
                DrawFlushGlobalMode = drawFlushGlobalMode,
                DrawFlushGameOverride = drawFlushGameOverride,
                BufferReadbackMode = bufferReadbackMode,
                BufferReadbackGlobalMode = bufferReadbackGlobalMode,
                BufferReadbackGameOverride = bufferReadbackGameOverride,
                StrictSyncMode = strictSyncMode,
                StrictSyncGlobalMode = strictSyncGlobalMode,
                StrictSyncGameOverride = strictSyncGameOverride,
                BackendThreading = (BackendThreading)backendThreading
            });
        }

        [UnmanagedCallersOnly(EntryPoint = "graphicsInitializeRenderer")]
        public unsafe static bool JnaGraphicsInitializeRenderer(char** extensionsArray,
                                                                          int extensionsLength,
                                                                          long driverHandle)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            if (Renderer != null)
            {
                return false;
            }

            List<string?> extensions = [];

            for (int i = 0; i < extensionsLength; i++)
            {
                extensions.Add(Marshal.PtrToStringAnsi((nint)extensionsArray[i]));
            }

            if (driverHandle != 0)
            {
                VulkanLoader = new VulkanLoader((nint)driverHandle);
            }

            CreateSurface createSurfaceFunc = instance =>
            {
                _surfacePtr = Interop.GetSurfacePtr();
                _window = Interop.GetWindowsHandle();

                Vk api = VulkanLoader?.GetApi() ?? Vk.GetApi();
                if (api.TryGetInstanceExtension(new Instance(instance), out KhrAndroidSurface surfaceExtension))
                {
                    AndroidSurfaceCreateInfoKHR createInfo = new()
                    {
                        SType = StructureType.AndroidSurfaceCreateInfoKhr,
                        Window = (nint*)_surfacePtr,
                    };

                    Result result = surfaceExtension.CreateAndroidSurface(new Instance(instance), in createInfo, null, out SurfaceKHR surface);

                    // If a rotation was applied before the surface was created → apply it now
                    if (_window != 0 && _pendingRotationDegrees != -1)
                    {
                        try
                        {
                            int t = _pendingRotationDegrees switch
                            {
                                0   => 0, // IDENTITY
                                90  => 4, // ROTATE_90
                                180 => 3, // ROTATE_180 (H|V mirror)
                                270 => 7, // ROTATE_270 (ROT_90 | H|V)
                                _   => 0,
                            };
                            setCurrentTransform(_window, t);
                            Logger.Trace?.Print(LogClass.Application, $"[JNI] Apply pending SurfaceTransform {_pendingRotationDegrees}° (t={t}, window=0x{_window:x})");
                            _lastRotationDegrees = _pendingRotationDegrees;
                            _pendingRotationDegrees = -1;
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning?.Print(LogClass.Application, $"Apply pending transform failed: {ex}");
                        }
                    }

                    return (nint)surface.Handle;
                }

                return nint.Zero;
            };

            return InitializeGraphicsRenderer(GraphicsBackend.Vulkan, createSurfaceFunc, extensions.ToArray());
        }

        [UnmanagedCallersOnly(EntryPoint = "graphicsRendererSetSize")]
        public static void JnaSetRendererSizeNative(int width, int height)
        {
            Logger.Trace?.Print(LogClass.Application, $"graphicsRendererSetSize -> {width}x{height}");
            _lastRenderWidth  = width;
            _lastRenderHeight = height;
            Renderer?.Window?.SetSize(width, height);
        }

        [UnmanagedCallersOnly(EntryPoint = "graphicsRendererRunLoop")]
        public static void JniRunLoopNative()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");

            // NEOV2 Etapa 7: o frameEnded do Kotlin (QuickSettings + turbo + broadcast de 60 Hz + progresso)
            // rodava a cada present via JNI. Agora roda no primeiro quadro e depois no maximo a cada 2 s.
            long lastFrameEndedTimestamp = 0;
            long frameEndedInterval = System.Diagnostics.Stopwatch.Frequency * 2;

            SetSwapBuffersCallback(() =>
            {
                if (SwitchDevice?.EmulationContext != null)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();

                    if (lastFrameEndedTimestamp == 0 || now - lastFrameEndedTimestamp >= frameEndedInterval)
                    {
                        lastFrameEndedTimestamp = now;
                        double time = SwitchDevice.EmulationContext.Statistics.GetGameFrameTime();
                        Interop.FrameEnded(time);
                    }
                }
            });
            RunLoop();
        }

        [UnmanagedCallersOnly(EntryPoint = "loggingSetEnabled")]
        public static void JniSetLoggingEnabledNative(int logLevel, bool enabled)
        {
            Logger.SetEnable((LogLevel)logLevel, enabled);
        }

        [UnmanagedCallersOnly(EntryPoint = "loggingEnabledGraphicsLog")]
        public static void JniSetLoggingEnabledGraphicsLog(bool enabled)
        {
            _enableGraphicsLogging = enabled;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceGetGameInfo")]
        public unsafe static void JniGetGameInfo(int fileDescriptor, nint extension, nint infoPtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            using FileStream stream = OpenFile(fileDescriptor);
            string? ext = Marshal.PtrToStringAnsi(extension);
            GameInfo info = GetGameInfo(stream, ext?.ToLower() ?? string.Empty) ?? GetDefaultInfo(stream);
            GameInfoNative* i = (GameInfoNative*)infoPtr;
            GameInfoNative n = new(info);
            i->TitleId = n.TitleId;
            i->TitleName = n.TitleName;
            i->Version = n.Version;
            i->FileSize = n.FileSize;
            i->Icon = n.Icon;
            i->Version = n.Version;
            i->Developer = n.Developer;
        }

        [UnmanagedCallersOnly(EntryPoint = "graphicsRendererSetVsync")]
        public static void JnaSetVsyncStateNative(int vSyncMode)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetVsyncState((VSyncMode)vSyncMode);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputInitialize")]
        public static void JnaInitializeInput(int width, int height)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            InitializeInput(width, height);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetClientSize")]
        public static void JnaSetClientSize(int width, int height)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetClientSize(width, height);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetTouchPoint")]
        public static void JnaSetTouchPoint(int x, int y)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetTouchPoint(x, y);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputReleaseTouchPoint")]
        public static void JnaReleaseTouchPoint()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            ReleaseTouchPoint();
        }

        [UnmanagedCallersOnly(EntryPoint = "inputUpdate")]
        public static void JniUpdateInput()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            UpdateInput();
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetButtonPressed")]
        public static void JnaSetButtonPressed(int button, int id)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetButtonPressed((GamepadButtonInputId)button, id);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetButtonReleased")]
        public static void JnaSetButtonReleased(int button, int id)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetButtonReleased((GamepadButtonInputId)button, id);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetAccelerometerData")]
        public static void JniSetAccelerometerData(float x, float y, float z, int id)
        {
            Vector3 accel = new(x, y, z);
            SetAccelerometerData(accel, id);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetGyroData")]
        public static void JniSetGyroData(float x, float y, float z, int id)
        {
            Vector3 gryo = new(x, y, z);
            SetGryoData(gryo, id);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputSetStickAxis")]
        public static void JnaSetStickAxis(int stick, float x, float y, int id)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            SetStickAxis((StickInputId)stick, new Vector2(float.IsNaN(x) ? 0 : x, float.IsNaN(y) ? 0 : y), id);
        }

        [UnmanagedCallersOnly(EntryPoint = "inputConnectGamepad")]
        public static int JnaConnectGamepad(int index)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            return ConnectGamepad(index);
        }

        [UnmanagedCallersOnly(EntryPoint = "userGetOpenedUser")]
        public static nint JniGetOpenedUser()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            string userId = GetOpenedUser();
            IntPtr ptr = Marshal.StringToHGlobalAnsi(userId);

            return ptr;
        }

        [UnmanagedCallersOnly(EntryPoint = "userGetUserPicture")]
        public static nint JniGetUserPicture(nint userIdPtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            string userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";

            return Marshal.StringToHGlobalAnsi(GetUserPicture(userId));
        }

        [UnmanagedCallersOnly(EntryPoint = "userSetUserPicture")]
        public static void JniGetUserPicture(nint userIdPtr, nint picturePtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            string userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";
            string picture = Marshal.PtrToStringAnsi(picturePtr) ?? "";

            SetUserPicture(userId, picture);
        }

        [UnmanagedCallersOnly(EntryPoint = "userGetUserName")]
        public static nint JniGetUserName(nint userIdPtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            string userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";

            return Marshal.StringToHGlobalAnsi(GetUserName(userId));
        }

        [UnmanagedCallersOnly(EntryPoint = "userSetUserName")]
        public static void JniSetUserName(nint userIdPtr, nint userNamePtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            string userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";
            string userName = Marshal.PtrToStringAnsi(userNamePtr) ?? "";

            SetUserName(userId, userName);
        }

        [UnmanagedCallersOnly(EntryPoint = "userGetAllUsers")]
        public static nint JniGetAllUsers()
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            string[] users = GetAllUsers();

            return CreateStringArray(users.ToList());
        }

        [UnmanagedCallersOnly(EntryPoint = "userAddUser")]
        public static void JniAddUser(nint userNamePtr, nint picturePtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            string userName = Marshal.PtrToStringAnsi(userNamePtr) ?? "";
            string picture = Marshal.PtrToStringAnsi(picturePtr) ?? "";

            AddUser(userName, picture);
        }

        [UnmanagedCallersOnly(EntryPoint = "userDeleteUser")]
        public static void JniDeleteUser(nint userIdPtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            string userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";

            DeleteUser(userId);
        }

        // --- Audio JNI Exports (Foreground/Background gating) ---
        [UnmanagedCallersOnly(EntryPoint = "audioSetPaused")]
        public static void JniAudioSetPaused(bool paused)
        {
            _audioPaused = paused;
            ApplyAudioState();
        }

        [UnmanagedCallersOnly(EntryPoint = "audioSetMuted")]
        public static void JniAudioSetMuted(bool muted)
        {
            _audioMuted = muted;
            ApplyAudioState();
        }
        // ---------------------------------------------------------

        [UnmanagedCallersOnly(EntryPoint = "uiHandlerSetup")]
        public static void JniSetupUiHandler()
        {
            SetupUiHandler();
        }

        [UnmanagedCallersOnly(EntryPoint = "uiHandlerSetResponse")]
        public static void JniSetUiHandlerResponse(bool isOkPressed, nint input)
        {
            SetUiHandlerResponse(isOkPressed, Marshal.PtrToStringAnsi(input) ?? "");
        }

        [UnmanagedCallersOnly(EntryPoint = "userOpenUser")]
        public static void JniOpenUser(nint userIdPtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            string userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";

            OpenUser(userId);
        }

        [UnmanagedCallersOnly(EntryPoint = "userCloseUser")]
        public static void JniCloseUser(nint userIdPtr)
        {
            Logger.Trace?.Print(LogClass.Application, "Jni Function Call");
            string userId = Marshal.PtrToStringAnsi(userIdPtr) ?? "";

            CloseUser(userId);
        }

        // --- Window Handle Update (Android) ---
        [UnmanagedCallersOnly(EntryPoint = "deviceSetWindowHandle")]
        public static void JniSetWindowHandle(long handle)
        {
            _window = handle;
            Logger.Trace?.Print(Ryujinx.Common.Logging.LogClass.Application,
                $"Window handle updated: 0x{handle:X}");
        }

        // --- Surface Rotation Bridge (Android) ---
        [UnmanagedCallersOnly(EntryPoint = "deviceSetSurfaceRotation")]
        public static void JniDeviceSetSurfaceRotation(int degrees)
        {
            try
            {
                // Normalize
                degrees = degrees switch { 0 => 0, 90 => 90, 180 => 180, 270 => 270, _ => 0 };

                if (degrees == _lastRotationDegrees)
                {
                    Logger.Trace?.Print(LogClass.Application, $"[JNI] SurfaceTransform unchanged ({degrees}°), skip");
                    return;
                }

                // CORRECT bitmask mapping according to NDK:
                // 0 -> 0 (IDENTITY)
                // 90 -> 4 (ROTATE_90)
                // 180 -> 3 (H|V mirror == 180°)
                // 270 -> 7 (ROTATE_270 == ROT_90 | H|V)
                int transform = degrees switch
                {
                    0   => 0,
                    90  => 4,
                    180 => 3,
                    270 => 7,
                    _   => 0
                };

                if (_window != 0)
                {
                    setCurrentTransform(_window, transform);
                    _lastRotationDegrees = degrees;
                    Logger.Trace?.Print(LogClass.Application, $"[JNI] SurfaceTransform -> {degrees}° (t={transform}, window=0x{_window:x})");
                }
                else
                {
                    _pendingRotationDegrees = degrees; // apply later (see createSurfaceFunc)
                    Logger.Warning?.Print(LogClass.Application, $"[JNI] deviceSetSurfaceRotation: _window == 0 (pending {degrees}°)");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"deviceSetSurfaceRotation failed: {ex}");
            }
        }

        // --- Vulkan/GL: Swapchain/Surface Reconfiguration via Size Jiggle ---
        [UnmanagedCallersOnly(EntryPoint = "deviceRecreateSwapchain")]
        public static void JniDeviceRecreateSwapchain()
        {
            try
            {
                Logger.Trace?.Print(LogClass.Application, "[JNI] deviceRecreateSwapchain");
                if (Renderer?.Window == null)
                {
                    Logger.Warning?.Print(LogClass.Application, "[JNI] deviceRecreateSwapchain: Renderer.Window == null");
                    return;
                }

                int w = _lastRenderWidth;
                int h = _lastRenderHeight;

                if (w > 0 && h > 0)
                {
                    int jiggleW = w;
                    int jiggleH = h;
                    if (w <= h) jiggleW = Math.Max(1, w - 1); else jiggleH = Math.Max(1, h - 1);

                    Logger.Trace?.Print(LogClass.Application, $"[JNI] deviceRecreateSwapchain: jiggle {jiggleW}x{jiggleH} -> {w}x{h}");
                    Renderer.Window.SetSize(jiggleW, jiggleH);
                    Renderer.Window.SetSize(w, h);
                }
                else
                {
                    Logger.Trace?.Print(LogClass.Application, "[JNI] deviceRecreateSwapchain: unknown last size -> 1x1 -> 2x2 jiggle");
                    Renderer.Window.SetSize(1, 1);
                    Renderer.Window.SetSize(2, 2);
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"deviceRecreateSwapchain failed: {ex}");
            }
        }

        // ===== PresentAllowed / Surface Control (JNI) =====

        // Alias for older calls, if available
        [UnmanagedCallersOnly(EntryPoint = "graphicsRendererSetPresent")]
        public static void JniGraphicsRendererSetPresent(bool enabled)
        {
            try
            {
                if (Renderer is VulkanRenderer vr)
                {
                    vr.SetPresentEnabled(enabled);
                    Logger.Trace?.Print(LogClass.Application, $"[JNI] PresentEnabled = {enabled}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"graphicsRendererSetPresent failed: {ex}");
            }
        }

        // New name: Matches KenjinxNative.graphicsSetPresentEnabled(...)
        [UnmanagedCallersOnly(EntryPoint = "graphicsSetPresentEnabled")]
        public static void JniGraphicsSetPresentEnabled(bool enabled)
        {
            try
            {
                (Renderer as VulkanRenderer)?.SetPresentEnabled(enabled);
                Logger.Trace?.Print(LogClass.Application, $"[JNI] graphicsSetPresentEnabled({enabled})");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"graphicsSetPresentEnabled failed: {ex}");
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "graphicsRendererRecreateSurface")]
        public static void JniGraphicsRendererRecreateSurface()
        {
            try
            {
                Logger.Trace?.Print(LogClass.Application, "[JNI] graphicsRendererRecreateSurface");
                _ = (Renderer as VulkanRenderer)?.RecreateSurface();
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"graphicsRendererRecreateSurface failed: {ex}");
            }
        }

#if ANDROID
        [UnmanagedCallersOnly(EntryPoint = "graphicsSetFullscreenStretch")]
        public static void JniGraphicsSetFullscreenStretch(bool enable)
        {
            Logger.Trace?.Print(LogClass.Application, $"[JNI] graphicsSetFullscreenStretch({enable})");
            ApplyFullscreenStretch(enable);
        }

        // NEOV2 NCE-PROT1: per game switch, set before the game is loaded.
        [UnmanagedCallersOnly(EntryPoint = "deviceSetNceTrackingProtection")]
        public static void JniDeviceSetNceTrackingProtection(bool enable)
        {
            Ryujinx.Cpu.Nce.MemoryManagerNative.TrackingProtectionRestoreEnabled = enable;
            Logger.Info?.Print(LogClass.Cpu, $"[NEOV2-NCE-PROT1] Tracking protection restore: {(enable ? "enabled" : "disabled")}");
        }

        // NEOV2 DMA-RELAX1: chave geral e por jogo "Copia DMA relaxada", definida antes de carregar o jogo.
        [UnmanagedCallersOnly(EntryPoint = "deviceSetRelaxedDmaCopy")]
        public static void JniDeviceSetRelaxedDmaCopy(bool enable)
        {
            Ryujinx.Graphics.Gpu.GraphicsConfig.RelaxedDmaCopy = enable;
            Logger.Info?.Print(LogClass.Gpu, $"[NEOV2-DMA-RELAX1] Relaxed DMA copy: {(enable ? "enabled" : "disabled")}");
        }

        // NEOV2-GPUMEM1: chave geral e por jogo "Memoria de GPU estilo Eden", definida antes de carregar o jogo.
        [UnmanagedCallersOnly(EntryPoint = "deviceSetEdenGpuMemory")]
        public static void JniDeviceSetEdenGpuMemory(bool enable)
        {
            Ryujinx.Graphics.Gpu.GraphicsConfig.EdenGpuMemory = enable;
            Ryujinx.Graphics.GAL.GpuMemoryStats.HostRamGuardEnabled = enable; // NEOV2-RAMGUARD1
            Logger.Info?.Print(LogClass.Gpu, $"Memoria de GPU estilo Eden: {(enable ? "ligada" : "desligada")}");
        }

        // NEOV2-VP9EDEN1: chave geral "VP9 pelo FFmpeg (Eden)", definida antes de carregar o jogo.
        [UnmanagedCallersOnly(EntryPoint = "deviceSetVp9FFmpeg")]
        public static void JniDeviceSetVp9FFmpeg(bool enable)
        {
            Ryujinx.Common.Diagnostics.NeoGraphicsSettings.Vp9FFmpegEnabled = enable;
            Logger.Info?.Print(LogClass.Nvdec, $"[NEOV2-VP9EDEN1] VP9 pelo FFmpeg: {(enable ? "ligado" : "desligado")}");
        }

        // NEOV2-NCZ2: conversor NSP -> NSZ. Bloqueante: chamar fora da thread de UI.
        [UnmanagedCallersOnly(EntryPoint = "deviceCompressNsp")]
        public static int JniDeviceCompressNsp(int inDescriptor, int outDescriptor, int level)
        {
            if (SwitchDevice?.VirtualFileSystem == null)
            {
                Logger.Error?.Print(LogClass.Loader, "[NEOV2-NCZ2] SwitchDevice is not initialized");
                return Ryujinx.HLE.FileSystem.NczCompressor.ResultError;
            }

            using FileStream input = OpenFile(inDescriptor);
            using FileStream output = OpenFile(outDescriptor);

            return Ryujinx.HLE.FileSystem.NczCompressor.CompressNsp(input, output, SwitchDevice.VirtualFileSystem, level);
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceCompressProgress")]
        public static int JniDeviceCompressProgress() => Ryujinx.HLE.FileSystem.NczCompressor.Permille;

        [UnmanagedCallersOnly(EntryPoint = "deviceCompressPhase")]
        public static int JniDeviceCompressPhase() => Ryujinx.HLE.FileSystem.NczCompressor.Phase;

        [UnmanagedCallersOnly(EntryPoint = "deviceCompressCancel")]
        public static void JniDeviceCompressCancel() => Ryujinx.HLE.FileSystem.NczCompressor.Cancel();

        [UnmanagedCallersOnly(EntryPoint = "deviceDecompressSize")]
        public static long JniDeviceDecompressSize(int descriptor, int standalone)
        {
            try
            {
                using FileStream input = new(new Microsoft.Win32.SafeHandles.SafeFileHandle(descriptor, false), FileAccess.Read);
                return Ryujinx.HLE.FileSystem.NczDecompressor.GetExpectedSize(input, standalone != 0);
            }
            catch (Exception exception)
            {
                Logger.Error?.Print(LogClass.Loader, $"[NEO-QOL1-DECOMPRESS] Descriptor inspection failed: {exception.Message}");
                return -1;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceDecompress")]
        public static int JniDeviceDecompress(int inputDescriptor, int outputDescriptor, int standalone, nint sourceName)
        {
            if (inputDescriptor == outputDescriptor) return Ryujinx.HLE.FileSystem.NczCompressor.ResultError;
            try
            {
                using FileStream input = new(new Microsoft.Win32.SafeHandles.SafeFileHandle(inputDescriptor, false), FileAccess.Read);
                using FileStream output = new(new Microsoft.Win32.SafeHandles.SafeFileHandle(outputDescriptor, false), FileAccess.ReadWrite);
                return Ryujinx.HLE.FileSystem.NczDecompressor.Decompress(input, output, standalone != 0,
                    Marshal.PtrToStringUTF8(sourceName) ?? "standalone.ncz");
            }
            catch (Exception exception)
            {
                Logger.Error?.Print(LogClass.Loader, $"[NEO-QOL1-DECOMPRESS] Descriptor conversion failed: {exception.Message}");
                return Ryujinx.HLE.FileSystem.NczCompressor.ResultError;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceDecompressProgress")]
        public static int JniDeviceDecompressProgress() => Ryujinx.HLE.FileSystem.NczDecompressor.Permille;

        [UnmanagedCallersOnly(EntryPoint = "deviceDecompressPhase")]
        public static int JniDeviceDecompressPhase() => Ryujinx.HLE.FileSystem.NczDecompressor.Phase;

        [UnmanagedCallersOnly(EntryPoint = "deviceDecompressCancel")]
        public static void JniDeviceDecompressCancel() => Ryujinx.HLE.FileSystem.NczDecompressor.Cancel();

        [UnmanagedCallersOnly(EntryPoint = "deviceOptimizeNszSize")]
        public static long JniDeviceOptimizeNszSize(int descriptor)
        {
            try
            {
                using FileStream input = new(new Microsoft.Win32.SafeHandles.SafeFileHandle(descriptor, false), FileAccess.Read);
                return Ryujinx.HLE.FileSystem.NszBlockOptimizer.GetRequiredSize(input);
            }
            catch (Exception exception)
            {
                Logger.Error?.Print(LogClass.Loader, $"[NEO-QOL4-NSZBLOCK] Descriptor inspection failed: {exception.Message}");
                return -1;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceOptimizeNsz")]
        public static int JniDeviceOptimizeNsz(int inputDescriptor, int outputDescriptor)
        {
            if (inputDescriptor == outputDescriptor) return Ryujinx.HLE.FileSystem.NczCompressor.ResultError;
            try
            {
                using FileStream input = new(new Microsoft.Win32.SafeHandles.SafeFileHandle(inputDescriptor, false), FileAccess.Read);
                using FileStream output = new(new Microsoft.Win32.SafeHandles.SafeFileHandle(outputDescriptor, false), FileAccess.ReadWrite);
                return Ryujinx.HLE.FileSystem.NszBlockOptimizer.Optimize(input, output);
            }
            catch (Exception exception)
            {
                Logger.Error?.Print(LogClass.Loader, $"[NEO-QOL4-NSZBLOCK] Descriptor conversion failed: {exception.Message}");
                return Ryujinx.HLE.FileSystem.NczCompressor.ResultError;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceOptimizeNszProgress")]
        public static int JniDeviceOptimizeNszProgress() => Ryujinx.HLE.FileSystem.NszBlockOptimizer.Permille;

        [UnmanagedCallersOnly(EntryPoint = "deviceOptimizeNszPhase")]
        public static int JniDeviceOptimizeNszPhase() => Ryujinx.HLE.FileSystem.NszBlockOptimizer.Phase;

        [UnmanagedCallersOnly(EntryPoint = "deviceOptimizeNszCancel")]
        public static void JniDeviceOptimizeNszCancel() => Ryujinx.HLE.FileSystem.NszBlockOptimizer.Cancel();

        [UnmanagedCallersOnly(EntryPoint = "deviceSetAudioBoost")]
        public static void JniDeviceSetAudioBoost(float gain)
        {
            Ryujinx.Audio.Backends.OpenAL.OpenALHardwareDeviceDriver.OutputBoost = Math.Clamp(gain, 1f, 2f);
            Logger.Info?.Print(LogClass.Audio, $"[NEOV2-AUDIO1] Amplificacao do audio: {Math.Clamp(gain, 1f, 2f) * 100:F0}%");
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceSetRejectUnmappedDescriptors")]
        public static void JniDeviceSetRejectUnmappedDescriptors(bool enable)
        {
            Ryujinx.Graphics.Gpu.GraphicsConfig.RejectUnmappedTextureDescriptors = enable;
        }

        // Chave por jogo "Compatibilidade grafica 2D" (correcoes do MARVEL Cosmic Invasion).
        [UnmanagedCallersOnly(EntryPoint = "deviceSetExperimentalGameFixes")]
        public static void JniDeviceSetExperimentalGameFixes(bool enable)
        {
            Ryujinx.Common.Diagnostics.NeoGraphicsSettings.ExperimentalGameFixesEnabled = enable;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceSetAsyncLarge3DTextureUpload")]
        public static void JniDeviceSetAsyncLarge3DTextureUpload(bool enable)
        {
            Ryujinx.Graphics.Gpu.GraphicsConfig.AsyncLarge3DTextureUpload = enable;
        }

        // Opcao por jogo "Otimizacoes de GPU" (o app envia o mesmo valor para todas, menos TEXBUF-ALIGN1, sempre ligado).
        [UnmanagedCallersOnly(EntryPoint = "deviceSetTurnipFence")]
        public static void JniDeviceSetTurnipFence(bool enable)
        {
            Ryujinx.Common.Diagnostics.NeoGraphicsSettings.TurnipFenceEnabled = enable;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceSetConditionalRenderNoWait")]
        public static void JniDeviceSetConditionalRenderNoWait(bool enable)
        {
            Ryujinx.Common.Diagnostics.NeoGraphicsSettings.ConditionalRenderNoWaitEnabled = enable;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceSetCommandFetchNoFlush")]
        public static void JniDeviceSetCommandFetchNoFlush(bool enable)
        {
            Ryujinx.Common.Diagnostics.NeoGraphicsSettings.CommandFetchWithoutFlushEnabled = enable;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceSetTexelBufferExactOffset")]
        public static void JniDeviceSetTexelBufferExactOffset(bool enable)
        {
            Ryujinx.Common.Diagnostics.NeoGraphicsSettings.TexelBufferExactOffsetEnabled = enable;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceSetQueueHysteresis")]
        public static void JniDeviceSetQueueHysteresis(bool enable)
        {
            Ryujinx.Common.Diagnostics.NeoGraphicsSettings.QueueHysteresisEnabled = enable;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceSetTimelineLazy")]
        public static void JniDeviceSetTimelineLazy(bool enable)
        {
            Ryujinx.Common.Diagnostics.NeoGraphicsSettings.TimelineLazyEnabled = enable;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceSetStagingBufferLock")]
        public static void JniDeviceSetStagingBufferLock(bool enable)
        {
            Ryujinx.Common.Diagnostics.NeoGraphicsSettings.StagingBufferLockEnabled = enable;
        }

        [UnmanagedCallersOnly(EntryPoint = "deviceSetTexturePoolKeepInvalid")]
        public static void JniDeviceSetTexturePoolKeepInvalid(bool enable)
        {
            Ryujinx.Common.Diagnostics.NeoGraphicsSettings.TexturePoolKeepInvalidEnabled = enable;
        }
#endif

        // Used by MainActivity/GameHost
        [UnmanagedCallersOnly(EntryPoint = "reattachWindowIfReady")]
        public static bool JniReattachWindowIfReady()
        {
            try
            {
                return (Renderer as VulkanRenderer)?.RecreateSurface() ?? false;
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"reattachWindowIfReady failed: {ex}");
                return false;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "detachWindow")]
        public static void JniDetachWindow()
        {
            try
            {
                (Renderer as VulkanRenderer)?.ReleaseSurface();
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"detachWindow failed: {ex}");
            }
        }

        // ===== Amiibo JNI Exports =====
        [UnmanagedCallersOnly(EntryPoint = "amiiboLoadBin")]
        public static bool JniAmiiboLoadBin(nint dataPtr, int length)
        {
            if (dataPtr == nint.Zero || length <= 0) return false;
            try
            {
                byte[] buf = new byte[length];
                Marshal.Copy(dataPtr, buf, 0, length);
                return AmiiboLoadFromBytes(buf);
            }
            catch
            {
                return false;
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "amiiboClear")]
        public static void JniAmiiboClear()
        {
            AmiiboClear();
        }
        // ===== End Amiibo JNI Exports =====

    }

    internal static partial class Logcat

    {
        [LibraryImport("liblog", StringMarshalling = StringMarshalling.Utf8)]
        private static partial void __android_log_print(LogLevel level, string? tag, string format, string args, nint ptr);

        internal static void AndroidLogPrint(LogLevel level, string? tag, string message) =>
            __android_log_print(level, tag, "%s", message, nint.Zero);

        internal enum LogLevel
        {
            Unknown = 0x00,
            Default = 0x01,
            Verbose = 0x02,
            Debug = 0x03,
            Info = 0x04,
            Warn = 0x05,
            Error = 0x06,
            Fatal = 0x07,
            Silent = 0x08,
        }

        // Opcao por jogo "Modo sem internet" (DOOM 2016).
        [UnmanagedCallersOnly(EntryPoint = "deviceSetNifmOffline")]
        public static void JniDeviceSetNifmOffline(bool enable)
        {
            Ryujinx.Common.Diagnostics.NeoGraphicsSettings.NifmOfflineEnabled = enable;
        }

        // Opcao por jogo "Termos offline" (BioShock).
        [UnmanagedCallersOnly(EntryPoint = "deviceSetWebOfflineTerms")]
        public static void JniDeviceSetWebOfflineTerms(bool enable)
        {
            Ryujinx.Common.Diagnostics.NeoGraphicsSettings.WebOfflineTermsEnabled = enable;
        }
    }
}
