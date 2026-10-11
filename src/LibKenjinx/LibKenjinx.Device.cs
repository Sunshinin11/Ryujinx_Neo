using LibHac.Ncm;
using LibHac.Tools.FsSystem.NcaUtils;
using Microsoft.Win32.SafeHandles;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.HLE;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS.SystemState;
using System;
using System.IO;
using System.Threading;

namespace LibKenjinx
{
    public static partial class LibKenjinx
    {
        public static bool InitializeDevice(MemoryManagerMode memoryManagerMode,
                                            bool useHypervisor,
                                            MemoryConfiguration memoryConfiguration,
                                            ulong memorySlack,
                                            SystemLanguage systemLanguage,
                                            RegionCode regionCode,
                                            VSyncMode vSyncMode,
                                            bool enableDockedMode,
                                            bool enablePtc,
                                            bool enableLowPowerPtc,
                                            bool enableJitCacheEviction,
                                            bool enableInternetAccess,
                                            bool enableFsIntegrityChecks,
                                            int fsGlobalAccessLogMode,
                                            string? timeZone,
                                            bool ignoreMissingServices)
        {
            if (SwitchDevice == null)
            {
                return false;
            }

            return SwitchDevice.InitializeContext(memoryManagerMode,
                                                  useHypervisor,
                                                  memoryConfiguration,
                                                  memorySlack,
                                                  systemLanguage,
                                                  regionCode,
                                                  vSyncMode,
                                                  enableDockedMode,
                                                  enablePtc,
                                                  enableLowPowerPtc,
                                                  enableJitCacheEviction,
                                                  enableInternetAccess,
                                                  enableFsIntegrityChecks,
                                                  fsGlobalAccessLogMode,
                                                  timeZone,
                                                  ignoreMissingServices);
        }

        public static void InstallFirmware(Stream stream, bool isXci)
        {
            SwitchDevice?.ContentManager?.InstallFirmware(stream, isXci);
        }

        public static string GetInstalledFirmwareVersion()
        {
            SystemVersion? version = SwitchDevice?.ContentManager?.GetCurrentFirmwareVersion();

            if (version != null)
            {
                return version.VersionString;
            }

            return String.Empty;
        }

        public static SystemVersion? VerifyFirmware(Stream stream, bool isXci)
        {
            return SwitchDevice?.ContentManager?.VerifyFirmwarePackage(stream, isXci) ?? null;
        }

        public static bool LoadApplication(Stream stream, FileType type, Stream? updateStream = null)
        {
            Switch? emulationContext = SwitchDevice?.EmulationContext;
            
            return type switch
            {
                FileType.None => false,
                FileType.Nsp => emulationContext?.LoadNsp(stream, 0, updateStream) ?? false,
                FileType.Xci => emulationContext?.LoadXci(stream, 0, updateStream) ?? false,
                FileType.Nro => emulationContext?.LoadProgram(stream, true, "") ?? false,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };
        }

        public static bool LaunchMiiEditApplet()
        {
            string? contentPath = SwitchDevice?.ContentManager.GetInstalledContentPath(0x0100000000001009, StorageId.BuiltInSystem, NcaContentType.Program);

            return LoadApplication(contentPath);
        }

        public static bool LoadApplication(string? path)
        {
            Switch? emulationContext = SwitchDevice?.EmulationContext;

            if (Directory.Exists(path))
            {
                string[] romFsFiles = Directory.GetFiles(path, "*.istorage");

                if (romFsFiles.Length == 0)
                {
                    romFsFiles = Directory.GetFiles(path, "*.romfs");
                }

                if (romFsFiles.Length > 0)
                {
                    Logger.Info?.Print(LogClass.Application, "Loading as cart with RomFS.");

                    if (emulationContext != null && !emulationContext.LoadCart(path, romFsFiles[0]))
                    {
                        SwitchDevice?.DisposeContext();
                        return false;
                    }
                }
                else
                {
                    Logger.Info?.Print(LogClass.Application, "Loading as cart WITHOUT RomFS.");

                    if (emulationContext != null && !emulationContext.LoadCart(path))
                    {
                        SwitchDevice?.DisposeContext();
                        return false;
                    }
                }
            }
            else if (File.Exists(path))
            {
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".xci":
                        Logger.Info?.Print(LogClass.Application, "Loading as XCI.");

                        if (emulationContext != null && !emulationContext.LoadXci(path))
                        {
                            SwitchDevice?.DisposeContext();
                            return false;
                        }
                        break;
                    case ".nca":
                        Logger.Info?.Print(LogClass.Application, "Loading as NCA.");

                        if (emulationContext != null && !emulationContext.LoadNca(path))
                        {
                            SwitchDevice?.DisposeContext();
                            return false;
                        }
                        break;
                    case ".nsp":
                    case ".pfs0":
                        Logger.Info?.Print(LogClass.Application, "Loading as NSP.");

                        if (emulationContext != null && !emulationContext.LoadNsp(path))
                        {
                            SwitchDevice?.DisposeContext();
                            return false;
                        }
                        break;
                    default:
                        Logger.Info?.Print(LogClass.Application, "Loading as Homebrew.");
                        try
                        {
                            if (emulationContext != null && !emulationContext.LoadProgram(path))
                            {
                                SwitchDevice?.DisposeContext();
                                return false;
                            }
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            Logger.Error?.Print(LogClass.Application, "The specified file is not supported by Ryujinx.");
                            SwitchDevice?.DisposeContext();
                            return false;
                        }
                        break;
                }
            }
            else
            {
                Logger.Warning?.Print(LogClass.Application, $"Couldn't load '{path}'. Please specify a valid XCI/NCA/NSP/PFS0/NRO file.");
                SwitchDevice?.DisposeContext();
                return false;
            }

            return true;
        }

        public static void SignalEmulationClose()
        {
            _isStopped = true;
            _isActive = false;
        }

        public static void CloseEmulation()
        {
            if (SwitchDevice == null)
                return;

            Logger.Info?.Print(LogClass.Application, "Closing emulation");

            _isStopped = true;
            _isActive = false;

            _npadManager?.Dispose();
            _npadManager = null;

            _touchScreenManager?.Dispose();
            _touchScreenManager = null;

            _gpuDoneEvent?.WaitOne(3000);
            _gpuDoneEvent?.Dispose();
            _gpuDoneEvent = null;

            _gpuCancellationTokenSource?.Cancel();
            _gpuCancellationTokenSource?.Dispose();
            _gpuCancellationTokenSource = null;

            SwitchDevice.Dispose();
            SwitchDevice = null;

            Renderer = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(2000);
            GC.Collect();
            Logger.Info?.Print(LogClass.Application, "Emulation closed");
        }

        public static void ReinitEmulation()
        {
            if (SwitchDevice == null)
            {
                Logger.Info?.Print(LogClass.Application, "Resetting device");
                if (AndroidFileSystem != null)
                {
                    SwitchDevice = new SwitchDevice(AndroidFileSystem);
                }
            }

            _isStopped = false;
            _isActive = false;
        }

        private static FileStream OpenFile(int descriptor)
        {
            SafeFileHandle safeHandle = new(descriptor, false);

            return new FileStream(safeHandle, FileAccess.ReadWrite);
        }

        public enum FileType
        {
            None,
            Nsp,
            Xci,
            Nro
        }
    }
}
