using Ryujinx.Common.Diagnostics;
using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    // Always enabled in the functional APK. No per-submit allocation, ring buffer,
    // file IO or extra Vulkan query. Timelines from different queues are not merged.
    internal sealed class SubmissionTelemetry
    {
        private readonly string _name;
        private long _calls, _items, _submitTicks, _waits, _waitTicks, _timeouts, _cached;
        private long _presents, _presentTicks, _lastPresent, _frameTicks, _frames, _slowFrames;
        private long _maxBatch, _maxWait, _maxFrame, _lastLog = Stopwatch.GetTimestamp();
        private ulong _submitted, _completed;

        internal SubmissionTelemetry(string name) => _name = name;

        private static void Max(ref long field, long value)
        {
            long old = Volatile.Read(ref field);
            while (value > old)
            {
                long observed = Interlocked.CompareExchange(ref field, value, old);
                if (observed == old) break;
                old = observed;
            }
        }

        internal void Submitted(int count, long ticks, ulong value)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _items, count);
            Interlocked.Add(ref _submitTicks, ticks);
            Max(ref _maxBatch, count);
            Volatile.Write(ref _submitted, value);
            Log();
        }

        internal void Waited(long ticks, bool timeout)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            Interlocked.Increment(ref _waits);
            Interlocked.Add(ref _waitTicks, ticks);
            if (timeout) Interlocked.Increment(ref _timeouts);
            Max(ref _maxWait, ticks);
            Log();
        }

        internal void CachedWait() => Interlocked.Increment(ref _cached);
        internal void Completed(ulong value) => Volatile.Write(ref _completed, value);

        internal void Presented(long ticks)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            Interlocked.Increment(ref _presents);
            Interlocked.Add(ref _presentTicks, ticks);
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(ref _lastPresent, now);
            if (previous != 0)
            {
                long delta = now - previous;
                Interlocked.Add(ref _frameTicks, delta);
                Interlocked.Increment(ref _frames);
                if (delta > Stopwatch.Frequency / 20) Interlocked.Increment(ref _slowFrames);
                Max(ref _maxFrame, delta);
            }
            Log();
        }

        private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        private void Log()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            long now = Stopwatch.GetTimestamp();
            long last = Volatile.Read(ref _lastLog);
            if (now - last < Stopwatch.Frequency * 5 || Interlocked.CompareExchange(ref _lastLog, now, last) != last) return;
            long frames = Interlocked.Exchange(ref _frames, 0);
            long frameTicks = Interlocked.Exchange(ref _frameTicks, 0);
            long rejectedStages = 0;
            long singleFlightOwner = 0;
            long singleFlightJoined = 0;
            int compileInFlight = 0;
            Logger.Info?.Print(LogClass.Gpu,
                $"[NEO-A17] queue={_name} windowMs={Milliseconds(now-last):F0} " +
                $"submitCalls={Interlocked.Exchange(ref _calls,0)} submitItems={Interlocked.Exchange(ref _items,0)} batchMax={Interlocked.Exchange(ref _maxBatch,0)} submitMs={Milliseconds(Interlocked.Exchange(ref _submitTicks,0)):F2} " +
                $"waitCalls={Interlocked.Exchange(ref _waits,0)} waitCached={Interlocked.Exchange(ref _cached,0)} waitMs={Milliseconds(Interlocked.Exchange(ref _waitTicks,0)):F2} waitMaxMs={Milliseconds(Interlocked.Exchange(ref _maxWait,0)):F2} timeouts={Interlocked.Exchange(ref _timeouts,0)} " +
                $"submitted={Volatile.Read(ref _submitted)} observedCompleted={Volatile.Read(ref _completed)} " +
                $"presentOk={Interlocked.Exchange(ref _presents,0)} presentMs={Milliseconds(Interlocked.Exchange(ref _presentTicks,0)):F2} presentIntervalAvgMs={(frames == 0 ? 0 : Milliseconds(frameTicks)/frames):F2} presentIntervalMaxMs={Milliseconds(Interlocked.Exchange(ref _maxFrame,0)):F2} intervalsOver50ms={Interlocked.Exchange(ref _slowFrames,0)} " +
                $"pipelineCreateRejectedNullStages={rejectedStages} " +
                $"graphicsPipelineSingleFlightOwner={singleFlightOwner} " +
                $"graphicsPipelineSingleFlightJoined={singleFlightJoined} " +
                $"graphicsPipelineCompileInFlight={compileInFlight}");
        }
    }
}
