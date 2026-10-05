using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    /// <summary>
    /// Keeps the two hot GPU threads on different performance cores.
    /// </summary>
    static class BackendThreadAffinity
    {
        private const string CpuRoot = "/sys/devices/system/cpu";

        [DllImport("libc", SetLastError = true)]
        private static extern unsafe int sched_setaffinity(int pid, nuint cpusetsize, ulong* mask);

        public static void PinCurrentThreadToGpuCores()
        {
            PinCurrentThread(reservePrimeForGpu: false);
        }

        public static void PinCurrentThreadToBackendCores()
        {
            PinCurrentThread(reservePrimeForGpu: true);
        }

        private static unsafe void PinCurrentThread(bool reservePrimeForGpu)
        {
            if (!OperatingSystem.IsAndroid() && !OperatingSystem.IsLinux())
            {
                return;
            }

            try
            {
                List<(int Cpu, long MaxFreq)> cpus = new();

                foreach (string dir in Directory.GetDirectories(CpuRoot, "cpu*"))
                {
                    string name = Path.GetFileName(dir);

                    if (name.Length <= 3 ||
                        !int.TryParse(name.AsSpan(3), out int cpu) ||
                        cpu >= 64)
                    {
                        continue;
                    }

                    string freqPath = Path.Combine(dir, "cpufreq", "cpuinfo_max_freq");

                    if (File.Exists(freqPath) &&
                        long.TryParse(File.ReadAllText(freqPath).Trim(), out long maxFreq))
                    {
                        cpus.Add((cpu, maxFreq));
                    }
                }

                if (cpus.Count < 2)
                {
                    Logger.Info?.Print(
                        LogClass.Gpu,
                        "[NEOV2-BACKEND-PRIME1] Not applied: CPU frequency information unavailable");
                    return;
                }

                long fastest = 0;
                long slowest = long.MaxValue;

                foreach ((int _, long maxFreq) in cpus)
                {
                    fastest = Math.Max(fastest, maxFreq);
                    slowest = Math.Min(slowest, maxFreq);
                }

                if (fastest == slowest)
                {
                    Logger.Info?.Print(
                        LogClass.Gpu,
                        "[NEOV2-BACKEND-PRIME1] Not applied: all cores have the same maximum frequency");
                    return;
                }

                long selectedFrequency = fastest;
                int fastestCoreCount = 0;

                foreach ((int _, long maxFreq) in cpus)
                {
                    if (maxFreq == fastest)
                    {
                        fastestCoreCount++;
                    }
                }

                // NEOV2 GPU-AFFINITY2: on SoCs with one prime core, pinning both
                // GPU.MainThread and the Vulkan backend there makes the two hottest
                // graphics threads time-slice on one CPU. Keep the prime for the GPU
                // producer and use the next performance cluster for the backend when
                // it is close enough in throughput.
                if (reservePrimeForGpu && fastestCoreCount == 1)
                {
                    long secondFastest = 0;

                    foreach ((int _, long maxFreq) in cpus)
                    {
                        if (maxFreq < fastest)
                        {
                            secondFastest = Math.Max(secondFastest, maxFreq);
                        }
                    }

                    if (secondFastest != 0 && secondFastest * 100 >= fastest * 90)
                    {
                        selectedFrequency = secondFastest;
                    }
                }

                ulong mask = 0;
                List<int> selected = new();

                // NEOV2 GPU-AFFINITY3: the backend is a single hot thread. On a design with
                // one prime core, keep it on one core from the next performance cluster to
                // avoid scheduler migrations and preserve its CPU-cache locality.
                bool selectSingleBackendCore =
                    reservePrimeForGpu && fastestCoreCount == 1 && selectedFrequency != fastest;
                int singleBackendCpu = -1;

                if (selectSingleBackendCore)
                {
                    foreach ((int cpu, long maxFreq) in cpus)
                    {
                        if (maxFreq == selectedFrequency)
                        {
                            singleBackendCpu = Math.Max(singleBackendCpu, cpu);
                        }
                    }
                }

                foreach ((int cpu, long maxFreq) in cpus)
                {
                    if (maxFreq == selectedFrequency &&
                        (!selectSingleBackendCore || cpu == singleBackendCpu))
                    {
                        mask |= 1UL << cpu;
                        selected.Add(cpu);
                    }
                }

                selected.Sort();

                int result = sched_setaffinity(
                    0,
                    (nuint)sizeof(ulong),
                    &mask);

                if (result == 0)
                {
                    Logger.Info?.Print(
                        LogClass.Gpu,
                        $"[NEOV2-GPU-AFFINITY2] {(reservePrimeForGpu ? "Vulkan backend" : "GPU main")} thread pinned to CPU {string.Join(",", selected)} ({selectedFrequency / 1000} MHz max)");
                }
                else
                {
                    Logger.Info?.Print(
                        LogClass.Gpu,
                        $"[NEOV2-BACKEND-PRIME1] Not applied: sched_setaffinity failed (errno {Marshal.GetLastPInvokeError()})");
                }
            }
            catch (Exception ex)
            {
                Logger.Info?.Print(
                    LogClass.Gpu,
                    $"[NEOV2-BACKEND-PRIME1] Not applied: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
