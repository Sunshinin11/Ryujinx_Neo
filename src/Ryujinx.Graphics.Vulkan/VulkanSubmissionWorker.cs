using Ryujinx.Common.Diagnostics;
using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkQueue = Silk.NET.Vulkan.Queue;

namespace Ryujinx.Graphics.Vulkan
{
    enum VulkanSubmissionBoundary
    {
        Generic,
        Readback,
        ExternalFlush,
        DeviceIdle,
        Shutdown,
    }

    sealed class VulkanSubmissionDeviceLostException : InvalidOperationException
    {
        public string Operation { get; }
        public ulong TimelineValue { get; }
        public ulong Sequence { get; }

        public VulkanSubmissionDeviceLostException(string operation, ulong timelineValue, ulong sequence = 0)
            : base($"Vulkan device lost during {operation} at timeline {timelineValue}, sequence {sequence}.")
        {
            Operation = operation;
            TimelineValue = timelineValue;
            Sequence = sequence;
        }
    }

    sealed class VulkanSubmissionHandle
    {
        private readonly TaskCompletionSource<bool> _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsCompleted => _completed.Task.IsCompleted;

        internal void Complete(Exception error)
        {
            if (error != null)
            {
                _completed.TrySetException(error);
            }
            else
            {
                _completed.TrySetResult(true);
            }
        }

        public void ThrowIfFaulted()
        {
            if (_completed.Task.IsCompleted)
            {
                _completed.Task.GetAwaiter().GetResult();
            }
        }

        public void Wait()
        {
            _completed.Task.GetAwaiter().GetResult();
        }

        public void Wait(CancellationToken cancellationToken)
        {
            _completed.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
        }
    }

    sealed class VulkanSubmissionWorker : IDisposable
    {
        internal enum WorkerState
        {
            Running,
            Faulted,
            Stopping,
            Stopped,
            Disposed,
        }

        private enum WorkType
        {
            Submit,
            BindSparse,
            Present,
            Drain,
            Stop,
        }

        private sealed class WorkItem
        {
            public WorkType Type;

            public CommandBuffer CommandBuffer;
            public Fence Fence;
            public Semaphore[] WaitSemaphores;
            public PipelineStageFlags[] WaitStages;
            public Semaphore[] SignalSemaphores;
            public int WaitSemaphoreCount;
            public int SignalSemaphoreCount;
            public VulkanSubmissionHandle Submission;
            public ulong TimelineValue;

            public VkBuffer SparseBuffer;
            public SparseMemoryBind[] SparseMemoryBinds;

            public KhrSwapchain SwapchainApi;
            public Semaphore PresentWaitSemaphore;
            public SwapchainKHR Swapchain;
            public uint ImageIndex;
            public Result PresentResult;
            // Queue submissions are completed through their FenceHolder/timeline.
            // Only synchronous control operations (present, drain, sparse bind and stop)
            // need a separate host completion handle.
            public VulkanSubmissionHandle Completion;
            public long EnqueuedTicks;
            public int EnqueuedDepth;
            public ulong Sequence;
            public VulkanSubmissionBoundary Boundary;

            public void Fail(Exception error)
            {
                if (Type == WorkType.Submit)
                {
                    Submission.Complete(error);
                }

                Completion?.Complete(error);
            }

            public void Complete()
            {
                if (Type == WorkType.Submit)
                {
                    Submission.Complete(null);
                }

                Completion?.Complete(null);
            }
        }

        private readonly Vk _api;
        private readonly Device _device;
        private readonly VkQueue _queue;
        private readonly Lock _queueLock;
        private readonly object _submissionGate;
        private readonly Thread _thread;
        private readonly AutoResetEvent _wakeEvent;
        private readonly object _sync = new();
        private readonly Queue<WorkItem> _workQueue = new();
        private readonly ConcurrentBag<WorkItem> _submitItemPool = new();
        private readonly VulkanSubmissionHandle _disposeCompletion = new();
        private readonly bool _timelineEnabled;
        private readonly SubmissionTelemetry SubmissionTelemetry;
        private Semaphore _timelineSemaphore;
        private long _nextTimelineValue;
        private long _nextSequence;
        private WorkItem _activeItem;

        private ExceptionDispatchInfo _fault;
        private string _faultOperation;
        private ulong _faultTimelineValue;
        private ulong _faultSequence;
        private WorkerState _state = WorkerState.Running;
        private bool _workerExited;

        public bool TimelineEnabled => _timelineEnabled;
        internal WorkerState State
        {
            get
            {
                lock (_sync)
                {
                    return _state;
                }
            }
        }
        internal (string Operation, ulong TimelineValue, ulong Sequence, Exception Error)? FirstFault
        {
            get
            {
                lock (_sync)
                {
                    return _fault == null
                        ? null
                        : (_faultOperation, _faultTimelineValue, _faultSequence, _fault.SourceException);
                }
            }
        }

        public unsafe VulkanSubmissionWorker(Vk api, Device device, VkQueue queue, Lock queueLock, object submissionGate, bool timelineEnabled, string name)
        {
            _api = api;
            _device = device;
            _queue = queue;
            _queueLock = queueLock;
            _submissionGate = submissionGate;
            _timelineEnabled = timelineEnabled;
            SubmissionTelemetry = new SubmissionTelemetry(name);
            _wakeEvent = new AutoResetEvent(false);

            if (_timelineEnabled)
            {
                var typeCreateInfo = new SemaphoreTypeCreateInfo
                {
                    SType = StructureType.SemaphoreTypeCreateInfo,
                    SemaphoreType = SemaphoreType.Timeline,
                    InitialValue = 0,
                };

                var semaphoreCreateInfo = new SemaphoreCreateInfo
                {
                    SType = StructureType.SemaphoreCreateInfo,
                    PNext = &typeCreateInfo,
                };

                _api.CreateSemaphore(_device, in semaphoreCreateInfo, null, out _timelineSemaphore).ThrowOnError();
            }

            _thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = name,
            };
            _thread.Start();
        }

        private ExceptionDispatchInfo GetFault()
        {
            lock (_sync)
            {
                return _fault;
            }
        }

        private void SetFault(Exception error, WorkItem item)
        {
            if (error is VulkanSubmissionDeviceLostException deviceLost)
            {
                SetFault(error, deviceLost.Operation, deviceLost.TimelineValue, deviceLost.Sequence);
            }
            else
            {
                SetFault(error, item.Type.ToString(), item.TimelineValue, item.Sequence);
            }
        }

        private void SetFault(Exception error, string operation, ulong timelineValue, ulong sequence)
        {
            lock (_sync)
            {
                if (_fault == null)
                {
                    _fault = ExceptionDispatchInfo.Capture(error);
                    _faultOperation = operation;
                    _faultTimelineValue = timelineValue;
                    _faultSequence = sequence;

                    if (_state == WorkerState.Running)
                    {
                        ReadbackDecisionTelemetry.RecordSubmissionStateTransition(
                            WorkerState.Running.ToString(),
                            WorkerState.Faulted.ToString(),
                            (ulong)Volatile.Read(ref _nextSequence));
                        _state = WorkerState.Faulted;
                    }
                }
            }
        }

        public void ThrowIfFaulted()
        {
            GetFault()?.Throw();
        }

        private void Enqueue(WorkItem item, bool allowFaulted = false)
        {
            lock (_submissionGate)
            {
                lock (_sync)
                {
                    if (EnqueueLocked(item, allowFaulted))
                    {
                        _wakeEvent.Set();
                    }
                }
            }
        }

        private bool EnqueueLocked(WorkItem item, bool allowFaulted)
        {
            if (_state is WorkerState.Stopped or WorkerState.Disposed ||
                (_state == WorkerState.Stopping && item.Type != WorkType.Stop))
            {
                throw new ObjectDisposedException(nameof(VulkanSubmissionWorker));
            }

            if (!allowFaulted)
            {
                _fault?.Throw();
            }

            if (GezinjinxPerformanceMode.DetailedTelemetry)
            {
                item.EnqueuedTicks = Stopwatch.GetTimestamp();
                item.EnqueuedDepth = _workQueue.Count + 1;
            }

            bool wakeWorker = _workQueue.Count == 0;
            item.Sequence = (ulong)Interlocked.Increment(ref _nextSequence);
            _workQueue.Enqueue(item);

            if (GezinjinxPerformanceMode.DetailedTelemetry)
            {
                GezinjinxEfficiencyTelemetry.RecordSubmitQueueDepth(_workQueue.Count);
            }

            return wakeWorker;
        }

        private ulong ReserveTimelineValue()
        {
            return (ulong)Interlocked.Increment(ref _nextTimelineValue);
        }

        private WorkItem RentSubmitItem()
        {
            if (!_submitItemPool.TryTake(out WorkItem item))
            {
                item = new WorkItem();
            }

            item.Type = WorkType.Submit;
            return item;
        }

        private void ReturnSubmitItem(WorkItem item)
        {
            if (item.WaitSemaphores?.Length > 0)
            {
                ArrayPool<Semaphore>.Shared.Return(item.WaitSemaphores);
            }
            if (item.WaitStages?.Length > 0)
            {
                ArrayPool<PipelineStageFlags>.Shared.Return(item.WaitStages);
            }
            if (item.SignalSemaphores?.Length > 0)
            {
                ArrayPool<Semaphore>.Shared.Return(item.SignalSemaphores);
            }

            item.CommandBuffer = default;
            item.Fence = default;
            item.WaitSemaphores = null;
            item.WaitStages = null;
            item.SignalSemaphores = null;
            item.WaitSemaphoreCount = 0;
            item.SignalSemaphoreCount = 0;
            item.Submission = null;
            item.TimelineValue = 0;
            item.EnqueuedTicks = 0;
            item.EnqueuedDepth = 0;
            item.Sequence = 0;

            _submitItemPool.Add(item);
        }

        public void Submit(
            CommandBuffer commandBuffer,
            FenceHolder completion,
            ReadOnlySpan<Semaphore> waitSemaphores,
            ReadOnlySpan<PipelineStageFlags> waitStages,
            ReadOnlySpan<Semaphore> signalSemaphores)
        {
            if (waitSemaphores.Length != waitStages.Length)
            {
                throw new ArgumentException("Wait semaphore and stage-mask counts must match.");
            }

            int waitCount = waitSemaphores.Length;
            int signalCount = signalSemaphores.Length + (_timelineEnabled ? 1 : 0);
            WorkItem item = RentSubmitItem();
            item.CommandBuffer = commandBuffer;
            item.Fence = completion.GetSubmitFence();
            item.WaitSemaphores = waitCount == 0 ? Array.Empty<Semaphore>() : ArrayPool<Semaphore>.Shared.Rent(waitCount);
            item.WaitStages = waitCount == 0 ? Array.Empty<PipelineStageFlags>() : ArrayPool<PipelineStageFlags>.Shared.Rent(waitCount);
            item.SignalSemaphores = signalCount == 0 ? Array.Empty<Semaphore>() : ArrayPool<Semaphore>.Shared.Rent(signalCount);
            item.WaitSemaphoreCount = waitCount;
            item.SignalSemaphoreCount = signalCount;
            item.Submission = completion.Submission;

            waitSemaphores.CopyTo(item.WaitSemaphores);
            waitStages.CopyTo(item.WaitStages);
            signalSemaphores.CopyTo(item.SignalSemaphores);

            if (_timelineEnabled)
            {
                item.SignalSemaphores[signalCount - 1] = _timelineSemaphore;
            }

            try
            {
                // Timeline values must follow the exact FIFO order in which work enters
                // this VkQueue. Reserve the value while holding the same lock used by
                // Enqueue so two producer threads cannot reserve 1/2 and enqueue 2/1.
                lock (_submissionGate)
                {
                    lock (_sync)
                    {
                        if (_timelineEnabled)
                        {
                            item.TimelineValue = ReserveTimelineValue();
                            completion.PrepareTimeline(item.TimelineValue);
                        }

                        if (EnqueueLocked(item, allowFaulted: false))
                        {
                            _wakeEvent.Set();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                completion.Submission.Complete(ex);
                ReturnSubmitItem(item);
                throw;
            }
        }

        public Result Present(KhrSwapchain swapchainApi, Semaphore waitSemaphore, SwapchainKHR swapchain, uint imageIndex)
        {
            var item = new WorkItem
            {
                Type = WorkType.Present,
                Completion = new VulkanSubmissionHandle(),
                SwapchainApi = swapchainApi,
                PresentWaitSemaphore = waitSemaphore,
                Swapchain = swapchain,
                ImageIndex = imageIndex,
            };

            Enqueue(item);
            long presentWaitStart = GezinjinxPerformanceMode.DetailedTelemetry ? Stopwatch.GetTimestamp() : 0;
            item.Completion.Wait();
            if (GezinjinxPerformanceMode.DetailedTelemetry)
            {
                GezinjinxEfficiencyTelemetry.RecordPresentCallerWait(Stopwatch.GetTimestamp() - presentWaitStart);
            }

            ThrowIfFaulted();
            return item.PresentResult;
        }

        public void BindSparse(VkBuffer buffer, ReadOnlySpan<SparseMemoryBind> memoryBinds)
        {
            var item = new WorkItem
            {
                Type = WorkType.BindSparse,
                Completion = new VulkanSubmissionHandle(),
                SparseBuffer = buffer,
                SparseMemoryBinds = memoryBinds.ToArray(),
            };

            Enqueue(item);
            item.Completion.Wait();
            ThrowIfFaulted();
        }

        public void Drain(VulkanSubmissionBoundary boundary = VulkanSubmissionBoundary.Generic, CancellationToken cancellationToken = default)
        {
            var item = new WorkItem
            {
                Type = WorkType.Drain,
                Completion = new VulkanSubmissionHandle(),
                Boundary = boundary,
            };
            Enqueue(item);
            long drainWaitStart = GezinjinxPerformanceMode.DetailedTelemetry ? Stopwatch.GetTimestamp() : 0;
            try
            {
                item.Completion.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ReadbackDecisionTelemetry.RecordSubmissionBoundary(boundary.ToString(), cancelled: true);
                throw;
            }
            finally
            {
                if (GezinjinxPerformanceMode.DetailedTelemetry)
                {
                    GezinjinxEfficiencyTelemetry.RecordDrainCallerWait(Stopwatch.GetTimestamp() - drainWaitStart);
                }
            }

            ReadbackDecisionTelemetry.RecordSubmissionBoundary(boundary.ToString(), cancelled: false);
            ThrowIfFaulted();
        }

        public unsafe bool IsTimelineReached(ulong value)
        {
            if (!_timelineEnabled)
            {
                throw new InvalidOperationException("Timeline semaphore completion is not enabled for this queue.");
            }

            ThrowIfFaulted();

            if (TimelineCompletionGenerationCache.IsKnownComplete(this, value))
            {
                return true;
            }

            Result result = _api.GetSemaphoreCounterValue(_device, _timelineSemaphore, out ulong currentValue);
            ThrowTimelineResult(result, "GetSemaphoreCounterValue", value);

            TimelineCompletionGenerationCache.MarkCompleted(this, currentValue);
            SubmissionTelemetry.Completed(currentValue);
            return currentValue >= value;
        }

        public unsafe bool WaitTimeline(ulong value, ulong timeout)
        {
            if (!_timelineEnabled)
            {
                throw new InvalidOperationException("Timeline semaphore completion is not enabled for this queue.");
            }

            ThrowIfFaulted();

            if (TimelineCompletionGenerationCache.IsKnownComplete(this, value))
            {
                SubmissionTelemetry.CachedWait();
                return true;
            }

            var semaphore = _timelineSemaphore;
            var waitValue = value;
            var waitInfo = new SemaphoreWaitInfo
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = &semaphore,
                PValues = &waitValue,
            };

            long waitStarted = Stopwatch.GetTimestamp();
            Result result = _api.WaitSemaphores(_device, in waitInfo, timeout);
            SubmissionTelemetry.Waited(Stopwatch.GetTimestamp() - waitStarted, result == Result.Timeout);
            if (result == Result.Timeout)
            {
                return false;
            }

            ThrowTimelineResult(result, "WaitSemaphores", value);
            TimelineCompletionGenerationCache.MarkCompleted(this, value);
            SubmissionTelemetry.Completed(value);
            return true;
        }

        private void ThrowTimelineResult(Result result, string operation, ulong timelineValue)
        {
            if (result == Result.ErrorDeviceLost)
            {
                throw RecordDeviceLost(operation, timelineValue);
            }

            result.ThrowOnError();
        }

        internal VulkanSubmissionDeviceLostException RecordDeviceLost(string operation, ulong timelineValue = 0)
        {
            ulong sequence = (ulong)Volatile.Read(ref _nextSequence);
            var error = new VulkanSubmissionDeviceLostException(operation, timelineValue, sequence);
            SetFault(error, operation, timelineValue, sequence);
            return error;
        }

        private bool TryDequeue(out WorkItem item)
        {
            lock (_sync)
            {
                if (_workQueue.Count != 0)
                {
                    item = _workQueue.Dequeue();
                    return true;
                }
            }

            item = null;
            return false;
        }

        private void WorkerLoop()
        {
            if (GezinjinxPerformanceMode.DetailedTelemetry)
            {
                GezinjinxEfficiencyTelemetry.RegisterCurrentThread("vkWorker");
            }

            try
            {
                WorkerLoopCore();
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _workerExited = true;
                }

                SetFault(
                    exception,
                    "WorkerLoop",
                    (ulong)Volatile.Read(ref _nextTimelineValue),
                    (ulong)Volatile.Read(ref _nextSequence));
                WorkItem activeItem = _activeItem;
                activeItem?.Fail(exception);
                if (activeItem?.Type == WorkType.Submit)
                {
                    ReturnSubmitItem(activeItem);
                }
                _activeItem = null;
                FailPending(exception);
            }
            finally
            {
                lock (_sync)
                {
                    _workerExited = true;
                }
            }
        }

        private void FailPending(Exception exception)
        {
            while (TryDequeue(out WorkItem item))
            {
                item.Fail(exception);
                if (item.Type == WorkType.Submit)
                {
                    ReturnSubmitItem(item);
                }
            }
        }

        private void WorkerLoopCore()
        {

            while (true)
            {
                long workerWaitStart = GezinjinxPerformanceMode.DetailedTelemetry ? Stopwatch.GetTimestamp() : 0;
                _wakeEvent.WaitOne();
                if (GezinjinxPerformanceMode.DetailedTelemetry)
                {
                    GezinjinxEfficiencyTelemetry.RecordSubmitWorkerWait(Stopwatch.GetTimestamp() - workerWaitStart);
                }

                while (TryDequeue(out WorkItem item))
                {
                    _activeItem = item;

                    if (GezinjinxPerformanceMode.DetailedTelemetry && item.Type == WorkType.Submit)
                    {
                        ReadbackDecisionTelemetry.RecordSubmitDequeued(
                            Stopwatch.GetTimestamp() - item.EnqueuedTicks,
                            item.EnqueuedDepth,
                            item.TimelineValue);
                    }
                    else if (GezinjinxPerformanceMode.DetailedTelemetry && item.Type == WorkType.BindSparse)
                    {
                        ReadbackDecisionTelemetry.RecordSparseBindDequeued(
                            Stopwatch.GetTimestamp() - item.EnqueuedTicks,
                            item.EnqueuedDepth,
                            item.Sequence);
                    }
                    else if (GezinjinxPerformanceMode.DetailedTelemetry && item.Type == WorkType.Present)
                    {
                        ReadbackDecisionTelemetry.RecordPresentDequeued(
                            Stopwatch.GetTimestamp() - item.EnqueuedTicks,
                            item.EnqueuedDepth,
                            item.Sequence);
                    }
                    else if (GezinjinxPerformanceMode.DetailedTelemetry && item.Type == WorkType.Drain)
                    {
                        ReadbackDecisionTelemetry.RecordDrainDequeued(
                            Stopwatch.GetTimestamp() - item.EnqueuedTicks,
                            item.EnqueuedDepth,
                            item.Sequence,
                            item.Boundary.ToString());
                    }

                    if (item.Type == WorkType.Stop)
                    {
                        ReadbackDecisionTelemetry.RecordSubmissionBoundary(
                            VulkanSubmissionBoundary.Shutdown.ToString(),
                            cancelled: false);
                        lock (_sync)
                        {
                            ReadbackDecisionTelemetry.RecordSubmissionStateTransition(
                                _state.ToString(),
                                WorkerState.Stopped.ToString(),
                                item.Sequence);
                            _state = WorkerState.Stopped;
                        }

                        item.Complete();
                        _activeItem = null;
                        return;
                    }

                    var fault = GetFault();
                    if (fault != null && item.Type is WorkType.Submit or WorkType.BindSparse or WorkType.Present)
                    {
                        item.Fail(fault.SourceException);
                        _activeItem = null;
                        if (item.Type == WorkType.Submit)
                        {
                            ReturnSubmitItem(item);
                        }
                        continue;
                    }

                    long workerWorkStart = GezinjinxPerformanceMode.DetailedTelemetry ? Stopwatch.GetTimestamp() : 0;

                    try
                    {
                        switch (item.Type)
                        {
                            case WorkType.Submit:
                                ProcessSubmit(item);
                                if (GezinjinxPerformanceMode.DetailedTelemetry)
                                {
                                    GezinjinxEfficiencyTelemetry.RecordSubmit();
                                    GezinjinxGuestStallTelemetry.RecordVkSubmit(item.TimelineValue, item.Sequence);
                                }

                                item.Complete();
                                break;
                            case WorkType.Present:
                                item.PresentResult = ProcessPresent(item);
                                if (GezinjinxPerformanceMode.DetailedTelemetry)
                                {
                                    GezinjinxEfficiencyTelemetry.RecordPresent();
                                    GezinjinxGuestStallTelemetry.RecordPresent(item.Sequence);
                                }

                                item.Complete();
                                break;
                            case WorkType.BindSparse:
                                ProcessBindSparse(item);
                                item.Complete();
                                break;
                            case WorkType.Drain:
                                item.Complete();
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        SetFault(ex, item);
                        int queueDepth;
                        lock (_sync)
                        {
                            queueDepth = _workQueue.Count;
                        }
                        string operation = ex is VulkanSubmissionDeviceLostException deviceLost
                            ? deviceLost.Operation
                            : item.Type.ToString();
                        ReadbackDecisionTelemetry.RecordSubmissionFault(operation, ex.GetType().Name + ":" + ex.Message, item.TimelineValue, item.Sequence, queueDepth);
                        item.Fail(ex);
                    }

                    if (GezinjinxPerformanceMode.DetailedTelemetry)
                    {
                        GezinjinxEfficiencyTelemetry.RecordSubmitWorkerWork(Stopwatch.GetTimestamp() - workerWorkStart);
                        GezinjinxEfficiencyTelemetry.TryLog();
                    }

                    _activeItem = null;
                    if (item.Type == WorkType.Submit)
                    {
                        ReturnSubmitItem(item);
                    }

                    BaselineTelemetry.TryFlush();
                    TextureFormatTelemetry.TryFlush();
                    ReadbackDecisionTelemetry.TryLogSnapshot();
                    PipelineCachePersistence.TryPeriodicSave();
                }
            }
        }

        // Only already queued, adjacent submits are batched. Never delay a submit to
        // fill a batch or cross Present/Drain/BindSparse/Stop. Each timeline identity
        // remains attached to its original command buffer and FenceHolder.
        // NEOV2 Etapa 14: agrupamento ligado. Com timeline, cada FenceHolder usa o valor da
        // timeline (sem VkFence), entao varios envios podem sair em uma unica chamada.
        private const bool EnableBatching = true;
        private static bool _batchingLogged;
        private readonly WorkItem[] _submitBatch = new WorkItem[TimelineBatchEncoding.MaxItems];

        private unsafe void ProcessSubmit(WorkItem item)
        {
            long started = Stopwatch.GetTimestamp();
            if (!EnableBatching || !_timelineEnabled || item.WaitSemaphoreCount + item.SignalSemaphoreCount > TimelineBatchEncoding.MaxSemaphores)
            {
                ProcessSubmitSingle(item);
                SubmissionTelemetry.Submitted(1, Stopwatch.GetTimestamp() - started, item.TimelineValue);
                return;
            }

            int count = 1;
            int waits = item.WaitSemaphoreCount;
            int signals = item.SignalSemaphoreCount;
            _submitBatch[0] = item;
            try
            {
                lock (_sync)
                {
                    while (_workQueue.TryPeek(out WorkItem next) &&
                           TimelineBatchEncoding.CanAppend(_timelineEnabled, next.Type == WorkType.Submit,
                               count, waits + signals + next.WaitSemaphoreCount + next.SignalSemaphoreCount))
                    {
                        _submitBatch[count++] = _workQueue.Dequeue();
                        waits += next.WaitSemaphoreCount;
                        signals += next.SignalSemaphoreCount;
                    }
                }

                SubmitInfo* submits = stackalloc SubmitInfo[count];
                TimelineSemaphoreSubmitInfo* timelines = stackalloc TimelineSemaphoreSubmitInfo[count];
                CommandBuffer* buffers = stackalloc CommandBuffer[count];
                Semaphore* waitSemaphores = stackalloc Semaphore[waits];
                Semaphore* signalSemaphores = stackalloc Semaphore[signals];
                PipelineStageFlags* stages = stackalloc PipelineStageFlags[waits];
                ulong* waitValues = stackalloc ulong[waits];
                ulong* signalValues = stackalloc ulong[signals];
                int wi = 0;
                int si = 0;
                int groupCount = 0;
                bool groupOnlyTimeline = false;
                ulong previous = 0;
                for (int i = 0; i < count; i++)
                {
                    WorkItem entry = _submitBatch[i];
                    if (entry.TimelineValue <= previous)
                    {
                        throw new InvalidOperationException("Non-increasing timeline in submission batch.");
                    }
                    previous = entry.TimelineValue;
                    buffers[i] = entry.CommandBuffer;

                    // NEOV2 Etapa 14: um envio sem espera, vindo logo apos um grupo que so sinaliza a
                    // timeline, entra no mesmo VkSubmitInfo. O grupo passa a sinalizar o valor maior,
                    // que tambem satisfaz quem espera pelo valor anterior (a timeline so cresce).
                    bool merge = groupCount > 0 && groupOnlyTimeline && entry.WaitSemaphoreCount == 0;

                    if (merge)
                    {
                        int g = groupCount - 1;

                        // Remove o sinal de timeline do grupo (unico sinal, ultimo escrito).
                        si -= (int)submits[g].SignalSemaphoreCount;

                        entry.SignalSemaphores.AsSpan(0, entry.SignalSemaphoreCount).CopyTo(new Span<Semaphore>(signalSemaphores + si, entry.SignalSemaphoreCount));
                        TimelineBatchEncoding.WriteValues(Span<ulong>.Empty,
                            new Span<ulong>(signalValues + si, entry.SignalSemaphoreCount), entry.TimelineValue);

                        submits[g].CommandBufferCount++;
                        submits[g].SignalSemaphoreCount = (uint)entry.SignalSemaphoreCount;
                        submits[g].PSignalSemaphores = signalSemaphores + si;
                        timelines[g].SignalSemaphoreValueCount = (uint)entry.SignalSemaphoreCount;
                        timelines[g].PSignalSemaphoreValues = signalValues + si;

                        si += entry.SignalSemaphoreCount;
                    }
                    else
                    {
                        int g = groupCount++;

                        entry.WaitSemaphores.AsSpan(0, entry.WaitSemaphoreCount).CopyTo(new Span<Semaphore>(waitSemaphores + wi, entry.WaitSemaphoreCount));
                        entry.WaitStages.AsSpan(0, entry.WaitSemaphoreCount).CopyTo(new Span<PipelineStageFlags>(stages + wi, entry.WaitSemaphoreCount));
                        entry.SignalSemaphores.AsSpan(0, entry.SignalSemaphoreCount).CopyTo(new Span<Semaphore>(signalSemaphores + si, entry.SignalSemaphoreCount));
                        TimelineBatchEncoding.WriteValues(new Span<ulong>(waitValues + wi, entry.WaitSemaphoreCount),
                            new Span<ulong>(signalValues + si, entry.SignalSemaphoreCount), entry.TimelineValue);
                        timelines[g] = new TimelineSemaphoreSubmitInfo
                        {
                            SType = StructureType.TimelineSemaphoreSubmitInfo,
                            WaitSemaphoreValueCount = (uint)entry.WaitSemaphoreCount,
                            PWaitSemaphoreValues = waitValues + wi,
                            SignalSemaphoreValueCount = (uint)entry.SignalSemaphoreCount,
                            PSignalSemaphoreValues = signalValues + si,
                        };
                        submits[g] = new SubmitInfo
                        {
                            SType = StructureType.SubmitInfo,
                            PNext = timelines + g,
                            WaitSemaphoreCount = (uint)entry.WaitSemaphoreCount,
                            PWaitSemaphores = waitSemaphores + wi,
                            PWaitDstStageMask = stages + wi,
                            CommandBufferCount = 1,
                            PCommandBuffers = buffers + i,
                            SignalSemaphoreCount = (uint)entry.SignalSemaphoreCount,
                            PSignalSemaphores = signalSemaphores + si,
                        };
                        wi += entry.WaitSemaphoreCount;
                        si += entry.SignalSemaphoreCount;
                    }

                    // So a timeline (ultimo sinal) e nenhum semaforo binario: o proximo pode entrar no grupo.
                    groupOnlyTimeline = entry.SignalSemaphoreCount == 1;
                }

                if (!_batchingLogged && count > 1)
                {
                    _batchingLogged = true;
                    Logger.Info?.Print(LogClass.Gpu, $"[NEOV2-ETAPA14] Submission batching active (first batch: {count} submits in {groupCount} submit infos)");
                }

                Result result;
                long queueSubmitStart = Stopwatch.GetTimestamp();
                lock (_queueLock)
                {
                    result = _api.QueueSubmit(_queue, (uint)groupCount, submits, default);
                }
                BaselineTelemetry.RecordSubmit(Stopwatch.GetTimestamp() - queueSubmitStart);
                if (result == Result.ErrorDeviceLost)
                {
                    throw new VulkanSubmissionDeviceLostException("QueueSubmitBatch", item.TimelineValue, item.Sequence);
                }
                result.ThrowOnError();
                SubmissionTelemetry.Submitted(count, Stopwatch.GetTimestamp() - started, previous);
                // Completion here only means the host submission returned. GPU
                // completion still uses each original timeline value through A9.
                for (int i = 1; i < count; i++)
                {
                    _submitBatch[i].Complete();
                }
            }
            catch (Exception error)
            {
                SetFault(error, item);
                for (int i = 1; i < count; i++)
                {
                    _submitBatch[i].Fail(error);
                }
                throw;
            }
            finally
            {
                for (int i = 1; i < count; i++)
                {
                    ReturnSubmitItem(_submitBatch[i]);
                }
                Array.Clear(_submitBatch, 0, count);
            }
        }

        private unsafe void ProcessSubmitSingle(WorkItem item)
        {
            long queueSubmitStart = GezinjinxPerformanceMode.DetailedTelemetry ? Stopwatch.GetTimestamp() : 0;
            var commandBuffer = item.CommandBuffer;

            fixed (Semaphore* pWaitSemaphores = item.WaitSemaphores, pSignalSemaphores = item.SignalSemaphores)
            fixed (PipelineStageFlags* pWaitStages = item.WaitStages)
            {
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    WaitSemaphoreCount = (uint)item.WaitSemaphoreCount,
                    PWaitSemaphores = pWaitSemaphores,
                    PWaitDstStageMask = pWaitStages,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                    SignalSemaphoreCount = (uint)item.SignalSemaphoreCount,
                    PSignalSemaphores = pSignalSemaphores,
                };

                TimelineSemaphoreSubmitInfo timelineInfo = default;

                if (_timelineEnabled)
                {
                    ulong* pWaitValues = stackalloc ulong[item.WaitSemaphoreCount];
                    ulong* pSignalValues = stackalloc ulong[item.SignalSemaphoreCount];

                    new Span<ulong>(pWaitValues, item.WaitSemaphoreCount).Clear();
                    new Span<ulong>(pSignalValues, item.SignalSemaphoreCount).Clear();
                    pSignalValues[item.SignalSemaphoreCount - 1] = item.TimelineValue;

                    timelineInfo = new TimelineSemaphoreSubmitInfo
                    {
                        SType = StructureType.TimelineSemaphoreSubmitInfo,
                        WaitSemaphoreValueCount = (uint)item.WaitSemaphoreCount,
                        PWaitSemaphoreValues = pWaitValues,
                        SignalSemaphoreValueCount = (uint)item.SignalSemaphoreCount,
                        PSignalSemaphoreValues = pSignalValues,
                    };

                    submitInfo.PNext = &timelineInfo;

                    SubmitToQueue(in submitInfo, item);
                }
                else
                {
                    SubmitToQueue(in submitInfo, item);
                }
            }

            if (GezinjinxPerformanceMode.DetailedTelemetry)
            {
                ReadbackDecisionTelemetry.RecordQueueSubmit(
                    Stopwatch.GetTimestamp() - queueSubmitStart,
                    item.TimelineValue);
            }
        }

        private unsafe void SubmitToQueue(in SubmitInfo submitInfo, WorkItem item)
        {
            Result result;
            long submitStart = Stopwatch.GetTimestamp();
            lock (_queueLock)
            {
                result = _api.QueueSubmit(_queue, 1, in submitInfo, item.Fence);
            }
            long submitElapsed = Stopwatch.GetTimestamp() - submitStart;
            BaselineTelemetry.RecordSubmit(submitElapsed);

            if (result == Result.ErrorDeviceLost)
            {
                throw new VulkanSubmissionDeviceLostException("QueueSubmit", item.TimelineValue, item.Sequence);
            }

            result.ThrowOnError();
        }

        private unsafe Result ProcessPresent(WorkItem item)
        {
            long started = Stopwatch.GetTimestamp();
            long queuePresentStart = GezinjinxPerformanceMode.DetailedTelemetry ? Stopwatch.GetTimestamp() : 0;
            Semaphore* pWait = stackalloc Semaphore[1];
            SwapchainKHR* pSwapchain = stackalloc SwapchainKHR[1];
            uint* pImageIndex = stackalloc uint[1];

            pWait[0] = item.PresentWaitSemaphore;
            pSwapchain[0] = item.Swapchain;
            pImageIndex[0] = item.ImageIndex;

            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = pWait,
                SwapchainCount = 1,
                PSwapchains = pSwapchain,
                PImageIndices = pImageIndex,
                PResults = null,
            };

            Result result;
            lock (_queueLock)
            {
                result = item.SwapchainApi.QueuePresent(_queue, in presentInfo);
            }

            if (result == Result.ErrorDeviceLost)
            {
                throw new VulkanSubmissionDeviceLostException("QueuePresent", item.TimelineValue, item.Sequence);
            }

            if (result == Result.Success || result == Result.SuboptimalKhr)
            {
                SubmissionTelemetry.Presented(Stopwatch.GetTimestamp() - started);
            }

            if (GezinjinxPerformanceMode.DetailedTelemetry)
            {
                ReadbackDecisionTelemetry.RecordQueuePresent(
                    Stopwatch.GetTimestamp() - queuePresentStart,
                    item.Sequence);
            }

            return result;
        }

        private unsafe void ProcessBindSparse(WorkItem item)
        {
            long queueBindSparseStart = GezinjinxPerformanceMode.DetailedTelemetry ? Stopwatch.GetTimestamp() : 0;

            fixed (SparseMemoryBind* pMemoryBinds = item.SparseMemoryBinds)
            {
                SparseBufferMemoryBindInfo bufferBind = new()
                {
                    Buffer = item.SparseBuffer,
                    BindCount = (uint)item.SparseMemoryBinds.Length,
                    PBinds = pMemoryBinds,
                };

                BindSparseInfo bindSparseInfo = new()
                {
                    SType = StructureType.BindSparseInfo,
                    BufferBindCount = 1,
                    PBufferBinds = &bufferBind,
                };

                Result result;
                lock (_queueLock)
                {
                    result = _api.QueueBindSparse(_queue, 1, in bindSparseInfo, default);
                }

                if (result == Result.ErrorDeviceLost)
                {
                    throw new VulkanSubmissionDeviceLostException("QueueBindSparse", 0, item.Sequence);
                }

                result.ThrowOnError();
            }

            if (GezinjinxPerformanceMode.DetailedTelemetry)
            {
                ReadbackDecisionTelemetry.RecordQueueBindSparse(
                    Stopwatch.GetTimestamp() - queueBindSparseStart,
                    item.Sequence);
            }
        }

        public unsafe void Dispose()
        {
            WorkItem stop = null;
            bool disposeOwner = false;

            lock (_submissionGate)
            {
                lock (_sync)
                {
                    if (_state is not WorkerState.Stopping and not WorkerState.Stopped and not WorkerState.Disposed)
                    {
                        disposeOwner = true;
                        ReadbackDecisionTelemetry.RecordSubmissionStateTransition(
                            _state.ToString(),
                            WorkerState.Stopping.ToString(),
                            (ulong)Volatile.Read(ref _nextSequence));
                        _state = WorkerState.Stopping;
                        if (!_workerExited)
                        {
                            stop = new WorkItem
                            {
                                Type = WorkType.Stop,
                                Completion = new VulkanSubmissionHandle(),
                                Boundary = VulkanSubmissionBoundary.Shutdown,
                            };
                            if (EnqueueLocked(stop, allowFaulted: true))
                            {
                                _wakeEvent.Set();
                            }
                        }
                    }
                }
            }

            if (!disposeOwner)
            {
                _disposeCompletion.Wait();
                return;
            }

            ExceptionDispatchInfo disposeError = null;

            VulkanTeardown.Run(ref disposeError, () => stop?.Completion.Wait());
            VulkanTeardown.Run(ref disposeError, _thread.Join);

            if (_timelineSemaphore.Handle != 0)
            {
                VulkanTeardown.Run(ref disposeError, () =>
                {
                    _api.DestroySemaphore(_device, _timelineSemaphore, null);
                    _timelineSemaphore = default;
                });
            }

            VulkanTeardown.Run(ref disposeError, _wakeEvent.Dispose);

            lock (_sync)
            {
                ReadbackDecisionTelemetry.RecordSubmissionStateTransition(
                    _state.ToString(),
                    WorkerState.Disposed.ToString(),
                    (ulong)Volatile.Read(ref _nextSequence));
                _state = WorkerState.Disposed;
            }

            _disposeCompletion.Complete(disposeError?.SourceException);

            if (disposeError != null)
            {
                disposeError.Throw();
            }
        }
    }
}
