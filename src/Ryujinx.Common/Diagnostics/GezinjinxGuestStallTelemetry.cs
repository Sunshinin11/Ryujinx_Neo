using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Common.Diagnostics
{
    public static class GezinjinxGuestStallTelemetry
    {
        private const int RingSize = 64;
        private const double MinimumLowProgressSeconds = 8;
        private const double MinimumHealthyPipelineRate = 2;

        private enum EventKind : int
        {
            Svc = 1,
            ThreadState = 2,
            Map = 3,
            Unmap = 4,
            GpfifoEnqueue = 5,
            GpfifoDequeue = 6,
            VkSubmit = 7,
            Present = 8,
        }

        private sealed class EventRing
        {
            private readonly long[] _timestamps = new long[RingSize];
            private readonly int[] _kinds = new int[RingSize];
            private readonly ulong[] _threadIds = new ulong[RingSize];
            private readonly ulong[] _pcs = new ulong[RingSize];
            private readonly ulong[] _arg0 = new ulong[RingSize];
            private readonly ulong[] _arg1 = new ulong[RingSize];
            private long _sequence;

            public void Record(EventKind kind, ulong threadId, ulong pc, ulong arg0, ulong arg1)
            {
                long sequence = Interlocked.Increment(ref _sequence);
                int index = (int)((sequence - 1) & (RingSize - 1));

                Volatile.Write(ref _timestamps[index], 0);
                _kinds[index] = (int)kind;
                _threadIds[index] = threadId;
                _pcs[index] = pc;
                _arg0[index] = arg0;
                _arg1[index] = arg1;
                Volatile.Write(ref _timestamps[index], Stopwatch.GetTimestamp());
            }

            public void Dump(string lane, long now)
            {
                long end = Volatile.Read(ref _sequence);
                long start = Math.Max(1, end - RingSize + 1);

                for (long sequence = start; sequence <= end; sequence++)
                {
                    int index = (int)((sequence - 1) & (RingSize - 1));
                    long timestamp = Volatile.Read(ref _timestamps[index]);
                    if (timestamp == 0)
                    {
                        continue;
                    }

                    EventKind kind = (EventKind)_kinds[index];
                    double ageMs = Math.Max(0, now - timestamp) * 1000.0 / Stopwatch.Frequency;
                    Logger.Info?.Print(LogClass.Gpu,
                        $"[RYUJINX-NEO-GUEST-STALL] lane={lane} seq={sequence} age={ageMs:F1}ms kind={kind} tid={_threadIds[index]} pc=0x{_pcs[index]:X16} arg0=0x{_arg0[index]:X16} arg1=0x{_arg1[index]:X16}");
                }
            }
        }

        private static readonly EventRing _guestRing = new();
        private static readonly EventRing _memoryRing = new();
        private static readonly EventRing _pipelineRing = new();

        private static long _pipelineSequence;
        private static long _lastCheckedPipelineSequence;
        private static long _lastGuestTicks;
        private static long _lastPipelineTicks;
        private static long _lowProgressTicks;
        private static long _lastDumpPipelineSequence = -1;
        private static int _armed;
        private static int _enabled;
        private static Timer _watchdog;
        private static Func<string> _rendererSnapshotProvider;

        private static readonly object Sync = new();
        private static long _lastSubmit;
        private static long _lastPresent;
        private static ulong _submitTimeline;
        private static ulong _submitSequence;
        private static ulong _presentSequence;

        public static void Enable()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry)
            {
                return;
            }

            if (Interlocked.Exchange(ref _enabled, 1) != 0)
            {
                return;
            }

            long intervalTicks = 5 * Stopwatch.Frequency;
            _watchdog = new Timer(_ => CheckForStall(intervalTicks), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            Logger.Notice.Print(LogClass.Gpu, "[RYUJINX-NEO-GUEST-STALL] watchdog enabled interval=5s threshold=8s");
        }

        public static void SetRendererSnapshotProvider(Func<string> provider)
        {
            Volatile.Write(ref _rendererSnapshotProvider, provider);
        }

        [Conditional("RYUJINX_GPFIFO_TELEMETRY")]
        public static void RecordSvc(ulong threadId, ulong pc, int id)
        {
            if (Volatile.Read(ref _enabled) == 0) return;
            Volatile.Write(ref _armed, 1);
            Volatile.Write(ref _lastGuestTicks, Stopwatch.GetTimestamp());
            _guestRing.Record(EventKind.Svc, threadId, pc, (uint)id, 0);
        }

        [Conditional("RYUJINX_GPFIFO_TELEMETRY")]
        public static void RecordThreadState(ulong threadId, ulong pc, int state, bool waitingSync, bool waitingArbitration, int core)
        {
            if (Volatile.Read(ref _enabled) == 0) return;
            Volatile.Write(ref _armed, 1);
            Volatile.Write(ref _lastGuestTicks, Stopwatch.GetTimestamp());
            ulong waitFlags = (waitingSync ? 1UL : 0UL) | (waitingArbitration ? 2UL : 0UL);
            _guestRing.Record(EventKind.ThreadState, threadId, pc, (uint)state, waitFlags | ((ulong)(uint)core << 32));
        }

        [Conditional("RYUJINX_GPFIFO_TELEMETRY")]
        public static void RecordMap(ulong va, ulong pa, ulong size)
        {
            if (Volatile.Read(ref _enabled) != 0) _memoryRing.Record(EventKind.Map, 0, va, pa, size);
        }

        [Conditional("RYUJINX_GPFIFO_TELEMETRY")]
        public static void RecordUnmap(ulong va, ulong size)
        {
            if (Volatile.Read(ref _enabled) != 0) _memoryRing.Record(EventKind.Unmap, 0, va, size, 0);
        }

        [Conditional("RYUJINX_GPFIFO_TELEMETRY")]
        public static void RecordGpfifoEnqueue(ulong address, int words) => RecordPipeline(EventKind.GpfifoEnqueue, address, (uint)words);

        [Conditional("RYUJINX_GPFIFO_TELEMETRY")]
        public static void RecordGpfifoDequeue(ulong address, int words) => RecordPipeline(EventKind.GpfifoDequeue, address, (uint)words);

        public static void RecordVkSubmit(ulong timeline, ulong sequence)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            RecordPipeline(EventKind.VkSubmit, timeline, sequence);
            lock (Sync)
            {
                _lastSubmit = Stopwatch.GetTimestamp();
                _submitTimeline = timeline;
                _submitSequence = sequence;
            }
        }

        public static void RecordPresent(ulong sequence)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            RecordPipeline(EventKind.Present, sequence, 0);
            lock (Sync)
            {
                _lastPresent = Stopwatch.GetTimestamp();
                _presentSequence = sequence;
            }
        }

        public static void LogProgress()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            string message;
            lock (Sync)
            {
                long now = Stopwatch.GetTimestamp();
                double submitAge = _lastSubmit == 0 ? -1 : (now - _lastSubmit) * 1000.0 / Stopwatch.Frequency;
                double presentAge = _lastPresent == 0 ? -1 : (now - _lastPresent) * 1000.0 / Stopwatch.Frequency;
                message = FormattableString.Invariant(
                    $"[NEOV2-HOST-PROGRESS] submitTimeline={_submitTimeline} submitSequence={_submitSequence} presentSequence={_presentSequence} submitAgeMs={submitAge:F2} presentAgeMs={presentAge:F2}");
            }
            Logger.Info?.Print(LogClass.Gpu, message);
        }

        private static void RecordPipeline(EventKind kind, ulong arg0, ulong arg1)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry || Volatile.Read(ref _enabled) == 0) return;
            Volatile.Write(ref _lastPipelineTicks, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _pipelineSequence);
            Volatile.Write(ref _armed, 1);
            _pipelineRing.Record(kind, 0, 0, arg0, arg1);
        }

        public static void CheckForStall(long windowTicks)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry || Volatile.Read(ref _enabled) == 0 || Volatile.Read(ref _armed) == 0 || windowTicks <= 0)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            long pipelineSequence = Volatile.Read(ref _pipelineSequence);
            long previousSequence = Interlocked.Exchange(ref _lastCheckedPipelineSequence, pipelineSequence);
            long progress = Math.Max(0, pipelineSequence - previousSequence);
            double windowSeconds = windowTicks / (double)Stopwatch.Frequency;
            double progressRate = progress / Math.Max(0.001, windowSeconds);

            if (progressRate >= MinimumHealthyPipelineRate)
            {
                Volatile.Write(ref _lowProgressTicks, 0);
                return;
            }

            long lowProgressTicks = Interlocked.Add(ref _lowProgressTicks, windowTicks);
            if (lowProgressTicks < MinimumLowProgressSeconds * Stopwatch.Frequency ||
                pipelineSequence == Volatile.Read(ref _lastDumpPipelineSequence))
            {
                return;
            }

            Volatile.Write(ref _lastDumpPipelineSequence, pipelineSequence);
            long lastGuest = Volatile.Read(ref _lastGuestTicks);
            long lastPipeline = Volatile.Read(ref _lastPipelineTicks);
            double guestAgeMs = lastGuest == 0 ? -1 : Math.Max(0, now - lastGuest) * 1000.0 / Stopwatch.Frequency;
            double pipelineAgeMs = lastPipeline == 0 ? -1 : Math.Max(0, now - lastPipeline) * 1000.0 / Stopwatch.Frequency;

            Logger.Warning?.Print(LogClass.Gpu,
                $"[RYUJINX-NEO-GUEST-STALL] snapshot reason=low-pipeline-progress window={windowSeconds:F2}s lowFor={lowProgressTicks / (double)Stopwatch.Frequency:F2}s events={progress} rate={progressRate:F2}/s guestAge={guestAgeMs:F1}ms pipelineAge={pipelineAgeMs:F1}ms pipelineSeq={pipelineSequence}");

            Func<string> rendererSnapshotProvider = Volatile.Read(ref _rendererSnapshotProvider);
            if (rendererSnapshotProvider != null)
            {
                try
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[RYUJINX-NEO-GUEST-STALL] renderer {rendererSnapshotProvider()}");
                }
                catch (Exception exception)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[RYUJINX-NEO-GUEST-STALL] renderer snapshot failed: {exception.GetType().Name}: {exception.Message}");
                }
            }

            _guestRing.Dump("guest", now);
            _memoryRing.Dump("memory", now);
            _pipelineRing.Dump("pipeline", now);
        }
    }
}
