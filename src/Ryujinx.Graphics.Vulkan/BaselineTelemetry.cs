using Ryujinx.Common.Diagnostics;
using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    internal static class BaselineTelemetry
    {
        private static long _submitCalls;
        private static long _submitTicks;

        private static long _waitCalls;
        private static long _waitTicks;
        private static long _waitAlreadySignaled;

        private static long _presentOk;
        private static long _presentTicks;
        private static long _presentIntervalTicks;
        private static long _presentIntervalCount;
        private static long _presentIntervalMaxTicks;
        private static long _lastPresentTimestamp;

        private static long _lastLogTimestamp = Stopwatch.GetTimestamp();
        private static int _isLogging;

        public static void RecordSubmit(long ticks)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            _submitCalls++;
            _submitTicks += ticks;
        }

        public static void TryFlush()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            long now = Stopwatch.GetTimestamp();
            if (now - _lastLogTimestamp >= Stopwatch.Frequency * 5)
            {
                if (Interlocked.CompareExchange(ref _isLogging, 1, 0) == 0)
                {
                    try
                    {
                        if (now - _lastLogTimestamp >= Stopwatch.Frequency * 5)
                        {
                            Log(now);
                        }
                    }
                    finally
                    {
                        _isLogging = 0;
                    }
                }
            }
        }

        public static void RecordWait(long ticks)
        {
            _waitCalls++;
            _waitTicks += ticks;
        }

        public static void RecordWaitAlreadySignaled()
        {
            _waitAlreadySignaled++;
        }

        public static void RecordPresent(long ticks)
        {
            _presentOk++;
            _presentTicks += ticks;

            long now = Stopwatch.GetTimestamp();
            if (_lastPresentTimestamp != 0)
            {
                long deltaTicks = now - _lastPresentTimestamp;
                _presentIntervalTicks += deltaTicks;
                _presentIntervalCount++;
                if (deltaTicks > _presentIntervalMaxTicks)
                {
                    _presentIntervalMaxTicks = deltaTicks;
                }
            }
            _lastPresentTimestamp = now;
        }

        private static void Log(long now)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            long elapsedTicks = now - _lastLogTimestamp;
            _lastLogTimestamp = now;

            long submitCalls = _submitCalls;
            long submitTicks = _submitTicks;
            long waitCalls = _waitCalls;
            long waitTicks = _waitTicks;
            long waitAlreadySignaled = _waitAlreadySignaled;
            long presentOk = _presentOk;
            long presentTicks = _presentTicks;
            long presentIntervalTicks = _presentIntervalTicks;
            long presentIntervalCount = _presentIntervalCount;
            long presentIntervalMaxTicks = _presentIntervalMaxTicks;

            _submitCalls = 0;
            _submitTicks = 0;
            _waitCalls = 0;
            _waitTicks = 0;
            _waitAlreadySignaled = 0;
            _presentOk = 0;
            _presentTicks = 0;
            _presentIntervalTicks = 0;
            _presentIntervalCount = 0;
            _presentIntervalMaxTicks = 0;

            double windowMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
            double submitMs = submitTicks * 1000.0 / Stopwatch.Frequency;
            double waitMs = waitTicks * 1000.0 / Stopwatch.Frequency;
            double presentMs = presentTicks * 1000.0 / Stopwatch.Frequency;
            double presentIntervalAvgMs = presentIntervalCount == 0 ? 0.0 : (presentIntervalTicks * 1000.0 / Stopwatch.Frequency) / presentIntervalCount;
            double presentIntervalMaxMs = presentIntervalMaxTicks * 1000.0 / Stopwatch.Frequency;

            Logger.Info?.Print(
                LogClass.Gpu,
                FormattableString.Invariant(
                    $"[NEOV2-BASELINE] windowMs={windowMs:F0} submitCalls={submitCalls} submitMs={submitMs:F2} waitCalls={waitCalls} waitMs={waitMs:F2} waitAlreadySignaled={waitAlreadySignaled} presentOk={presentOk} presentMs={presentMs:F2} presentIntervalAvgMs={presentIntervalAvgMs:F2} presentIntervalMaxMs={presentIntervalMaxMs:F2}"));
        }
    }
}