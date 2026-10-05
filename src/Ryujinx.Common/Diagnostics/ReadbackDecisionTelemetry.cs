using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Common.Diagnostics
{
    public static class ReadbackDecisionTelemetry
    {
        private sealed class LatencyHistogram
        {
            private static readonly double[] UpperMs =
            {
                0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 4, 8, 16, 33, 66, 100, 250, 500, 1000,
            };

            private readonly long[] _buckets = new long[UpperMs.Length];
            private long _count;
            private long _ticks;
            private long _maxTicks;
            private long _maxContext;

            public void Record(long ticks, long context = 0)
            {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
                ticks = Math.Max(0, ticks);
                Interlocked.Increment(ref _count);
                Interlocked.Add(ref _ticks, ticks);

                long current = Volatile.Read(ref _maxTicks);
                while (ticks > current)
                {
                    long observed = Interlocked.CompareExchange(ref _maxTicks, ticks, current);
                    if (observed == current)
                    {
                        Volatile.Write(ref _maxContext, context);
                        break;
                    }

                    current = observed;
                }

                double ms = Milliseconds(ticks);
                int bucket = UpperMs.Length - 1;
                for (int i = 0; i < UpperMs.Length - 1; i++)
                {
                    if (ms <= UpperMs[i])
                    {
                        bucket = i;
                        break;
                    }
                }

                Interlocked.Increment(ref _buckets[bucket]);
            }

            public Snapshot Take()
            {
                long count = Interlocked.Exchange(ref _count, 0);
                long ticks = Interlocked.Exchange(ref _ticks, 0);
                long maxTicks = Interlocked.Exchange(ref _maxTicks, 0);
                long maxContext = Interlocked.Exchange(ref _maxContext, 0);
                long[] buckets = new long[_buckets.Length];

                for (int i = 0; i < buckets.Length; i++)
                {
                    buckets[i] = Interlocked.Exchange(ref _buckets[i], 0);
                }

                return new Snapshot(count, ticks, maxTicks, maxContext, buckets);
            }

            public readonly record struct Snapshot(long Count, long Ticks, long MaxTicks, long MaxContext, long[] Buckets)
            {
                public double AverageMs => Count == 0 ? 0 : Milliseconds(Ticks) / Count;
                public double MaxMs => Milliseconds(MaxTicks);

                public double Percentile(double percentile)
                {
                    if (Count == 0)
                    {
                        return 0;
                    }

                    long target = Math.Max(1, (long)Math.Ceiling(Count * percentile));
                    long cumulative = 0;

                    for (int i = 0; i < Buckets.Length; i++)
                    {
                        cumulative += Buckets[i];
                        if (cumulative >= target)
                        {
                            return i == Buckets.Length - 1 ? MaxMs : UpperMs[i];
                        }
                    }

                    return MaxMs;
                }
            }
        }

        private static readonly LatencyHistogram _publication = new();
        private static readonly LatencyHistogram _backend = new();
        private static readonly LatencyHistogram _backendInterrupt = new();
        private static readonly LatencyHistogram _submitQueue = new();
        private static readonly LatencyHistogram _queueSubmit = new();
        private static readonly LatencyHistogram _sparseQueue = new();
        private static readonly LatencyHistogram _queueBindSparse = new();
        private static readonly LatencyHistogram _presentQueue = new();
        private static readonly LatencyHistogram _queuePresent = new();
        private static readonly LatencyHistogram _drainQueue = new();
        private static readonly LatencyHistogram _texturePreflush = new();
        private static readonly LatencyHistogram _textureFlushAction = new();
        private static readonly LatencyHistogram _backgroundBarrier = new();
        private static readonly LatencyHistogram _bufferPostWaitFlush = new();

        private static long _publicationImmediate;
        private static long _publicationWakeups;
        private static long _publicationLagTotal;
        private static long _publicationLagMax;
        private static long _rendererQueueDepthTotal;
        private static long _rendererQueueDepthMax;
        private static long _backendWatermark;
        private static long _backendMissing;
        private static long _backendTimedOut;
        private static long _backendFlushInterrupt;
        private static long _submitQueueDepthMax;
        private static long _submitFaults;
        private static long _workerStateTransitions;
        private static long _readbackBoundaries;
        private static long _externalFlushBoundaries;
        private static long _deviceIdleBoundaries;
        private static long _shutdownBoundaries;
        private static long _genericBoundaries;
        private static long _cancelledBoundaries;
        private static long _textureForcedSync;
        private static long _textureSyncWait;
        private static long _textureSyncNotReady;
        private static long _textureInBuffer;
        private static long _textureDirect;
        private static long _textureImported;
        private static long _textureSlices;
        private static long _textureBytes;
        private static long _backgroundInvoked;
        private static long _backgroundDirect;
        private static long _backgroundBatchActions;
        private static int _firstFaultRecorded;
        private static long _textureFlushRejectedByRevalidation;
        private static long _layoutConverterRejectedBySize;

        private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static void UpdateMax(ref long target, long value)
        {
            long current = Volatile.Read(ref target);
            while (value > current)
            {
                long observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }

        public static void RecordPublication(long ticks, ulong requestedGeneration, ulong targetGeneration, ulong createdGeneration, ulong publishedGeneration, int wakeups)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            ulong lag = createdGeneration >= publishedGeneration ? createdGeneration - publishedGeneration : 0;
            long safeLag = lag > long.MaxValue ? long.MaxValue : (long)lag;
            _publication.Record(ticks, requestedGeneration > long.MaxValue ? long.MaxValue : (long)requestedGeneration);
            if (wakeups == 0)
            {
                Interlocked.Increment(ref _publicationImmediate);
            }

            Interlocked.Add(ref _publicationWakeups, Math.Max(0, wakeups));
            Interlocked.Add(ref _publicationLagTotal, safeLag);
            UpdateMax(ref _publicationLagMax, safeLag);
        }

        public static void RecordRendererQueueDepth(int depth)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            depth = Math.Max(0, depth);
            Interlocked.Add(ref _rendererQueueDepthTotal, depth);
            UpdateMax(ref _rendererQueueDepthMax, depth);
        }

        public static void RecordBackendWait(long ticks, ulong id, bool watermark, bool missing, bool timedOut, bool flushInterrupt, long interruptTicks)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            _backend.Record(ticks, id > long.MaxValue ? long.MaxValue : (long)id);
            if (watermark) Interlocked.Increment(ref _backendWatermark);
            if (missing) Interlocked.Increment(ref _backendMissing);
            if (timedOut) Interlocked.Increment(ref _backendTimedOut);
            if (flushInterrupt) Interlocked.Increment(ref _backendFlushInterrupt);
            if (interruptTicks > 0) _backendInterrupt.Record(interruptTicks, id > long.MaxValue ? long.MaxValue : (long)id);
        }

        public static void RecordSubmitDequeued(long queueTicks, int enqueuedDepth, ulong timelineValue)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            _submitQueue.Record(queueTicks, timelineValue > long.MaxValue ? long.MaxValue : (long)timelineValue);
            UpdateMax(ref _submitQueueDepthMax, Math.Max(0, enqueuedDepth));
        }

        public static void RecordQueueSubmit(long ticks, ulong timelineValue)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            _queueSubmit.Record(ticks, timelineValue > long.MaxValue ? long.MaxValue : (long)timelineValue);
        }

        public static void RecordSparseBindDequeued(long queueTicks, int enqueuedDepth, ulong sequence)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            _sparseQueue.Record(queueTicks, sequence > long.MaxValue ? long.MaxValue : (long)sequence);
            UpdateMax(ref _submitQueueDepthMax, Math.Max(0, enqueuedDepth));
        }

        public static void RecordQueueBindSparse(long ticks, ulong sequence)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            _queueBindSparse.Record(ticks, sequence > long.MaxValue ? long.MaxValue : (long)sequence);
        }

        public static void RecordPresentDequeued(long queueTicks, int enqueuedDepth, ulong sequence)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            _presentQueue.Record(queueTicks, sequence > long.MaxValue ? long.MaxValue : (long)sequence);
            UpdateMax(ref _submitQueueDepthMax, Math.Max(0, enqueuedDepth));
        }

        public static void RecordQueuePresent(long ticks, ulong sequence)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            _queuePresent.Record(ticks, sequence > long.MaxValue ? long.MaxValue : (long)sequence);
        }

        public static void RecordDrainDequeued(long queueTicks, int enqueuedDepth, ulong sequence, string boundary)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            _drainQueue.Record(queueTicks, sequence > long.MaxValue ? long.MaxValue : (long)sequence);
            UpdateMax(ref _submitQueueDepthMax, Math.Max(0, enqueuedDepth));
        }

        public static void RecordSubmissionBoundary(string boundary, bool cancelled)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            if (cancelled)
            {
                Interlocked.Increment(ref _cancelledBoundaries);
                return;
            }

            switch (boundary)
            {
                case "Readback": Interlocked.Increment(ref _readbackBoundaries); break;
                case "ExternalFlush": Interlocked.Increment(ref _externalFlushBoundaries); break;
                case "DeviceIdle": Interlocked.Increment(ref _deviceIdleBoundaries); break;
                case "Shutdown": Interlocked.Increment(ref _shutdownBoundaries); break;
                default: Interlocked.Increment(ref _genericBoundaries); break;
            }
        }

        public static void RecordSubmissionStateTransition(string from, string to, ulong sequence)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            Interlocked.Increment(ref _workerStateTransitions);
            Logger.Debug?.Print(LogClass.Gpu,
                $"[RYUJINX-NEO-SUBMISSION-STATE] from={from} to={to} sequence={sequence}");
        }

        public static void RecordSubmissionFault(string operation, string exception, ulong timelineValue, ulong sequence, int queueDepth)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            Interlocked.Increment(ref _submitFaults);
            if (Interlocked.CompareExchange(ref _firstFaultRecorded, 1, 0) == 0)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu,
                    $"[RYUJINX-NEO-SUBMISSION-FIRST-FAULT] operation={operation} timeline={timelineValue} sequence={sequence} queueDepth={queueDepth} exception={exception}");
            }
        }

        public static void RecordTextureForcedSync()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            Interlocked.Increment(ref _textureForcedSync);
        }

        public static void RecordTextureSync(bool waited, bool ready, bool inBuffer)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            if (waited) Interlocked.Increment(ref _textureSyncWait);
            if (!ready) Interlocked.Increment(ref _textureSyncNotReady);
            if (inBuffer) Interlocked.Increment(ref _textureInBuffer);
            else Interlocked.Increment(ref _textureDirect);
        }

        public static void RecordTexturePreflush(long ticks, int slices, ulong bytes, bool imported)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            _texturePreflush.Record(ticks, slices);
            Interlocked.Add(ref _textureSlices, Math.Max(0, slices));
            Interlocked.Add(ref _textureBytes, bytes > long.MaxValue ? long.MaxValue : (long)bytes);
            if (imported) Interlocked.Increment(ref _textureImported);
        }

        public static void RecordTextureFlushAction(long ticks, int slices, ulong bytes, bool inBuffer, bool imported)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            _textureFlushAction.Record(ticks, slices);
            Interlocked.Add(ref _textureSlices, Math.Max(0, slices));
            Interlocked.Add(ref _textureBytes, bytes > long.MaxValue ? long.MaxValue : (long)bytes);
            if (inBuffer) Interlocked.Increment(ref _textureInBuffer);
            else Interlocked.Increment(ref _textureDirect);
            if (imported) Interlocked.Increment(ref _textureImported);
        }

        public static void RecordBackgroundBarrier(long ticks, int queueDepth, bool invoked)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            _backgroundBarrier.Record(ticks, Math.Max(0, queueDepth));
            if (invoked) Interlocked.Increment(ref _backgroundInvoked);
            else Interlocked.Increment(ref _backgroundDirect);
        }

        public static void RecordBackgroundBatch(int actions)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            Interlocked.Add(ref _backgroundBatchActions, Math.Max(0, actions));
        }

        public static void RecordBufferPostWaitFlush(long ticks, int ranges)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            _bufferPostWaitFlush.Record(ticks, Math.Max(0, ranges));
        }

        public static void RecordTextureFlushRejectedByRevalidation()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            Interlocked.Increment(ref _textureFlushRejectedByRevalidation);
        }

        public static void RecordLayoutConverterRejectedBySize()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            Interlocked.Increment(ref _layoutConverterRejectedBySize);
        }

        public static long TextureFlushRejectedByRevalidation => Volatile.Read(ref _textureFlushRejectedByRevalidation);
        public static long LayoutConverterRejectedBySize => Volatile.Read(ref _layoutConverterRejectedBySize);

        public static void ResetRejectionCounters()
        {
            Interlocked.Exchange(ref _textureFlushRejectedByRevalidation, 0);
            Interlocked.Exchange(ref _layoutConverterRejectedBySize, 0);
        }

        private static string H(string name, LatencyHistogram.Snapshot value)
        {
            return $"{name}={value.Count}/{value.AverageMs:F3}ms p50<={value.Percentile(0.50):F3} p95<={value.Percentile(0.95):F3} p99<={value.Percentile(0.99):F3} max={value.MaxMs:F1}@{value.MaxContext}";
        }

        public static void LogSnapshot(long sequence)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;
            LatencyHistogram.Snapshot publication = _publication.Take();
            LatencyHistogram.Snapshot backend = _backend.Take();
            LatencyHistogram.Snapshot interrupt = _backendInterrupt.Take();
            LatencyHistogram.Snapshot submitQueue = _submitQueue.Take();
            LatencyHistogram.Snapshot queueSubmit = _queueSubmit.Take();
            LatencyHistogram.Snapshot sparseQueue = _sparseQueue.Take();
            LatencyHistogram.Snapshot queueBindSparse = _queueBindSparse.Take();
            LatencyHistogram.Snapshot presentQueue = _presentQueue.Take();
            LatencyHistogram.Snapshot queuePresent = _queuePresent.Take();
            LatencyHistogram.Snapshot drainQueue = _drainQueue.Take();
            LatencyHistogram.Snapshot texturePreflush = _texturePreflush.Take();
            LatencyHistogram.Snapshot textureFlushAction = _textureFlushAction.Take();
            LatencyHistogram.Snapshot backgroundBarrier = _backgroundBarrier.Take();
            LatencyHistogram.Snapshot bufferPostWaitFlush = _bufferPostWaitFlush.Take();
            long publicationImmediate = Interlocked.Exchange(ref _publicationImmediate, 0);
            long publicationWakeups = Interlocked.Exchange(ref _publicationWakeups, 0);
            long publicationLagTotal = Interlocked.Exchange(ref _publicationLagTotal, 0);
            long publicationLagMax = Interlocked.Exchange(ref _publicationLagMax, 0);
            long rendererQueueDepthTotal = Interlocked.Exchange(ref _rendererQueueDepthTotal, 0);
            long rendererQueueDepthMax = Interlocked.Exchange(ref _rendererQueueDepthMax, 0);
            long backendWatermark = Interlocked.Exchange(ref _backendWatermark, 0);
            long backendMissing = Interlocked.Exchange(ref _backendMissing, 0);
            long backendTimedOut = Interlocked.Exchange(ref _backendTimedOut, 0);
            long backendFlushInterrupt = Interlocked.Exchange(ref _backendFlushInterrupt, 0);
            long submitQueueDepthMax = Interlocked.Exchange(ref _submitQueueDepthMax, 0);
            long submitFaults = Interlocked.Exchange(ref _submitFaults, 0);
            long workerStateTransitions = Interlocked.Exchange(ref _workerStateTransitions, 0);
            long readbackBoundaries = Interlocked.Exchange(ref _readbackBoundaries, 0);
            long externalFlushBoundaries = Interlocked.Exchange(ref _externalFlushBoundaries, 0);
            long deviceIdleBoundaries = Interlocked.Exchange(ref _deviceIdleBoundaries, 0);
            long shutdownBoundaries = Interlocked.Exchange(ref _shutdownBoundaries, 0);
            long genericBoundaries = Interlocked.Exchange(ref _genericBoundaries, 0);
            long cancelledBoundaries = Interlocked.Exchange(ref _cancelledBoundaries, 0);
            long textureForcedSync = Interlocked.Exchange(ref _textureForcedSync, 0);
            long textureSyncWait = Interlocked.Exchange(ref _textureSyncWait, 0);
            long textureSyncNotReady = Interlocked.Exchange(ref _textureSyncNotReady, 0);
            long textureInBuffer = Interlocked.Exchange(ref _textureInBuffer, 0);
            long textureDirect = Interlocked.Exchange(ref _textureDirect, 0);
            long textureImported = Interlocked.Exchange(ref _textureImported, 0);
            long textureSlices = Interlocked.Exchange(ref _textureSlices, 0);
            long textureBytes = Interlocked.Exchange(ref _textureBytes, 0);
            long backgroundInvoked = Interlocked.Exchange(ref _backgroundInvoked, 0);
            long backgroundDirect = Interlocked.Exchange(ref _backgroundDirect, 0);
            long backgroundBatchActions = Interlocked.Exchange(ref _backgroundBatchActions, 0);
            long textureFlushRejectedByRevalidation = Interlocked.Exchange(ref _textureFlushRejectedByRevalidation, 0);
            long layoutConverterRejectedBySize = Interlocked.Exchange(ref _layoutConverterRejectedBySize, 0);

            double publicationLagAverage = publication.Count == 0 ? 0 : (double)publicationLagTotal / publication.Count;
            double rendererQueueDepthAverage = backend.Count == 0 ? 0 : (double)rendererQueueDepthTotal / backend.Count;

            Logger.Info?.Print(LogClass.Gpu,
                $"[RYUJINX-NEO-READBACK-DECISION] seq={sequence} {H("publish", publication)} immediate={publicationImmediate} wakeups={publicationWakeups} lagAvg={publicationLagAverage:F1} lagMax={publicationLagMax} rendererQAvg={rendererQueueDepthAverage:F1} rendererQMax={rendererQueueDepthMax} | " +
                $"{H("backend", backend)} watermark={backendWatermark} missing={backendMissing} timeout={backendTimedOut} flushInterrupt={backendFlushInterrupt} {H("interrupt", interrupt)} | " +
                $"{H("submitQueue", submitQueue)} submitQMax={submitQueueDepthMax} {H("vkQueueSubmit", queueSubmit)} {H("sparseQueue", sparseQueue)} {H("vkQueueBindSparse", queueBindSparse)} {H("presentQueue", presentQueue)} {H("vkQueuePresent", queuePresent)} {H("drainQueue", drainQueue)} boundaries=readback:{readbackBoundaries},external:{externalFlushBoundaries},idle:{deviceIdleBoundaries},shutdown:{shutdownBoundaries},generic:{genericBoundaries},cancelled:{cancelledBoundaries} faults={submitFaults} stateTransitions={workerStateTransitions} | " +
                $"forcedHostSync={textureForcedSync} textureSyncWait={textureSyncWait} textureNotReady={textureSyncNotReady} inBuffer={textureInBuffer} direct={textureDirect} imported={textureImported} slices={textureSlices} MiB={textureBytes / 1048576.0:F1} {H("preflush", texturePreflush)} {H("textureFlush", textureFlushAction)} | " +
                $"textureFlushRejectedByRevalidation={textureFlushRejectedByRevalidation} layoutConverterRejectedBySize={layoutConverterRejectedBySize} | " +
                $"{H("backgroundBarrier", backgroundBarrier)} invoked={backgroundInvoked} direct={backgroundDirect} batchedActions={backgroundBatchActions} {H("postWaitFlush", bufferPostWaitFlush)}");
        }

        // NEOV2: the snapshot above already existed but was never called periodically,
        // on the Current branch either. This wrapper gives it the same self-rate-limited
        // "TryFlush" shape as BaselineTelemetry/TextureFormatTelemetry, so it actually
        // reaches the log instead of only accumulating silently.
        private static long _lastSnapshotTicks = Stopwatch.GetTimestamp();
        private static long _snapshotSequence;

        public static void TryLogSnapshot()
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            long last = Volatile.Read(ref _lastSnapshotTicks);
            if (now - last < Stopwatch.Frequency * 5)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastSnapshotTicks, now, last) != last)
            {
                return;
            }

            LogSnapshot(Interlocked.Increment(ref _snapshotSequence));
        }
    }
}
