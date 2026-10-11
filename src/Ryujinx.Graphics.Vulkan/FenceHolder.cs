using Silk.NET.Vulkan;
using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    class FenceHolder : IDisposable
    {
        private readonly Vk _api;
        private readonly Device _device;
        private readonly VulkanSubmissionWorker _submissionWorker;
        private readonly bool _usesTimeline;
        private Fence _fence;
        private ulong _timelineValue;
        private int _referenceCount;
        private int _lock;
        private readonly bool _concurrentWaitUnsupported;
        private readonly bool _alwaysWaits;
        private bool _disposed;
        private readonly VulkanSubmissionHandle _submission = new();

        internal VulkanSubmissionHandle Submission => _submission;
        internal bool IsTimeline => _usesTimeline;
        internal VulkanSubmissionWorker SubmissionWorker => _submissionWorker;
        internal ulong TimelineValue => Volatile.Read(ref _timelineValue);

        public unsafe FenceHolder(
            Vk api,
            Device device,
            bool concurrentWaitUnsupported,
            VulkanSubmissionWorker submissionWorker,
            bool alwaysWaits = false)
        {
            _api = api;
            _device = device;
            _submissionWorker = submissionWorker;
            _concurrentWaitUnsupported = concurrentWaitUnsupported;
            _alwaysWaits = alwaysWaits;

            // NEOV2 Etapa 2: quando o worker usa timeline semaphore, a conclusao e
            // verificada pelo contador do timeline e nenhum VkFence e criado.
            _usesTimeline = submissionWorker.TimelineEnabled;

            if (!_usesTimeline)
            {
                FenceCreateInfo fenceCreateInfo = new()
                {
                    SType = StructureType.FenceCreateInfo,
                };

                api.CreateFence(device, in fenceCreateInfo, null, out _fence).ThrowOnError();
            }

            _referenceCount = 1;
        }

        internal void PrepareTimeline(ulong value)
        {
            if (!_usesTimeline || value == 0 || _timelineValue != 0)
            {
                throw new InvalidOperationException("Invalid timeline completion assignment.");
            }

            Volatile.Write(ref _timelineValue, value);
        }

        internal Fence GetSubmitFence()
        {
            return _usesTimeline ? default : _fence;
        }

        public Fence GetUnsafe()
        {
            return _fence;
        }

        internal bool TryAddReference()
        {
            int lastValue;
            do
            {
                lastValue = _referenceCount;

                if (lastValue == 0)
                {
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _referenceCount, lastValue + 1, lastValue) != lastValue);

            return true;
        }

        public bool TryGet(out Fence fence)
        {
            if (_usesTimeline)
            {
                fence = default;
                return false;
            }

            if (!TryAddReference())
            {
                fence = default;
                return false;
            }

            try
            {
                _submission.Wait();
            }
            catch
            {
                Put();
                throw;
            }

            if (_concurrentWaitUnsupported)
            {
                AcquireLock();
            }

            fence = _fence;
            return true;
        }

        public Fence Get()
        {
            Interlocked.Increment(ref _referenceCount);
            return _fence;
        }

        public void PutLock()
        {
            Put();

            if (_concurrentWaitUnsupported)
            {
                ReleaseLock();
            }
        }

        public void Put()
        {
            if (Interlocked.Decrement(ref _referenceCount) == 0)
            {
                if (!_usesTimeline && _fence.Handle != 0)
                {
                    _api.DestroyFence(_device, _fence, Span<AllocationCallbacks>.Empty);
                    _fence = default;
                }
            }
        }

        private void AcquireLock()
        {
            while (!TryAcquireLock())
            {
                Thread.SpinWait(32);
            }
        }

        private bool TryAcquireLock()
        {
            return Interlocked.Exchange(ref _lock, 1) == 0;
        }

        private void ReleaseLock()
        {
            Interlocked.Exchange(ref _lock, 0);
        }

        public void Wait()
        {
            long start = Stopwatch.GetTimestamp();

            _submission.Wait();

            if (_usesTimeline)
            {
                _submissionWorker.WaitTimeline(TimelineValue, ulong.MaxValue);
            }
            else if (_concurrentWaitUnsupported)
            {
                AcquireLock();

                try
                {
                    FenceHelper.WaitAllIndefinitely(_api, _device, [_fence]);
                }
                finally
                {
                    ReleaseLock();
                }
            }
            else
            {
                FenceHelper.WaitAllIndefinitely(_api, _device, [_fence]);
            }

            long elapsed = Stopwatch.GetTimestamp() - start;
            BaselineTelemetry.RecordWait(elapsed);
        }

        public bool Wait(ulong timeout)
        {
            if (timeout == 0 && !_submission.IsCompleted)
            {
                return false;
            }

            _submission.Wait();

            if (_usesTimeline)
            {
                return _submissionWorker.WaitTimeline(TimelineValue, timeout);
            }

            if (_concurrentWaitUnsupported)
            {
                AcquireLock();

                try
                {
                    return FenceHelper.AllSignaled(_api, _device, [_fence], timeout);
                }
                finally
                {
                    ReleaseLock();
                }
            }

            return FenceHelper.AllSignaled(_api, _device, [_fence], timeout);
        }

        public bool IsSignaledLazy()
        {
            // O workaround do Turnip existe para evitar vkWaitForFences com timeout zero.
            // Com timeline, a consulta e vkGetSemaphoreCounterValue, entao ele nao se aplica.
            if (_alwaysWaits && !_usesTimeline)
            {
                return false;
            }

            // NEOV2 TIMELINE-LAZY1:
            // no Turnip evita consultar a timeline emulada pelo driver neste caminho oportunista.
            if (_alwaysWaits && _usesTimeline && Ryujinx.Common.Diagnostics.NeoGraphicsSettings.TimelineLazyEnabled)
            {
                ulong value = TimelineValue;

                return _submission.IsCompleted &&
                       TimelineCompletionGenerationCache.IsKnownComplete(_submissionWorker, value);
            }

            return IsSignaled();
        }

        public bool IsSignaled()
        {
            if (!_submission.IsCompleted)
            {
                return false;
            }

            _submission.ThrowIfFaulted();

            if (_usesTimeline)
            {
                bool reached = _submissionWorker.IsTimelineReached(TimelineValue);

                if (reached)
                {
                    BaselineTelemetry.RecordWaitAlreadySignaled();
                }

                return reached;
            }

            bool signaled;

            if (_concurrentWaitUnsupported)
            {
                if (!TryAcquireLock())
                {
                    return false;
                }

                try
                {
                    signaled = FenceHelper.AllSignaled(_api, _device, [_fence]);
                }
                finally
                {
                    ReleaseLock();
                }
            }
            else
            {
                signaled = FenceHelper.AllSignaled(_api, _device, [_fence]);
            }

            if (signaled)
            {
                BaselineTelemetry.RecordWaitAlreadySignaled();
            }

            return signaled;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Put();
                _disposed = true;
            }
        }
    }
}