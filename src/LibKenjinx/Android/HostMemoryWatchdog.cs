using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using System;
using System.IO;
using System.Threading;

namespace LibKenjinx.Android
{
    // NEOV2-GPUMEM1: advisory RAM pressure, followed by the existing game-exit
    // path. This cannot guarantee recovery from a blocked GPU driver or kernel.
    internal static class HostMemoryWatchdog
    {
        private const long MiB = 1024L * 1024;
        private const long LowAvailableBytes = 1200 * MiB;
        private const long CriticalAvailableBytes = 700 * MiB;
        private const int IntervalMs = 250;
        private const int CriticalSamplesToExit = 3;
        private static readonly object Gate = new();
        private static Session? _session;

        private sealed class Session
        {
            internal Timer? Timer;
            internal int Running;
            internal int CriticalSamples;
            internal bool ExitRequested;
        }

        public static void Start()
        {
            lock (Gate)
            {
                Stop();
                if (!GraphicsConfig.EdenGpuMemory) return;

                Session session = new();
                _session = session;
                session.Timer = new Timer(Tick, session, 1000, IntervalMs);
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                Session? session = _session;
                _session = null;
                session?.Timer?.Dispose();
                GpuMemoryStats.HostPressureLevel = 0;
            }
        }

        private static void Tick(object? state)
        {
            if (state is not Session session || Interlocked.Exchange(ref session.Running, 1) != 0) return;
            try
            {
                // File I/O happens outside the session gate. A stopped session cannot
                // publish pressure or request exit after the next game starts.
                long available = ReadMemAvailable();
                lock (Gate)
                {
                    if (!ReferenceEquals(_session, session) || available <= 0) return;
                    int level = available < CriticalAvailableBytes ? 2 : (available < LowAvailableBytes ? 1 : 0);
                    session.CriticalSamples = level == 2 ? session.CriticalSamples + 1 : 0;
                    int effective = level == 2 && session.CriticalSamples < CriticalSamplesToExit ? 1 : level;
                    if (session.ExitRequested) effective = 2;
                    GpuMemoryStats.HostPressureLevel = effective;

                    if (session.CriticalSamples >= CriticalSamplesToExit && !session.ExitRequested)
                    {
                        session.ExitRequested = true;
                        Logger.Error?.Print(LogClass.Application,
                            $"Android RAM critical: available={available / MiB}MiB. Closing the game to keep the device responsive. GPU {GpuMemoryStats.Describe()}");
                        // Signal only; never dispose the GPU/device from the timer thread.
                        global::LibKenjinx.LibKenjinx.SignalEmulationClose();
                        Interop.OnHostLowMemory();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Android RAM watchdog error: {ex.Message}");
            }
            finally { Volatile.Write(ref session.Running, 0); }
        }

        private static long ReadMemAvailable()
        {
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
                string value = line.Substring("MemAvailable:".Length).Trim();
                int space = value.IndexOf(' ');
                if (space > 0) value = value.Substring(0, space);
                return long.TryParse(value, out long kb) ? kb * 1024 : -1;
            }
            return -1;
        }
    }
}
