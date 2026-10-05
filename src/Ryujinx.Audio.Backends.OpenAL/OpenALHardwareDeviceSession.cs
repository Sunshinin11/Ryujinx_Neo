using OpenTK.Audio.OpenAL;
using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Audio.Backends.OpenAL
{
    // ReSharper disable once InconsistentNaming
    sealed class OpenALHardwareDeviceSession : HardwareDeviceSessionOutputBase
    {
        private readonly OpenALHardwareDeviceDriver _driver;
        private readonly int _sourceId;
        private readonly ALFormat _targetFormat;
        private bool _isActive;
        private readonly Queue<OpenALAudioBuffer> _queuedBuffers;
        private ulong _playedSampleCount;
        private float _volume;

        // NEOV2-AUDIO2: jitter buffer state.
        private ulong _queuedSampleCount;
        private long _stallStartTimestamp = -1;

        private readonly Lock _lock = new();

        public OpenALHardwareDeviceSession(OpenALHardwareDeviceDriver driver, IVirtualMemoryManager memoryManager, SampleFormat requestedSampleFormat, uint requestedSampleRate, uint requestedChannelCount) : base(memoryManager, requestedSampleFormat, requestedSampleRate, requestedChannelCount)
        {
            _driver = driver;
            _queuedBuffers = new Queue<OpenALAudioBuffer>();
            _sourceId = AL.GenSource();
            _targetFormat = GetALFormat();
            _isActive = false;
            _playedSampleCount = 0;
            SetVolume(1f);
        }

        private ALFormat GetALFormat()
        {
            return RequestedSampleFormat switch
            {
                SampleFormat.PcmInt16 => RequestedChannelCount switch
                {
                    1 => ALFormat.Mono16,
                    2 => ALFormat.Stereo16,
                    6 => ALFormat.Multi51Chn16Ext,
                    _ => throw new NotImplementedException($"Unsupported channel config {RequestedChannelCount}"),
                },
                _ => throw new NotImplementedException($"Unsupported sample format {RequestedSampleFormat}"),
            };
        }

        public override void PrepareToClose() { }

        /// <summary>
        /// NEOV2-AUDIO2: starts (or restarts after an underrun) only once enough audio is queued. Starting on the
        /// first 5 ms buffer left the source with less audio than one OpenAL mixing period, so it ran dry on
        /// every period: gaps, clicks and stutter.
        /// </summary>
        private void TryStartPlayback()
        {
            if (!_isActive || _queuedBuffers.Count == 0)
            {
                _stallStartTimestamp = -1;

                return;
            }

            AL.GetSource(_sourceId, ALGetSourcei.SourceState, out int stateInt);

            if ((ALSourceState)stateInt == ALSourceState.Playing)
            {
                _stallStartTimestamp = -1;

                return;
            }

            long now = Stopwatch.GetTimestamp();

            if (_stallStartTimestamp < 0)
            {
                _stallStartTimestamp = now;
            }

            ulong threshold = (ulong)RequestedSampleRate * (ulong)Math.Max(0, OpenALHardwareDeviceDriver.PrerollMilliseconds) / 1000;
            bool timedOut = (now - _stallStartTimestamp) * 1000 >= (long)Math.Max(0, OpenALHardwareDeviceDriver.PrerollTimeoutMilliseconds) * Stopwatch.Frequency;

            if (_queuedSampleCount < threshold && !timedOut)
            {
                return;
            }

            // A stopped source must be rewound first, or its new buffers count as already played.
            RecoverStoppedSource();

            if (_queuedBuffers.Count == 0)
            {
                return;
            }

            AL.SourcePlay(_sourceId);
            _stallStartTimestamp = -1;

        }

        /// <summary>
        /// NEOV2-AUDIO3: while a source is STOPPED (after an underrun or Stop), OpenAL Soft reports every queued
        /// buffer as processed, including buffers queued after the stop. Releasing them threw away audio that was
        /// never played, so after the first underrun nothing was ever played again (no sound). Release what is
        /// in the queue now (it played before the stop) and rewind to INITIAL, where new buffers count as pending.
        /// </summary>
        private int RecoverStoppedSource()
        {
            AL.GetSource(_sourceId, ALGetSourcei.SourceState, out int stateInt);

            if ((ALSourceState)stateInt != ALSourceState.Stopped)
            {
                return 0;
            }

            int released = ReleaseProcessedBuffers();

            AL.SourceRewind(_sourceId);

            return released;
        }

        private int ReleaseProcessedBuffers()
        {
            AL.GetSource(_sourceId, ALGetSourcei.BuffersProcessed, out int releasedCount);

            if (releasedCount <= 0)
            {
                return 0;
            }

            int[] bufferIds = new int[releasedCount];

            AL.SourceUnqueueBuffers(_sourceId, releasedCount, bufferIds);

            int i = 0;

            while (i < bufferIds.Length && _queuedBuffers.TryPeek(out OpenALAudioBuffer buffer) && buffer.BufferId == bufferIds[i])
            {
                _playedSampleCount += buffer.SampleCount;
                _queuedSampleCount -= Math.Min(_queuedSampleCount, buffer.SampleCount);

                _queuedBuffers.TryDequeue(out _);

                i++;
            }

            Debug.Assert(i == bufferIds.Length, "Unknown buffer ids found!");

            AL.DeleteBuffers(bufferIds);

            return releasedCount;
        }

        public override void QueueBuffer(AudioBuffer buffer)
        {
            lock (_lock)
            {
                // NEOV2-AUDIO3: never append to a STOPPED source; the new buffer would be reported as played.
                RecoverStoppedSource();

                OpenALAudioBuffer driverBuffer = new()
                {
                    DriverIdentifier = buffer.DataPointer,
                    BufferId = AL.GenBuffer(),
                    SampleCount = GetSampleCount(buffer),
                };

                AL.BufferData(driverBuffer.BufferId, _targetFormat, buffer.Data, (int)RequestedSampleRate);

                _queuedBuffers.Enqueue(driverBuffer);
                _queuedSampleCount += driverBuffer.SampleCount;

                AL.SourceQueueBuffer(_sourceId, driverBuffer.BufferId);

                if (_isActive)
                {
                    TryStartPlayback();
                }
            }
        }

        public override void SetVolume(float volume)
        {
            _volume = volume;

            UpdateMasterVolume(_driver.Volume);
        }

        public override float GetVolume()
        {
            return _volume;
        }

        public void UpdateMasterVolume(float newVolume)
        {
            lock (_lock)
            {
                AL.Source(_sourceId, ALSourcef.Gain, newVolume * _volume);
            }
        }

        public override void Start()
        {
            lock (_lock)
            {
                _isActive = true;

                TryStartPlayback();
            }
        }

        public override void Stop()
        {
            lock (_lock)
            {
                SetVolume(0.0f);

                AL.SourceStop(_sourceId);

                _isActive = false;
                _stallStartTimestamp = -1;
            }
        }

        public override void UnregisterBuffer(AudioBuffer buffer) { }

        public override bool WasBufferFullyConsumed(AudioBuffer buffer)
        {
            lock (_lock)
            {
                if (!_queuedBuffers.TryPeek(out OpenALAudioBuffer driverBuffer))
                {
                    return true;
                }

                return driverBuffer.DriverIdentifier != buffer.DataPointer;
            }
        }

        public override ulong GetPlayedSampleCount()
        {
            lock (_lock)
            {
                return _playedSampleCount;
            }
        }

        public bool Update()
        {
            lock (_lock)
            {
                if (_isActive)
                {
                    int releasedCount = RecoverStoppedSource();

                    releasedCount += ReleaseProcessedBuffers();

                    TryStartPlayback();

                    return releasedCount > 0;
                }

                return false;
            }
        }

        private void Dispose(bool disposing)
        {
            if (disposing && _driver.Unregister(this))
            {
                lock (_lock)
                {
                    PrepareToClose();
                    Stop();

                    AL.DeleteSource(_sourceId);
                }
            }
        }

        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
