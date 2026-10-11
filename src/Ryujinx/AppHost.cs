using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using LibHac.Common;
using LibHac.Ns;
using LibHac.Tools.FsSystem;
using Ryujinx.Audio.Backends.Dummy;
using Ryujinx.Audio.Backends.OpenAL;
using Ryujinx.Audio.Backends.SDL3;
using Ryujinx.Audio.Backends.SoundIo;
using Ryujinx.Audio.Integration;
using Ryujinx.Ava.Common;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Input;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.Models;
using Ryujinx.Ava.UI.Renderer;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Multiplayer;
using Ryujinx.Common.Diagnostics;
using Ryujinx.Common.Logging;
using Ryujinx.Common.SystemInterop;
using Ryujinx.Common.Utilities;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.OpenGL;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.HLE;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using Ryujinx.UI.App.Common;
using Ryujinx.UI.Common;
using Ryujinx.UI.Common.Configuration;
using Ryujinx.UI.Common.Helper;
using Silk.NET.Vulkan;
using SkiaSharp;
using SPB.Graphics.Exceptions;
using SPB.Graphics.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static Ryujinx.Ava.UI.Helpers.Win32NativeInterop;
using AntiAliasing = Ryujinx.Common.Configuration.AntiAliasing;
using InputManager = Ryujinx.Input.HLE.InputManager;
using IRenderer = Ryujinx.Graphics.GAL.IRenderer;
using Key = Ryujinx.Input.Key;
using MouseButton = Ryujinx.Input.MouseButton;
using ScalingFilter = Ryujinx.Common.Configuration.ScalingFilter;
using Size = Avalonia.Size;
using Switch = Ryujinx.HLE.Switch;
using VSyncMode = Ryujinx.Common.Configuration.VSyncMode;

namespace Ryujinx.Ava
{
    internal class AppHost : IDisposable
    {
        private const int CursorHideIdleTime = 5; // Hide Cursor seconds.
        private const float MaxResolutionScale = 4.0f; // Max resolution hotkeys can scale to before wrapping.
        private const int TargetFps = 60;
        private readonly GameSettingsProfile _gameSettingsProfile;
        private const float VolumeDelta = 0.05f;

        private static readonly Cursor _invisibleCursor = new(StandardCursorType.None);
        private readonly nint _invisibleCursorWin;
        private readonly nint _defaultCursorWin;

        private readonly long _ticksPerFrame;
        private readonly Stopwatch _chrono;
        private long _ticks;

        private readonly AccountManager _accountManager;
        private readonly UserChannelPersistence _userChannelPersistence;
        private readonly InputManager _inputManager;

        private readonly MainWindowViewModel _viewModel;
        private readonly IKeyboard _keyboardInterface;
        private readonly TopLevel _topLevel;
        public RendererHost RendererHost;

        private readonly GraphicsDebugLevel _glLogLevel;
        private float _newVolume;
        private KeyboardHotkeyState _prevHotkeyState;

        private long _lastCursorMoveTime;
        private bool _isCursorInRenderer = true;
        private bool _ignoreCursorState = false;

        private enum CursorStates
        {
            CursorIsHidden,
            CursorIsVisible,
            ForceChangeCursor
        };

        private CursorStates _cursorState = !ConfigurationState.Instance.Hid.EnableMouse.Value ?
            CursorStates.CursorIsVisible : CursorStates.CursorIsHidden;

        private DateTime _lastShaderReset;
        private uint _displayCount;
        private uint _previousCount = 0;

        private bool _isStopped;
        private bool _isActive;
        private bool _renderingStarted;

        private readonly ManualResetEvent _gpuDoneEvent;

        private IRenderer _renderer;
        private readonly Thread _renderingThread;
        private readonly CancellationTokenSource _gpuCancellationTokenSource;
        private WindowsMultimediaTimerResolution _windowsMultimediaTimerResolution;

        private bool _dialogShown;
        private readonly bool _isFirmwareTitle;

        private readonly Lock _lockObject = new();

        public event EventHandler AppExit;
        public event EventHandler<StatusUpdatedEventArgs> StatusUpdatedEvent;

        public VirtualFileSystem VirtualFileSystem { get; }
        public ContentManager ContentManager { get; }
        public NpadManager NpadManager { get; }
        public TouchScreenManager TouchScreenManager { get; }
        public Switch Device { get; set; }

        public int Width { get; private set; }
        public int Height { get; private set; }
        public string ApplicationPath { get; private set; }
        public ulong ApplicationId { get; private set; }
        public bool ScreenshotRequested { get; set; }

        public AppHost(
            RendererHost renderer,
            InputManager inputManager,
            string applicationPath,
            ulong applicationId,
            VirtualFileSystem virtualFileSystem,
            ContentManager contentManager,
            AccountManager accountManager,
            UserChannelPersistence userChannelPersistence,
            MainWindowViewModel viewmodel,
            TopLevel topLevel)
        {
            _viewModel = viewmodel;
            _inputManager = inputManager;
            _accountManager = accountManager;
            _userChannelPersistence = userChannelPersistence;
            _renderingThread = new Thread(RenderLoop) { Name = "GUI.RenderThread" };
            _lastCursorMoveTime = Stopwatch.GetTimestamp();
            _glLogLevel = ConfigurationState.Instance.Logger.GraphicsDebugLevel;
            _topLevel = topLevel;

            _inputManager.SetMouseDriver(new AvaloniaMouseDriver(_topLevel, renderer));

            _keyboardInterface = (IKeyboard)_inputManager.KeyboardDriver.GetGamepad("0");

            NpadManager = _inputManager.CreateNpadManager();
            TouchScreenManager = _inputManager.CreateTouchScreenManager();
            ApplicationPath = applicationPath;
            ApplicationId = applicationId;
            _gameSettingsProfile = GameSettingsProfileStore.Load(applicationId);


            // NCE-PROT1 must be selected before HLE/Switch/NCE creation.
            // Explicitly reset to false for games where it is not enabled.
            Ryujinx.Cpu.Nce.MemoryManagerNative.TrackingProtectionRestoreEnabled =
                _gameSettingsProfile.Enabled &&
                _gameSettingsProfile.NceProtectionRestore == true;

            Logger.Info?.PrintMsg(
                LogClass.Cpu,
                $"[NEOV2-NCE-PROT1] profile={applicationId:X16} enabled={Ryujinx.Cpu.Nce.MemoryManagerNative.TrackingProtectionRestoreEnabled}");
            if (_gameSettingsProfile.Enabled)
            {
                Logger.Info?.PrintMsg(
                    LogClass.Application,
                    $"[NEOV2-PROFILE] Enabled profile for {applicationId:X16}");
            }
            VirtualFileSystem = virtualFileSystem;
            ContentManager = contentManager;

            RendererHost = renderer;

            _chrono = new Stopwatch();
            _ticksPerFrame = Stopwatch.Frequency / TargetFps;

            if (ApplicationPath.StartsWith("@SystemContent"))
            {
                ApplicationPath = VirtualFileSystem.SwitchPathToSystemPath(ApplicationPath);

                _isFirmwareTitle = true;
            }

            ConfigurationState.Instance.HideCursor.Event += HideCursorState_Changed;

            _topLevel.PointerMoved += TopLevel_PointerEnteredOrMoved;
            _topLevel.PointerEntered += TopLevel_PointerEnteredOrMoved;
            _topLevel.PointerExited += TopLevel_PointerExited;

            if (OperatingSystem.IsWindows())
            {
                _invisibleCursorWin = CreateEmptyCursor();
                _defaultCursorWin = CreateArrowCursor();
            }

            ConfigurationState.Instance.System.IgnoreMissingServices.Event += UpdateIgnoreMissingServicesState;
            ConfigurationState.Instance.Graphics.AspectRatio.Event += UpdateAspectRatioState;
            ConfigurationState.Instance.System.EnableDockedMode.Event += UpdateDockedModeState;
            ConfigurationState.Instance.System.AudioVolume.Event += UpdateAudioVolumeState;
            ConfigurationState.Instance.Graphics.AntiAliasing.Event += UpdateAntiAliasing;
            ConfigurationState.Instance.Graphics.ScalingFilter.Event += UpdateScalingFilter;
            ConfigurationState.Instance.Graphics.ScalingFilterLevel.Event += UpdateScalingFilterLevel;
            ConfigurationState.Instance.Graphics.EnableColorSpacePassthrough.Event += UpdateColorSpacePassthrough;
            ConfigurationState.Instance.Graphics.VSyncMode.Event += UpdateVSyncMode;
            ConfigurationState.Instance.Graphics.CustomVSyncInterval.Event += UpdateCustomVSyncIntervalValue;
            ConfigurationState.Instance.Graphics.EnableCustomVSyncInterval.Event += UpdateCustomVSyncIntervalEnabled;

            ConfigurationState.Instance.System.EnableInternetAccess.Event += UpdateEnableInternetAccessState;
            ConfigurationState.Instance.Multiplayer.LanInterfaceId.Event += UpdateLanInterfaceIdState;
            ConfigurationState.Instance.Multiplayer.Mode.Event += UpdateMultiplayerModeState;

            _gpuCancellationTokenSource = new CancellationTokenSource();
            _gpuDoneEvent = new ManualResetEvent(false);
        }

        private void TopLevel_PointerEnteredOrMoved(object sender, PointerEventArgs e)
        {
            if (!_viewModel.IsActive)
            {
                _isCursorInRenderer = false;
                _ignoreCursorState = false;
                return;
            }

            if (sender is MainWindow window)
            {
                if (ConfigurationState.Instance.HideCursor.Value == HideCursorMode.OnIdle)
                {
                    _lastCursorMoveTime = Stopwatch.GetTimestamp();
                }

                var point = e.GetCurrentPoint(window).Position;
                var bounds = RendererHost.EmbeddedWindow.Bounds;
                double windowYOffset = bounds.Y + window.MenuBarHeight;
                double windowYLimit = (int)window.Bounds.Height - window.StatusBarHeight - 1;

                if (!_viewModel.ShowMenuAndStatusBar)
                {
                    windowYOffset -= window.MenuBarHeight;
                    windowYLimit += window.StatusBarHeight + 1;
                }

                _isCursorInRenderer = point.X >= bounds.X &&
                    Math.Ceiling(point.X) <= (int)window.Bounds.Width &&
                    point.Y >= windowYOffset &&
                    point.Y <= windowYLimit &&
                    !_viewModel.IsSubMenuOpen;

                _ignoreCursorState = false;
            }
        }

        private void TopLevel_PointerExited(object sender, PointerEventArgs e)
        {
            _isCursorInRenderer = false;

            if (sender is MainWindow window)
            {
                var point = e.GetCurrentPoint(window).Position;
                var bounds = RendererHost.EmbeddedWindow.Bounds;
                double windowYOffset = bounds.Y + window.MenuBarHeight;
                double windowYLimit = (int)window.Bounds.Height - window.StatusBarHeight - 1;

                if (!_viewModel.ShowMenuAndStatusBar)
                {
                    windowYOffset -= window.MenuBarHeight;
                    windowYLimit += window.StatusBarHeight + 1;
                }

                _ignoreCursorState = (point.X == bounds.X ||
                    Math.Ceiling(point.X) == (int)window.Bounds.Width) &&
                    point.Y >= windowYOffset &&
                    point.Y <= windowYLimit;
            }

            _cursorState = CursorStates.ForceChangeCursor;
        }

        private void UpdateScalingFilterLevel(object sender, ReactiveEventArgs<int> e)
        {
            _renderer.Window?.SetScalingFilter((Graphics.GAL.ScalingFilter)ConfigurationState.Instance.Graphics.ScalingFilter.Value);
            _renderer.Window?.SetScalingFilterLevel(ConfigurationState.Instance.Graphics.ScalingFilterLevel.Value);
        }

        private void UpdateScalingFilter(object sender, ReactiveEventArgs<ScalingFilter> e)
        {
            _renderer.Window?.SetScalingFilter((Graphics.GAL.ScalingFilter)ConfigurationState.Instance.Graphics.ScalingFilter.Value);
            _renderer.Window?.SetScalingFilterLevel(ConfigurationState.Instance.Graphics.ScalingFilterLevel.Value);
        }

        private void UpdateColorSpacePassthrough(object sender, ReactiveEventArgs<bool> e)
        {
            _renderer.Window?.SetColorSpacePassthrough((bool)ConfigurationState.Instance.Graphics.EnableColorSpacePassthrough.Value);
        }

        public void UpdateVSyncMode(object sender, ReactiveEventArgs<VSyncMode> e)
        {
            if (Device != null)
            {
                Device.VSyncMode = e.NewValue;
                Device.UpdateVSyncInterval();
            }
            _renderer.Window?.ChangeVSyncMode((Ryujinx.Graphics.GAL.VSyncMode)e.NewValue);

            _viewModel.ShowCustomVSyncIntervalPicker = (e.NewValue == VSyncMode.Custom);
        }

        public void VSyncModeToggle()
        {
            VSyncMode oldVSyncMode = Device.VSyncMode;
            VSyncMode newVSyncMode = VSyncMode.Switch;
            bool customVSyncIntervalEnabled = ConfigurationState.Instance.Graphics.EnableCustomVSyncInterval.Value;

            switch (oldVSyncMode)
            {
                case VSyncMode.Switch:
                    newVSyncMode = VSyncMode.Unbounded;
                    break;
                case VSyncMode.Unbounded:
                    if (customVSyncIntervalEnabled)
                    {
                        newVSyncMode = VSyncMode.Custom;
                    }
                    else
                    {
                        newVSyncMode = VSyncMode.Switch;
                    }

                    break;
                case VSyncMode.Custom:
                    newVSyncMode = VSyncMode.Switch;
                    break;
            }

            UpdateVSyncMode(this, new ReactiveEventArgs<VSyncMode>(oldVSyncMode, newVSyncMode));
        }

        private void UpdateCustomVSyncIntervalValue(object sender, ReactiveEventArgs<int> e)
        {
            if (Device != null)
            {
                Device.TargetVSyncInterval = e.NewValue;
                Device.UpdateVSyncInterval();
            }
        }

        private void UpdateCustomVSyncIntervalEnabled(object sender, ReactiveEventArgs<bool> e)
        {
            if (Device != null)
            {
                Device.CustomVSyncIntervalEnabled = e.NewValue;
                Device.UpdateVSyncInterval();
            }
        }

        private void ShowCursor()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _viewModel.Cursor = Cursor.Default;

                if (OperatingSystem.IsWindows())
                {
                    if (_cursorState != CursorStates.CursorIsHidden && !_ignoreCursorState)
                    {
                        SetCursor(_defaultCursorWin);
                    }
                }
            });

            _cursorState = CursorStates.CursorIsVisible;
        }

        private void HideCursor()
        {
            Dispatcher.UIThread.Post(() =>
            {
                _viewModel.Cursor = _invisibleCursor;

                if (OperatingSystem.IsWindows())
                {
                    SetCursor(_invisibleCursorWin);
                }
            });

            _cursorState = CursorStates.CursorIsHidden;
        }

        private void SetRendererWindowSize(Size size)
        {
            if (_renderer != null)
            {
                double scale = _topLevel.RenderScaling;

                _renderer.Window?.SetSize((int)(size.Width * scale), (int)(size.Height * scale));
            }
        }

        private void Renderer_ScreenCaptured(object sender, ScreenCaptureImageInfo e)
        {
            if (e.Data.Length > 0 && e.Height > 0 && e.Width > 0)
            {
                Task.Run(() =>
                {
                    lock (_lockObject)
                    {
                        string applicationName = Device.Processes.ActiveApplication.Name;
                        string sanitizedApplicationName = FileSystemUtils.SanitizeFileName(applicationName);
                        DateTime currentTime = DateTime.Now;

                        string filename = $"{sanitizedApplicationName}_{currentTime.Year}-{currentTime.Month:D2}-{currentTime.Day:D2}_{currentTime.Hour:D2}-{currentTime.Minute:D2}-{currentTime.Second:D2}.png";

                        string directory = Path.Combine(AppDataManager.BaseDirPath, "screenshots");

                        string path = Path.Combine(directory, filename);

                        try
                        {
                            Directory.CreateDirectory(directory);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error?.Print(LogClass.Application, $"Failed to create directory at path {directory}. Error : {ex.GetType().Name}", "Screenshot");

                            return;
                        }

                        var colorType = e.IsBgra ? SKColorType.Bgra8888 : SKColorType.Rgba8888;
                        using SKBitmap bitmap = new(new SKImageInfo(e.Width, e.Height, colorType, SKAlphaType.Premul));

                        Marshal.Copy(e.Data, 0, bitmap.GetPixels(), e.Data.Length);

                        using SKBitmap bitmapToSave = new(bitmap.Width, bitmap.Height);
                        using SKCanvas canvas = new(bitmapToSave);

                        canvas.Clear(SKColors.Black);

                        float scaleX = e.FlipX ? -1 : 1;
                        float scaleY = e.FlipY ? -1 : 1;

                        var matrix = SKMatrix.CreateScale(scaleX, scaleY, bitmap.Width / 2f, bitmap.Height / 2f);

                        canvas.SetMatrix(matrix);
                        canvas.DrawBitmap(bitmap, SKPoint.Empty);

                        SaveBitmapAsPng(bitmapToSave, path);

                        Logger.Notice.Print(LogClass.Application, $"Screenshot saved to '{path}'.", "Screenshot");
                    }
                });
            }
            else
            {
                Logger.Error?.Print(LogClass.Application, $"Screenshot is empty. Size : {e.Data.Length} bytes. Resolution : {e.Width}x{e.Height}", "Screenshot");
            }
        }

        private void SaveBitmapAsPng(SKBitmap bitmap, string path)
        {
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using FileStream stream = File.OpenWrite(path);

            data.SaveTo(stream);
        }

        public void Start()
        {
            ARMeilleure.Optimizations.EcoFriendly = ConfigurationState.Instance.System.EnableLowPowerPtc;
            ARMeilleure.Optimizations.CacheEviction = ConfigurationState.Instance.System.EnableJitCacheEviction;

            if (OperatingSystem.IsWindows())
            {
                _windowsMultimediaTimerResolution = new WindowsMultimediaTimerResolution(1);
            }

            DisplaySleep.Prevent();

            NpadManager.Initialize(Device, ConfigurationState.Instance.Hid.InputConfig, ConfigurationState.Instance.Hid.EnableKeyboard, ConfigurationState.Instance.Hid.EnableMouse);
            TouchScreenManager.Initialize(Device);

            _viewModel.IsGameRunning = true;

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                _viewModel.Title = TitleHelper.ActiveApplicationTitle(Device.Processes.ActiveApplication, Program.Version);
            });

            _viewModel.SetUiProgressHandlers(Device);

            RendererHost.BoundsChanged += Window_BoundsChanged;

            _isActive = true;

            _renderingThread.Start();

            _viewModel.Volume = ConfigurationState.Instance.System.AudioVolume.Value;

            MainLoop();

            Exit();
        }

        private void UpdateIgnoreMissingServicesState(object sender, ReactiveEventArgs<bool> args)
        {
            if (Device != null)
            {
                Device.Configuration.IgnoreMissingServices = args.NewValue;
            }
        }

        private void UpdateAspectRatioState(object sender, ReactiveEventArgs<AspectRatio> args)
        {
            if (Device != null)
            {
                Device.Configuration.AspectRatio = args.NewValue;
            }
        }

        private void UpdateAntiAliasing(object sender, ReactiveEventArgs<AntiAliasing> e)
        {
            _renderer?.Window?.SetAntiAliasing((Graphics.GAL.AntiAliasing)e.NewValue);
        }

        private void UpdateDockedModeState(object sender, ReactiveEventArgs<bool> e)
        {
            bool effectiveDockedMode =
                _gameSettingsProfile.Enabled &&
                _gameSettingsProfile.EnableDockedMode.HasValue
                    ? _gameSettingsProfile.EnableDockedMode.Value
                    : e.NewValue;

            Device?.System.ChangeDockedModeState(effectiveDockedMode);
        }

        private void UpdateAudioVolumeState(object sender, ReactiveEventArgs<float> e)
        {
            Device?.SetVolume(e.NewValue);

            Dispatcher.UIThread.Post(() =>
            {
                _viewModel.Volume = e.NewValue;
            });
        }

        private void UpdateEnableInternetAccessState(object sender, ReactiveEventArgs<bool> e)
        {
            Device.Configuration.EnableInternetAccess = e.NewValue;
        }

        private void UpdateLanInterfaceIdState(object sender, ReactiveEventArgs<string> e)
        {
            Device.Configuration.MultiplayerLanInterfaceId = e.NewValue;
        }

        private void UpdateMultiplayerModeState(object sender, ReactiveEventArgs<MultiplayerMode> e)
        {
            Device.Configuration.MultiplayerMode = e.NewValue;
        }

        public void Stop()
        {
            _isActive = false;
            DiscordIntegrationModule.SwitchToMainState();
        }

        private void Exit()
        {
            (_keyboardInterface as AvaloniaKeyboard)?.Clear();

            if (_isStopped)
            {
                return;
            }

            _isStopped = true;
            Stop();
        }

        public void DisposeContext()
        {
            Dispose();

            _isActive = false;

            DisplaySleep.Restore();

            NpadManager.Dispose();
            TouchScreenManager.Dispose();
            Device.Dispose();

            // NOTE: The render loop is allowed to stay alive until the renderer itself is disposed, as it may handle resource dispose.
            // We only need to wait for all commands submitted during the main gpu loop to be processed.
            // If the GPU has no work and is cancelled, we need to handle that as well.

            WaitHandle.WaitAny(new[] { _gpuDoneEvent, _gpuCancellationTokenSource.Token.WaitHandle });
            if (_renderingStarted)
            {
                // Waiting for work to be finished before we dispose.
                Device.Gpu.WaitUntilGpuReady();
            }

            _gpuDoneEvent.Dispose();
            _gpuCancellationTokenSource.Dispose();

            DisposeGpu();
            AppExit?.Invoke(this, EventArgs.Empty);
        }

        // MUST be public to inherit from IDisposable
        public void Dispose()
        {
            if (Device.Processes != null)
            {
                MainWindowViewModel.UpdateGameMetadata(Device.Processes.ActiveApplication?.ProgramIdText);
            }

            ConfigurationState.Instance.System.IgnoreMissingServices.Event -= UpdateIgnoreMissingServicesState;
            ConfigurationState.Instance.Graphics.AspectRatio.Event -= UpdateAspectRatioState;
            ConfigurationState.Instance.System.EnableDockedMode.Event -= UpdateDockedModeState;
            ConfigurationState.Instance.System.AudioVolume.Event -= UpdateAudioVolumeState;
            ConfigurationState.Instance.Graphics.ScalingFilter.Event -= UpdateScalingFilter;
            ConfigurationState.Instance.Graphics.ScalingFilterLevel.Event -= UpdateScalingFilterLevel;
            ConfigurationState.Instance.Graphics.AntiAliasing.Event -= UpdateAntiAliasing;
            ConfigurationState.Instance.Graphics.EnableColorSpacePassthrough.Event -= UpdateColorSpacePassthrough;

            _topLevel.PointerMoved -= TopLevel_PointerEnteredOrMoved;
            _topLevel.PointerEntered -= TopLevel_PointerEnteredOrMoved;
            _topLevel.PointerExited -= TopLevel_PointerExited;

            _gpuCancellationTokenSource.Cancel();

            _chrono.Stop();
        }

        public void DisposeGpu()
        {
            if (OperatingSystem.IsWindows())
            {
                _windowsMultimediaTimerResolution?.Dispose();
                _windowsMultimediaTimerResolution = null;
            }

            if (RendererHost.EmbeddedWindow is EmbeddedWindowOpenGL openGlWindow)
            {
                try
                {
                    // Try to bind the OpenGL context before disposing GPU resources.
                    openGlWindow.MakeCurrent();
                }
                catch (ContextException e) when (_userChannelPersistence.ShouldRestart)
                {
                    // ExecuteProgram may detach the old native window before GPU
                    // disposal. Allow the old context to be released with the window
                    // and continue the requested program relaunch.
                    Logger.Warning?.Print(
                        LogClass.UI,
                        $"Failed to bind OpenGL context during program relaunch: {e}");

                    return;
                }

                Device.DisposeGpu();

                // Unbind context and destroy everything.
                openGlWindow.MakeCurrent(true, false);
            }
            else
            {
                // No use waiting on something that never started work
                if (_renderingStarted)
                {
                    Device.Gpu.WaitUntilGpuReady();
                }

                Device.DisposeGpu();
            }
        }

        private void HideCursorState_Changed(object sender, ReactiveEventArgs<HideCursorMode> state)
        {
            if (state.NewValue == HideCursorMode.OnIdle)
            {
                _lastCursorMoveTime = Stopwatch.GetTimestamp();
            }

            _cursorState = CursorStates.ForceChangeCursor;
        }

        public async Task LoadGuestApplication(CancellationTokenSource cts, BlitStruct<ApplicationControlProperty>? customNacpData = null)
        {
            // Establish desktop global graphics defaults first.
            MainWindow.UpdateGraphicsConfig();

            // InitializeSwitchInstance applies the per-game overrides before
            // creating the renderer and GPU/HLE consumers.
            InitializeSwitchInstance();

            SystemVersion firmwareVersion = ContentManager.GetCurrentFirmwareVersion();

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (!SetupValidator.CanStartApplication(ContentManager, ApplicationPath, out UserError userError))
                {
                    {
                        if (SetupValidator.CanFixStartApplication(ContentManager, ApplicationPath, userError, out firmwareVersion))
                        {
                            if (userError == UserError.NoFirmware)
                            {
                                UserResult result = await ContentDialogHelper.CreateConfirmationDialog(
                                    LocaleManager.Instance[LocaleKeys.DialogFirmwareNoFirmwareInstalledMessage],
                                    LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.DialogFirmwareInstallEmbeddedMessage, firmwareVersion.VersionString),
                                    LocaleManager.Instance[LocaleKeys.InputDialogYes],
                                    LocaleManager.Instance[LocaleKeys.InputDialogNo],
                                    "");

                                if (result != UserResult.Yes)
                                {
                                    await UserErrorDialog.ShowUserErrorDialog(userError);
                                    Device.Dispose();

                                    cts.Cancel();
                                throw new OperationCanceledException(cts.Token);
                                }
                            }

                            if (!SetupValidator.TryFixStartApplication(ContentManager, ApplicationPath, userError, out _))
                            {
                                await UserErrorDialog.ShowUserErrorDialog(userError);
                                Device.Dispose();

                                cts.Cancel();
                            throw new OperationCanceledException(cts.Token);
                            }

                            // Tell the user that we installed firmware for them.
                            if (userError == UserError.NoFirmware)
                            {
                                firmwareVersion = ContentManager.GetCurrentFirmwareVersion();

                                _viewModel.RefreshFirmwareStatus();

                                await ContentDialogHelper.CreateInfoDialog(
                                    LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.DialogFirmwareInstalledMessage, firmwareVersion.VersionString),
                                    LocaleManager.Instance.UpdateAndGetDynamicValue(LocaleKeys.DialogFirmwareInstallEmbeddedSuccessMessage, firmwareVersion.VersionString),
                                    LocaleManager.Instance[LocaleKeys.InputDialogOk],
                                    "",
                                    LocaleManager.Instance[LocaleKeys.RyujinxInfo]);
                            }
                        }
                        else
                        {
                            await UserErrorDialog.ShowUserErrorDialog(userError);
                            Device.Dispose();

                            cts.Cancel();
                        throw new OperationCanceledException(cts.Token);
                        }
                    }
                }
            }

            Logger.Notice.Print(LogClass.Application, $"Using Firmware Version: {firmwareVersion?.VersionString}");

            if (_isFirmwareTitle)
            {
                Logger.Info?.Print(LogClass.Application, "Loading as Firmware Title (NCA).");

                if (!Device.LoadNca(ApplicationPath))
                {
                    Device.Dispose();

                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }
            }
            else if (Directory.Exists(ApplicationPath))
            {
                string[] romFsFiles = Directory.GetFiles(ApplicationPath, "*.istorage");

                if (romFsFiles.Length == 0)
                {
                    romFsFiles = Directory.GetFiles(ApplicationPath, "*.romfs");
                }

                if (romFsFiles.Length > 0)
                {
                    Logger.Info?.Print(LogClass.Application, "Loading as cart with RomFS.");

                    if (!Device.LoadCart(ApplicationPath, romFsFiles[0]))
                    {
                        await ContentDialogHelper.CreateErrorDialog(
                            "Please specify an unpacked game directory with a valid exefs or NSO/NRO.");
                        Device.Dispose();

                        cts.Cancel();
                        throw new OperationCanceledException(cts.Token);
                    }
                }
                else
                {
                    Logger.Info?.Print(LogClass.Application, "Loading as cart WITHOUT RomFS.");
                    if (!Device.LoadCart(ApplicationPath))
                    {
                        await ContentDialogHelper.CreateErrorDialog(
                            "Please specify an unpacked game directory with a valid exefs or NSO/NRO.");
                        Device.Dispose();
                        cts.Cancel();
                        throw new OperationCanceledException(cts.Token);
                    }
                }
            }
            else if (File.Exists(ApplicationPath))
            {
                switch (Path.GetExtension(ApplicationPath).ToLowerInvariant())
                {
                    case ".xci":
                    case ".xcz":
                        {
                            Logger.Info?.Print(LogClass.Application, "Loading as XCI.");

                            if (!Device.LoadXci(ApplicationPath, ApplicationId))
                            {
                                Device.Dispose();

                                cts.Cancel();
                                throw new OperationCanceledException(cts.Token);
                            }

                            break;
                        }
                    case ".nca":
                        {
                            Logger.Info?.Print(LogClass.Application, "Loading as NCA.");

                            if (!Device.LoadNca(ApplicationPath))
                            {
                                Device.Dispose();

                                cts.Cancel();
                                throw new OperationCanceledException(cts.Token);
                            }

                            break;
                        }
                    case ".nsp":
                    case ".nsz":
                    case ".pfs0":
                        {
                            Logger.Info?.Print(LogClass.Application, "Loading as NSP.");

                            if (!Device.LoadNsp(ApplicationPath, ApplicationId))
                            {
                                Device.Dispose();

                                cts.Cancel();
                                throw new OperationCanceledException(cts.Token);
                            }

                            break;
                        }
                    default:
                        {
                            Logger.Info?.Print(LogClass.Application, "Loading as homebrew.");

                            try
                            {
                                if (!Device.LoadProgram(ApplicationPath))
                                {
                                    Device.Dispose();

                                    cts.Cancel();
                                    throw new OperationCanceledException(cts.Token);
                                }
                            }
                            catch (ArgumentOutOfRangeException)
                            {
                                Logger.Error?.Print(LogClass.Application, "The specified file is not supported by Ryujinx.");

                                Device.Dispose();

                                cts.Cancel();
                                throw new OperationCanceledException(cts.Token);
                            }

                            break;
                        }
                }
            }
            else
            {
                Logger.Warning?.Print(LogClass.Application, "Please specify a valid XCI/NCA/NSP/PFS0/NSO/NRO file.");

                Device.Dispose();

                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }

            ApplicationMetadata appMeta = ApplicationLibrary.LoadAndSaveMetaData(Device.Processes.ActiveApplication.ProgramIdText, 
                appMetadata => appMetadata.UpdatePreGame()
            );

            DiscordIntegrationModule.SwitchToPlayingState(appMeta, Device.Processes.ActiveApplication);
        }

        internal void Resume()
        {
            Device?.System.TogglePauseEmulation(false);

            _viewModel.IsPaused = false;
            _viewModel.Title = TitleHelper.ActiveApplicationTitle(Device?.Processes.ActiveApplication, Program.Version);
            Logger.Info?.Print(LogClass.Emulation, "Emulation was resumed.");
        }

        internal void Pause()
        {
            Device?.System.TogglePauseEmulation(true);

            _viewModel.IsPaused = true;
            _viewModel.Title = TitleHelper.ActiveApplicationTitle(Device?.Processes.ActiveApplication, Program.Version, LocaleManager.Instance[LocaleKeys.Paused]);
            Logger.Info?.Print(LogClass.Emulation, "Emulation was paused.");
        }


        private void ApplyGameGraphicsProfile()
        {
            ConfigurationState.GraphicsSection global = ConfigurationState.Instance.Graphics;
            bool profileEnabled = _gameSettingsProfile.Enabled;

            int resScaleSetting =
                profileEnabled && _gameSettingsProfile.ResScale.HasValue
                    ? _gameSettingsProfile.ResScale.Value
                    : global.ResScale.Value;

            float resScaleCustom =
                profileEnabled && _gameSettingsProfile.ResScaleCustom.HasValue
                    ? _gameSettingsProfile.ResScaleCustom.Value
                    : global.ResScaleCustom.Value;

            GraphicsConfig.ResScale =
                resScaleSetting == -1
                    ? resScaleCustom
                    : resScaleSetting;

            GraphicsConfig.MaxAnisotropy =
                profileEnabled && _gameSettingsProfile.MaxAnisotropy.HasValue
                    ? _gameSettingsProfile.MaxAnisotropy.Value
                    : global.MaxAnisotropy.Value;

            GraphicsConfig.ShadersDumpPath = global.ShadersDumpPath.Value;

            GraphicsConfig.EnableShaderCache =
                profileEnabled && _gameSettingsProfile.EnableShaderCache.HasValue
                    ? _gameSettingsProfile.EnableShaderCache.Value
                    : global.EnableShaderCache.Value;

            // A UI Linux ainda não possui um toggle global para esta opção.
            // Portanto, sem override por jogo, usamos o default seguro do NEOV2.
            GraphicsConfig.EnableShaderLoopSafety =
                profileEnabled && _gameSettingsProfile.EnableShaderLoopSafety.HasValue
                    ? _gameSettingsProfile.EnableShaderLoopSafety.Value
                    : false;

            GraphicsConfig.EnableTextureRecompression =
                profileEnabled && _gameSettingsProfile.EnableTextureRecompression.HasValue
                    ? _gameSettingsProfile.EnableTextureRecompression.Value
                    : global.EnableTextureRecompression.Value;

            GraphicsConfig.EnableMacroHLE =
                profileEnabled && _gameSettingsProfile.EnableMacroHLE.HasValue
                    ? _gameSettingsProfile.EnableMacroHLE.Value
                    : global.EnableMacroHLE.Value;

            // NEOV2-PERGAME-EXTRA
            // Flags do GraphicsConfig sem configuração global equivalente.
            GraphicsConfig.RelaxedDmaCopy =
                profileEnabled && _gameSettingsProfile.RelaxedDmaCopy.HasValue
                    ? _gameSettingsProfile.RelaxedDmaCopy.Value
                    : false;

            GraphicsConfig.EnableVtgViaCompute =
                profileEnabled && _gameSettingsProfile.EnableVtgViaCompute.HasValue
                    ? _gameSettingsProfile.EnableVtgViaCompute.Value
                    : true;

            GraphicsConfig.RejectUnmappedTextureDescriptors =
                profileEnabled && _gameSettingsProfile.RejectUnmappedTextureDescriptors.HasValue
                    ? _gameSettingsProfile.RejectUnmappedTextureDescriptors.Value
                    : false;

            GraphicsConfig.AsyncLarge3DTextureUpload =
                profileEnabled && _gameSettingsProfile.AsyncLarge3DTextureUpload.HasValue
                    ? _gameSettingsProfile.AsyncLarge3DTextureUpload.Value
                    : true;

            // Pacote GPU do 2a1c968.
            // TEXBUF-ALIGN não faz parte do toggle: permanece sempre ativo.
            bool gpuOptimizations =
                profileEnabled && _gameSettingsProfile.GpuOptimizations.HasValue
                    ? _gameSettingsProfile.GpuOptimizations.Value
                    : true;

            NeoGraphicsSettings.TurnipFenceEnabled = gpuOptimizations;
            // NEOV2 GLOBAL-TIMELINE-BATCH1: use the lower-overhead timeline submit path by
            // default for every title. A per-game false remains available as a compatibility
            // escape hatch for titles or drivers that regress.
            NeoGraphicsSettings.TurnipTimelineBatchingEnabled =
                !profileEnabled || _gameSettingsProfile.TurnipTimelineBatching != false;
            NeoGraphicsSettings.ConditionalRenderNoWaitEnabled = gpuOptimizations;
            NeoGraphicsSettings.CommandFetchWithoutFlushEnabled = gpuOptimizations;
            NeoGraphicsSettings.QueueHysteresisEnabled = gpuOptimizations;
            NeoGraphicsSettings.TimelineLazyEnabled = gpuOptimizations;
            NeoGraphicsSettings.StagingBufferLockEnabled = gpuOptimizations;
            NeoGraphicsSettings.TexturePoolKeepInvalidEnabled = gpuOptimizations;

            // Este fix é intencionalmente permanente.
            NeoGraphicsSettings.TexelBufferExactOffsetEnabled = true;

            NeoGraphicsSettings.ExperimentalGameFixesEnabled =
                profileEnabled && _gameSettingsProfile.ExperimentalGameFixesEnabled.HasValue
                    ? _gameSettingsProfile.ExperimentalGameFixesEnabled.Value
                    : false;

            NeoGraphicsSettings.NifmOfflineEnabled =
                profileEnabled && _gameSettingsProfile.NifmOfflineEnabled.HasValue
                    ? _gameSettingsProfile.NifmOfflineEnabled.Value
                    : false;

            // Overrides do backend. Null = usar a configuração global.
            NeoGraphicsSettings.DrawFlushGameOverride =
                profileEnabled
                    ? _gameSettingsProfile.DrawFlushMode
                    : null;

            NeoGraphicsSettings.BufferReadbackGameOverride =
                profileEnabled
                    ? _gameSettingsProfile.BufferReadbackMode
                    : null;

            NeoGraphicsSettings.StrictSyncGameOverride =
                profileEnabled
                    ? _gameSettingsProfile.StrictSyncMode
                    : null;

            NeoGraphicsSettings.DrawFlushMode =
                NeoGraphicsSettings.DrawFlushGameOverride ??
                NeoGraphicsSettings.DrawFlushGlobalMode;

            NeoGraphicsSettings.BufferReadbackMode =
                NeoGraphicsSettings.BufferReadbackGameOverride ??
                NeoGraphicsSettings.BufferReadbackGlobalMode;

            NeoGraphicsSettings.StrictSyncMode =
                NeoGraphicsSettings.StrictSyncGameOverride ??
                NeoGraphicsSettings.StrictSyncGlobalMode;

            Logger.Info?.PrintMsg(
                LogClass.Gpu,
                $"[NEOV2-PROFILE-GPU] TitleId={ApplicationId:X16} " +
                $"enabled={profileEnabled} " +
                $"ResScale={GraphicsConfig.ResScale} " +
                $"Aniso={GraphicsConfig.MaxAnisotropy} " +
                $"ShaderCache={GraphicsConfig.EnableShaderCache} " +
                $"LoopSafety={GraphicsConfig.EnableShaderLoopSafety} " +
                $"TextureRecompression={GraphicsConfig.EnableTextureRecompression} " +
                $"MacroHLE={GraphicsConfig.EnableMacroHLE} " +
                $"RelaxedDMA={GraphicsConfig.RelaxedDmaCopy} " +
                $"VTG={GraphicsConfig.EnableVtgViaCompute} " +
                $"RejectUnmapped={GraphicsConfig.RejectUnmappedTextureDescriptors} " +
                $"Async3D={GraphicsConfig.AsyncLarge3DTextureUpload} " +
                $"GpuOpt={gpuOptimizations} " +
                $"Experimental2D={NeoGraphicsSettings.ExperimentalGameFixesEnabled} " +
                $"NifmOffline={NeoGraphicsSettings.NifmOfflineEnabled} " +
                $"DrawFlush={NeoGraphicsSettings.DrawFlushMode} " +
                $"Readback={NeoGraphicsSettings.BufferReadbackMode} " +
                $"StrictSync={NeoGraphicsSettings.StrictSyncMode}");
        }

        private void InitializeSwitchInstance()
        {
            // Initialize KeySet.
            VirtualFileSystem.ReloadKeySet();

            // NEOV2: global GraphicsConfig was established by LoadGuestApplication.
            // Apply the per-game overrides now, before creating the renderer.
            ApplyGameGraphicsProfile();

            // Initialize Renderer.
            IRenderer renderer;

            if (ConfigurationState.Instance.Graphics.GraphicsBackend.Value == GraphicsBackend.Vulkan)
            {
                renderer = new VulkanRenderer(
                    Vk.GetApi(),
                    ((EmbeddedWindowVulkan)RendererHost.EmbeddedWindow).CreateSurface,
                    VulkanHelper.GetRequiredInstanceExtensions,
                    ConfigurationState.Instance.Graphics.PreferredGpu.Value);
            }
            else
            {
                renderer = new OpenGLRenderer();
            }

            BackendThreading threadingMode = ConfigurationState.Instance.Graphics.BackendThreading;

            bool isGALThreaded = threadingMode == BackendThreading.On || (threadingMode == BackendThreading.Auto && renderer.PreferThreading);
            if (isGALThreaded)
            {
                renderer = new ThreadedRenderer(renderer);
            }

            Logger.Info?.PrintMsg(LogClass.Gpu, $"Backend Threading ({threadingMode}): {isGALThreaded}");

            // Initialize Configuration.
            MemoryConfiguration memoryConfiguration = ConfigurationState.Instance.System.DramSize.Value;

            bool profileEnabled = _gameSettingsProfile.Enabled;

            bool useNce =
                profileEnabled && _gameSettingsProfile.UseNce.HasValue
                    ? _gameSettingsProfile.UseNce.Value
                    : ConfigurationState.Instance.System.UseHypervisor.Value;

            bool enablePtc =
                profileEnabled && _gameSettingsProfile.EnablePtc.HasValue
                    ? _gameSettingsProfile.EnablePtc.Value
                    : ConfigurationState.Instance.System.EnablePtc.Value;

            VSyncMode vSyncMode =
                profileEnabled && _gameSettingsProfile.VSyncMode.HasValue
                    ? _gameSettingsProfile.VSyncMode.Value
                    : ConfigurationState.Instance.Graphics.VSyncMode.Value;

            bool dockedMode =
                profileEnabled && _gameSettingsProfile.EnableDockedMode.HasValue
                    ? _gameSettingsProfile.EnableDockedMode.Value
                    : ConfigurationState.Instance.System.EnableDockedMode.Value;

            const ulong TotkTitleId = 0x0100F2C0115B6000UL;

            bool memorySlackEnabled =
                profileEnabled && _gameSettingsProfile.MemorySlackEnabled.HasValue
                    ? _gameSettingsProfile.MemorySlackEnabled.Value
                    : ApplicationId == TotkTitleId;

            // NEOV2 TOTK-MEMSLACK2: 12 GiB of extra guest memory creates a 16 GiB
            // shared backing file and drives 12 GiB hosts into zram. TotK keeps a
            // generous 4 GiB extension (about 7.3 GiB application memory total)
            // without allowing the guest/driver working set to grow unbounded.
            ulong memorySlackMiB = ApplicationId == TotkTitleId ? 4096UL : 12288UL;

            ulong memorySlack = memorySlackEnabled
                ? memorySlackMiB * 1024 * 1024
                : 0UL;

            Logger.Info?.PrintMsg(
                LogClass.Emulation,
                $"[NEOV2-PROFILE] TitleId={ApplicationId:X16} enabled={profileEnabled} NCE={useNce} PPTC={enablePtc} Docked={dockedMode} VSync={vSyncMode} MemorySlack={memorySlack / 1024 / 1024}MB");

                        HLEConfiguration configuration = new(VirtualFileSystem,
                                                     _viewModel.LibHacHorizonManager,
                                                     ContentManager,
                                                     _accountManager,
                                                     _userChannelPersistence,
                                                     renderer,
                                                     InitializeAudio(),
                                                     memoryConfiguration,
                                                     memorySlack,
                                                     _viewModel.UiHandler,
                                                     (SystemLanguage)ConfigurationState.Instance.System.Language.Value,
                                                     (RegionCode)ConfigurationState.Instance.System.Region.Value,
                                                     vSyncMode,
                                                     dockedMode,
                                                     enablePtc,
                                                     ConfigurationState.Instance.System.EnableInternetAccess,
                                                     ConfigurationState.Instance.System.EnableFsIntegrityChecks ? IntegrityCheckLevel.ErrorOnInvalid : IntegrityCheckLevel.None,
                                                     ConfigurationState.Instance.System.FsGlobalAccessLogMode,
                                                     ConfigurationState.Instance.System.MatchSystemTime 
                                                         ? 0 
                                                         : ConfigurationState.Instance.System.SystemTimeOffset,
                                                     ConfigurationState.Instance.System.TimeZone,
                                                     ConfigurationState.Instance.System.MemoryManagerMode.Value,
                                                     ConfigurationState.Instance.System.IgnoreMissingServices,
                                                     ConfigurationState.Instance.Graphics.AspectRatio,
                                                     ConfigurationState.Instance.System.AudioVolume,
                                                     useNce,
                                                     ConfigurationState.Instance.Multiplayer.LanInterfaceId.Value,
                                                     ConfigurationState.Instance.Multiplayer.Mode,
                                                     ConfigurationState.Instance.Multiplayer.DisableP2p,
                                                     ConfigurationState.Instance.Multiplayer.LdnPassphrase,
                                                     ConfigurationState.Instance.Multiplayer.LdnServer,
                                                     ConfigurationState.Instance.Graphics.CustomVSyncInterval.Value);

            Device = new Switch(configuration);
        }

        private static IHardwareDeviceDriver InitializeAudio()
        {
            List<AudioBackend> availableBackends = new()
            {
                AudioBackend.SDL3,
                AudioBackend.SoundIo,
                AudioBackend.OpenAl,
                AudioBackend.Dummy,
            };

            AudioBackend preferredBackend = ConfigurationState.Instance.System.AudioBackend.Value;

            for (int i = 0; i < availableBackends.Count; i++)
            {
                if (availableBackends[i] == preferredBackend)
                {
                    availableBackends.RemoveAt(i);
                    availableBackends.Insert(0, preferredBackend);
                    break;
                }
            }

            static IHardwareDeviceDriver InitializeAudioBackend<T>(AudioBackend backend, AudioBackend nextBackend) where T : IHardwareDeviceDriver, new()
            {
                if (T.IsSupported)
                {
                    return new T();
                }

                Logger.Warning?.Print(LogClass.Audio, $"{backend} is not supported, falling back to {nextBackend}.");

                return null;
            }

            IHardwareDeviceDriver deviceDriver = null;

            for (int i = 0; i < availableBackends.Count; i++)
            {
                AudioBackend currentBackend = availableBackends[i];
                AudioBackend nextBackend = i + 1 < availableBackends.Count ? availableBackends[i + 1] : AudioBackend.Dummy;

                deviceDriver = currentBackend switch
                {
                    AudioBackend.SDL3 => InitializeAudioBackend<SDL3HardwareDeviceDriver>(AudioBackend.SDL3, nextBackend),
                    AudioBackend.SoundIo => InitializeAudioBackend<SoundIoHardwareDeviceDriver>(AudioBackend.SoundIo, nextBackend),
                    AudioBackend.OpenAl => InitializeAudioBackend<OpenALHardwareDeviceDriver>(AudioBackend.OpenAl, nextBackend),
                    _ => new DummyHardwareDeviceDriver(),
                };

                if (deviceDriver != null)
                {
                    ConfigurationState.Instance.System.AudioBackend.Value = currentBackend;
                    break;
                }
            }

            MainWindowViewModel.SaveConfig();

            return deviceDriver;
        }

        private void Window_BoundsChanged(object sender, Size e)
        {
            Width = (int)e.Width;
            Height = (int)e.Height;

            SetRendererWindowSize(e);
        }

        private void MainLoop()
        {
            while (UpdateFrame())
            {
                // Polling becomes expensive if it's not slept.
                Thread.Sleep(1);
            }
        }

        private void RenderLoop()
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_viewModel.StartGamesInFullscreen && _viewModel.WindowState is not WindowState.FullScreen)
                {
                    // Use the view model toggle so decoration ordering matches user toggles.
                    _viewModel.ToggleFullscreen();
                }

                if (_viewModel.WindowState == WindowState.FullScreen || _viewModel.StartGamesWithoutUI)
                {
                    _viewModel.ShowMenuAndStatusBar = false;
                }
            });

            _renderer = Device.Gpu.Renderer is ThreadedRenderer tr ? tr.BaseRenderer : Device.Gpu.Renderer;

            _renderer.ScreenCaptured += Renderer_ScreenCaptured;

            (RendererHost.EmbeddedWindow as EmbeddedWindowOpenGL)?.InitializeBackgroundContext(_renderer);

            Device.Gpu.Renderer.Initialize(_glLogLevel);

            _renderer?.Window?.SetAntiAliasing((Graphics.GAL.AntiAliasing)ConfigurationState.Instance.Graphics.AntiAliasing.Value);
            _renderer?.Window?.SetScalingFilter((Graphics.GAL.ScalingFilter)ConfigurationState.Instance.Graphics.ScalingFilter.Value);
            _renderer?.Window?.SetScalingFilterLevel(ConfigurationState.Instance.Graphics.ScalingFilterLevel.Value);
            _renderer?.Window?.SetColorSpacePassthrough(ConfigurationState.Instance.Graphics.EnableColorSpacePassthrough.Value);

            Width = (int)RendererHost.Bounds.Width;
            Height = (int)RendererHost.Bounds.Height;

            _renderer?.Window?.SetSize((int)(Width * _topLevel.RenderScaling), (int)(Height * _topLevel.RenderScaling));

            _chrono.Start();

            Device.Gpu.Renderer.RunLoop(() =>
            {
                try
                {
                    Device.Gpu.SetGpuThread();
                    Device.Gpu.InitializeShaderCache(_gpuCancellationTokenSource.Token);

                    _renderer.Window.ChangeVSyncMode((Ryujinx.Graphics.GAL.VSyncMode)Device.VSyncMode);

                    while (_isActive)
                    {
                        _ticks += _chrono.ElapsedTicks;

                        _chrono.Restart();

                        if (Device.WaitFifo())
                        {
                            Device.Statistics.RecordFifoStart();
                            Device.ProcessFrame();
                            Device.Statistics.RecordFifoEnd();
                        }

                        while (Device.ConsumeFrameAvailable())
                        {
                            if (!_renderingStarted)
                            {
                                _renderingStarted = true;
                                _viewModel.SwitchToRenderer(false);
                                InitStatus();
                            }

                            Device.PresentFrame(() =>
                                (RendererHost.EmbeddedWindow as EmbeddedWindowOpenGL)?.SwapBuffers());
                        }

                        if (_ticks >= _ticksPerFrame)
                        {
                            UpdateStatus();
                        }
                    }
                }
                finally
                {
                    // Make sure all commands in the run loop are fully executed before leaving the loop.
                    if (Device.Gpu.Renderer is ThreadedRenderer threaded)
                    {
                        Logger.Info?.PrintMsg(LogClass.Gpu, "Flushing threaded commands...");
                        threaded.FlushThreadedCommands();
                        Logger.Info?.PrintMsg(LogClass.Gpu, "Flushed!");
                    }
                    _gpuDoneEvent.Set();
                }
            });

            (RendererHost.EmbeddedWindow as EmbeddedWindowOpenGL)?.MakeCurrent(true);
        }

        public void InitStatus()
        {
            _viewModel.BackendText = ConfigurationState.Instance.Graphics.GraphicsBackend.Value switch
            {
                GraphicsBackend.Vulkan => "Vulkan",
                GraphicsBackend.OpenGl => "OpenGL",
                _ => throw new NotImplementedException()
            };

            _viewModel.GpuNameText = $"GPU: {_renderer.GetHardwareInfo().GpuDriver}";
        }

        private static readonly bool _fpsLogEnabled = System.Environment.GetEnvironmentVariable("RYUJINX_NEO_FPS_LOG") == "1";
        private long _fpsLogLast;

        public void UpdateStatus()
        {
            // Run a status update only when a frame is to be drawn. This prevents from updating the ui and wasting a render when no frame is queued.
            bool effectiveDockedMode = Device?.System.State.DockedMode ?? ConfigurationState.Instance.System.EnableDockedMode.Value;
            string dockedMode = effectiveDockedMode ? LocaleManager.Instance[LocaleKeys.Docked] : LocaleManager.Instance[LocaleKeys.Handheld];
            string vSyncMode = Device.VSyncMode.ToString();

            UpdateShaderCount();
            
            if (GraphicsConfig.ResScale != 1)
            {
                dockedMode += $" ({GraphicsConfig.ResScale}x)";
            }

            // NEO-FPSLOG (diag): RYUJINX_NEO_FPS_LOG=1 logs game FPS / frame time / FIFO once per second.
            if (_fpsLogEnabled && System.Diagnostics.Stopwatch.GetElapsedTime(_fpsLogLast).TotalSeconds >= 1)
            {
                _fpsLogLast = System.Diagnostics.Stopwatch.GetTimestamp();
                Logger.Info?.Print(LogClass.Application,
                    $"[NEO-FPS] fps={Device.Statistics.GetGameFrameRate():F2} ms={Device.Statistics.GetGameFrameTime():F2} fifo={Device.Statistics.GetFifoPercent():F1} maxms={Device.Statistics.GetGameMaxFrameTime():F1} {Ryujinx.Common.Diagnostics.NeoHitchStats.SnapshotAndReset()}");
            }

            StatusUpdatedEvent?.Invoke(this, new StatusUpdatedEventArgs(
                vSyncMode,
                LocaleManager.Instance[LocaleKeys.VolumeShort] + $": {(int)(Device.GetVolume() * 100)}%",
                dockedMode,
                ConfigurationState.Instance.Graphics.AspectRatio.Value.ToText(),
                LocaleManager.Instance[LocaleKeys.Game] + $": {Device.Statistics.GetGameFrameRate():00.00} FPS ({Device.Statistics.GetGameFrameTime():00.00} ms)",
                $"FIFO: {Device.Statistics.GetFifoPercent():00.00} %",
                _displayCount));
        }

        public async Task ShowExitPrompt()
        {
            bool shouldExit = !ConfigurationState.Instance.ShowConfirmExit;
            if (!shouldExit)
            {
                if (_dialogShown)
                {
                    return;
                }

                _dialogShown = true;

                // The hard-coded hotkey mapped to exit is Escape, but it's also the same key
                // that causes the dialog we launch to close (without doing anything). In release
                // mode, a race is observed that between ShowExitPrompt() appearing on KeyDown
                // and the ContentDialog we create seeing the key state before KeyUp. Merely waiting
                // for the key to no longer be pressed appears to be insufficient.
                // NB: Using _keyboardInterface.IsPressed(Key.Escape) does not currently work.
                if (OperatingSystem.IsWindows())
                {
                    while (GetAsyncKeyState(0x1B) != 0)
                    {
                        await Task.Delay(100);
                    }
                }
                else
                {
                    await Task.Delay(250);
                }

                shouldExit = await ContentDialogHelper.CreateStopEmulationDialog();

                _dialogShown = false;
            }

            if (shouldExit)
            {
                Stop();
            }
        }

        private void UpdateShaderCount()
        {
            // If there is a mismatch between total program compile and previous count
            // this means new shaders have been compiled and should be displayed.
            if (_renderer.ProgramCount != _previousCount)
            {
                _displayCount += _renderer.ProgramCount - _previousCount;
                _lastShaderReset = DateTime.Now;
                _previousCount = _renderer.ProgramCount;
            }
            // Check if 5s has passed since any new shaders were compiled.
            // If yes, reset the counter.
            else if (_lastShaderReset.AddSeconds(5) <= DateTime.Now)
            {
                _displayCount = 0;
            }
        }

        private bool UpdateFrame()
        {
            if (!_isActive)
            {
                return false;
            }

            NpadManager.Update(ConfigurationState.Instance.Graphics.AspectRatio.Value.ToFloat());

            if (_viewModel.IsActive)
            {
                bool isCursorVisible = true;

                if (_isCursorInRenderer && !_viewModel.ShowLoadProgress)
                {
                    if (ConfigurationState.Instance.Hid.EnableMouse.Value)
                    {
                        isCursorVisible = ConfigurationState.Instance.HideCursor.Value == HideCursorMode.Never;
                    }
                    else
                    {
                        isCursorVisible = ConfigurationState.Instance.HideCursor.Value == HideCursorMode.Never ||
                            (ConfigurationState.Instance.HideCursor.Value == HideCursorMode.OnIdle &&
                            Stopwatch.GetTimestamp() - _lastCursorMoveTime < CursorHideIdleTime * Stopwatch.Frequency);
                    }
                }

                if (_cursorState != (isCursorVisible ? CursorStates.CursorIsVisible : CursorStates.CursorIsHidden))
                {
                    if (isCursorVisible)
                    {
                        ShowCursor();
                    }
                    else
                    {
                        HideCursor();
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (_keyboardInterface.GetKeyboardStateSnapshot().IsPressed(Key.Delete) && _viewModel.WindowState != WindowState.FullScreen)
                    {
                        Device.Processes.ActiveApplication.DiskCacheLoadState?.Cancel();
                    }
                });

                KeyboardHotkeyState currentHotkeyState = GetHotkeyState();

                if (currentHotkeyState != _prevHotkeyState)
                {
                    switch (currentHotkeyState)
                    {
                        case KeyboardHotkeyState.ToggleVSyncMode:
                            VSyncModeToggle();
                            break;
                        case KeyboardHotkeyState.CustomVSyncIntervalDecrement:
                            Device.DecrementCustomVSyncInterval();
                            _viewModel.CustomVSyncInterval -= 1;
                            break;
                        case KeyboardHotkeyState.CustomVSyncIntervalIncrement:
                            Device.IncrementCustomVSyncInterval();
                            _viewModel.CustomVSyncInterval += 1;
                            break;
                        case KeyboardHotkeyState.Screenshot:
                            ScreenshotRequested = true;
                            break;
                        case KeyboardHotkeyState.ShowUI:
                            _viewModel.ShowMenuAndStatusBar = !_viewModel.ShowMenuAndStatusBar;
                            break;
                        case KeyboardHotkeyState.Pause:
                            if (_viewModel.IsPaused)
                            {
                                Resume();
                            }
                            else
                            {
                                Pause();
                            }
                            break;
                        case KeyboardHotkeyState.ToggleMute:
                            if (Device.IsAudioMuted())
                            {
                                Device.SetVolume(_viewModel.VolumeBeforeMute);
                            }
                            else
                            {
                                _viewModel.VolumeBeforeMute = Device.GetVolume();
                                Device.SetVolume(0);
                            }

                            _viewModel.Volume = Device.GetVolume();
                            break;
                        case KeyboardHotkeyState.ResScaleUp:
                            GraphicsConfig.ResScale = GraphicsConfig.ResScale % MaxResolutionScale + 1;
                            break;
                        case KeyboardHotkeyState.ResScaleDown:
                            GraphicsConfig.ResScale =
                            (MaxResolutionScale + GraphicsConfig.ResScale - 2) % MaxResolutionScale + 1;
                            break;
                        case KeyboardHotkeyState.VolumeUp:
                            _newVolume = MathF.Round((Device.GetVolume() + VolumeDelta), 2);
                            Device.SetVolume(_newVolume);

                            _viewModel.Volume = Device.GetVolume();
                            break;
                        case KeyboardHotkeyState.VolumeDown:
                            _newVolume = MathF.Round((Device.GetVolume() - VolumeDelta), 2);
                            Device.SetVolume(_newVolume);

                            _viewModel.Volume = Device.GetVolume();
                            break;
                        case KeyboardHotkeyState.None:
                            (_keyboardInterface as AvaloniaKeyboard)?.Clear();
                            break;
                    }
                }

                _prevHotkeyState = currentHotkeyState;

                if (ScreenshotRequested)
                {
                    ScreenshotRequested = false;
                    _renderer.Screenshot();
                }
            }

            // Touchscreen.
            bool hasTouch = false;

            if (_viewModel.IsActive && !ConfigurationState.Instance.Hid.EnableMouse.Value)
            {
                hasTouch = TouchScreenManager.Update(true, ((AvaloniaMouseDriver)_inputManager.MouseDriver).IsButtonPressed(MouseButton.Button1), ConfigurationState.Instance.Graphics.AspectRatio.Value.ToFloat());
            }

            if (!hasTouch)
            {
                Device.Hid.Touchscreen.Update();
            }

            Device.Hid.DebugPad.Update();

            return true;
        }

        private KeyboardHotkeyState GetHotkeyState()
        {
            KeyboardHotkeyState state = KeyboardHotkeyState.None;

            if (_keyboardInterface.IsPressed((Key)ConfigurationState.Instance.Hid.Hotkeys.Value.ToggleVSyncMode))
            {
                state = KeyboardHotkeyState.ToggleVSyncMode;
            }
            else if (_keyboardInterface.IsPressed((Key)ConfigurationState.Instance.Hid.Hotkeys.Value.Screenshot))
            {
                state = KeyboardHotkeyState.Screenshot;
            }
            else if (_keyboardInterface.IsPressed((Key)ConfigurationState.Instance.Hid.Hotkeys.Value.ShowUI))
            {
                state = KeyboardHotkeyState.ShowUI;
            }
            else if (_keyboardInterface.IsPressed((Key)ConfigurationState.Instance.Hid.Hotkeys.Value.Pause))
            {
                state = KeyboardHotkeyState.Pause;
            }
            else if (_keyboardInterface.IsPressed((Key)ConfigurationState.Instance.Hid.Hotkeys.Value.ToggleMute))
            {
                state = KeyboardHotkeyState.ToggleMute;
            }
            else if (_keyboardInterface.IsPressed((Key)ConfigurationState.Instance.Hid.Hotkeys.Value.ResScaleUp))
            {
                state = KeyboardHotkeyState.ResScaleUp;
            }
            else if (_keyboardInterface.IsPressed((Key)ConfigurationState.Instance.Hid.Hotkeys.Value.ResScaleDown))
            {
                state = KeyboardHotkeyState.ResScaleDown;
            }
            else if (_keyboardInterface.IsPressed((Key)ConfigurationState.Instance.Hid.Hotkeys.Value.VolumeUp))
            {
                state = KeyboardHotkeyState.VolumeUp;
            }
            else if (_keyboardInterface.IsPressed((Key)ConfigurationState.Instance.Hid.Hotkeys.Value.VolumeDown))
            {
                state = KeyboardHotkeyState.VolumeDown;
            }
            else if (_keyboardInterface.IsPressed((Key)ConfigurationState.Instance.Hid.Hotkeys.Value.CustomVSyncIntervalIncrement))
            {
                state = KeyboardHotkeyState.CustomVSyncIntervalIncrement;
            }
            else if (_keyboardInterface.IsPressed((Key)ConfigurationState.Instance.Hid.Hotkeys.Value.CustomVSyncIntervalDecrement))
            {
                state = KeyboardHotkeyState.CustomVSyncIntervalDecrement;
            }

            return state;
        }
    }
}
