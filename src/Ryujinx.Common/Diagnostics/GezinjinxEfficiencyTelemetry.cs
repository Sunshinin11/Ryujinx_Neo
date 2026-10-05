using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Common.Diagnostics
{
    public static class GezinjinxEfficiencyTelemetry
    {
        private static long _submits;
        private static long _presents;
        private static long _workerWaitTicks;
        private static long _workerWorkTicks;
        private static long _presentCallerWaitTicks;
        private static long _drainCallerWaitTicks;
        private static long _queueDepthTotal;
        private static long _queueDepthSamples;
        private static long _lastLog = Stopwatch.GetTimestamp();

        public static void RegisterCurrentThread(string name)
        {
            if (GezinjinxPerformanceMode.DetailedTelemetry)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"[NEOV2-EFFICIENCY] thread={name} managedThreadId={Environment.CurrentManagedThreadId}");
            }
        }

        public static void RecordSubmitQueueDepth(int depth)
        {
            if (GezinjinxPerformanceMode.DetailedTelemetry)
            {
                Interlocked.Add(ref _queueDepthTotal, depth);
                Interlocked.Increment(ref _queueDepthSamples);
            }
        }

        public static void RecordSubmit() => Add(ref _submits, 1);
        public static void RecordPresent() => Add(ref _presents, 1);
        public static void RecordSubmitWorkerWait(long ticks) => Add(ref _workerWaitTicks, ticks);
        public static void RecordSubmitWorkerWork(long ticks) => Add(ref _workerWorkTicks, ticks);
        public static void RecordPresentCallerWait(long ticks) => Add(ref _presentCallerWaitTicks, ticks);
        public static void RecordDrainCallerWait(long ticks) => Add(ref _drainCallerWaitTicks, ticks);

        private static void Add(ref long counter, long value)
        {
            if (GezinjinxPerformanceMode.DetailedTelemetry)
            {
                Interlocked.Add(ref counter, value);
            }
        }

        private static double TakeMilliseconds(ref long ticks) =>
            Interlocked.Exchange(ref ticks, 0) * 1000.0 / Stopwatch.Frequency;

        public static void TryLog()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            long last = Volatile.Read(ref _lastLog);
            if (now - last < Stopwatch.Frequency * 5 || Interlocked.CompareExchange(ref _lastLog, now, last) != last)
            {
                return;
            }

            long samples = Interlocked.Exchange(ref _queueDepthSamples, 0);
            long depthTotal = Interlocked.Exchange(ref _queueDepthTotal, 0);
            Logger.Info?.Print(LogClass.Gpu, FormattableString.Invariant(
                $"[NEOV2-EFFICIENCY] windowMs={(now - last) * 1000.0 / Stopwatch.Frequency:F0} submits={Interlocked.Exchange(ref _submits, 0)} presents={Interlocked.Exchange(ref _presents, 0)} queueDepthSamples={samples} queueDepthTotal={depthTotal} workerWaitMs={TakeMilliseconds(ref _workerWaitTicks):F2} workerWorkMs={TakeMilliseconds(ref _workerWorkTicks):F2} presentCallerWaitMs={TakeMilliseconds(ref _presentCallerWaitTicks):F2} drainCallerWaitMs={TakeMilliseconds(ref _drainCallerWaitTicks):F2}"));
            GezinjinxGuestStallTelemetry.LogProgress();
        }
    }
}
