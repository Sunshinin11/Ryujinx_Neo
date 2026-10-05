using Ryujinx.Common.Diagnostics;
using Ryujinx.Common.Logging;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    internal static class TimelineWaitCoalescingTelemetry
    {
        private static readonly long SampleIntervalTicks = Stopwatch.Frequency * 5;
        private static long _nextSampleTimestamp = Stopwatch.GetTimestamp() + SampleIntervalTicks;
        private static long _requested;
        private static long _actual;
        private static long _pending;
        private static long _knownComplete;

        public static void Record(int requested, int actual)
        {
            Record(requested, actual, 0, 0);
        }

        public static void Record(int requested, int actual, int pending)
        {
            Record(requested, actual, pending, 0);
        }

        public static void Record(int requested, int actual, int pending, int knownComplete)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry || requested <= 0) return;

            if (requested <= 0)
            {
                return;
            }

            Interlocked.Add(ref _requested, requested);
            Interlocked.Add(ref _actual, actual);
            Interlocked.Add(ref _pending, pending);
            Interlocked.Add(ref _knownComplete, knownComplete);
            TryLog();
        }

        private static void TryLog()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            long now = Stopwatch.GetTimestamp();
            long next = Volatile.Read(ref _nextSampleTimestamp);

            if (now < next ||
                Interlocked.CompareExchange(ref _nextSampleTimestamp, now + SampleIntervalTicks, next) != next)
            {
                return;
            }

            long requested = Interlocked.Exchange(ref _requested, 0);
            long actual = Interlocked.Exchange(ref _actual, 0);
            long pending = Interlocked.Exchange(ref _pending, 0);
            long knownComplete = Interlocked.Exchange(ref _knownComplete, 0);
            long saved = requested - actual;
            long coalesced = saved - knownComplete;

            Logger.Info?.Print(
                LogClass.Gpu,
                $"[GEZINJINX-V0.0.9-R6] Timeline wait coalescing: requested={requested} actual={actual} saved={saved} coalesced={coalesced} knownComplete={knownComplete} pending={pending}");
        }
    }
}
