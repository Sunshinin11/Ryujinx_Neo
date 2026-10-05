using Ryujinx.Common.Diagnostics;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Ryujinx.Graphics.Vulkan
{
    internal class AutoFlushCounter
    {
        // How often to flush on framebuffer change.
        private readonly static long _framebufferFlushTimer = Stopwatch.Frequency / 1000; // (1ms)

        // How often to flush on draw when fast flush mode is enabled.
        private readonly static long _drawFlushTimer = Stopwatch.Frequency / 666; // (1.5ms)

        // Average wait time that triggers fast flush mode to be entered.
        private readonly static long _fastFlushEnterThreshold = Stopwatch.Frequency / 666; // (1.5ms)

        // Average wait time that triggers fast flush mode to be exited.
        private readonly static long _fastFlushExitThreshold = Stopwatch.Frequency / 10000; // (0.1ms)

        // Turnip recovers from the NCE-driven fast-flush state sooner.
        private readonly static long _turnipFastFlushExitThreshold = Stopwatch.Frequency * 3 / 4000; // (0.75ms)

        // Number of frames to average waiting times over.
        private const int SyncWaitAverageCount = 20;

        private const int MinDrawCountForFlush = 10;
        private const int MaxDrawFlushesPerFrame = 4;
        private const int MinConsecutiveQueryForFlush = 10;
        private const int InitialQueryCountForFlush = 32;

        private readonly VulkanRenderer _gd;

        private long _lastFlush;
        private ulong _lastDrawCount;
        private bool _hasPendingQuery;
        private int _consecutiveQueries;
        private int _queryCount;

        private readonly int[] _queryCountHistory = new int[3];
        private int _queryCountHistoryIndex;
        private int _remainingQueries;

        private readonly long[] _syncWaitHistory = new long[SyncWaitAverageCount];
        private int _syncWaitHistoryIndex;

        private bool _fastFlushMode;
        private int _drawFlushesThisFrame;
        private int _drawFlushesSuppressed;
        private readonly DrawFlushMode _drawFlushMode = NeoGraphicsSettings.DrawFlushMode;

        // NEOV2 Etapa 16: intervalos originais restaurados (a Etapa 15 piorou o TOTK).
        // Contadores de flush por motivo, registrados a cada 5 s. Flushes forcados por
        // FlushAllCommands sao separados por origem (arquivo.metodo).
        private static readonly long _statsInterval = Stopwatch.Frequency * 5;
        private static readonly ConcurrentDictionary<string, int> _externalOrigins = new();
        private long _statsStart = Stopwatch.GetTimestamp();
        private int _flushTotal;
        private int _flushAttachment;
        private int _flushDraw;
        private int _flushQuery;
        private int _presents;
        private int _fastFlushFrames;

        public AutoFlushCounter(VulkanRenderer gd)
        {
            _gd = gd;
        }

        public static void RecordExternalFlush(string callerFile, string callerMember)
        {
            if (!GezinjinxPerformanceMode.DetailedTelemetry) return;

            string file = string.IsNullOrEmpty(callerFile) ? "?" : Path.GetFileNameWithoutExtension(callerFile.Replace('\\', '/'));
            string key = file + "." + callerMember;

            _externalOrigins.AddOrUpdate(key, 1, (_, count) => count + 1);
        }

        public void RegisterFlush(ulong drawCount)
        {
            _lastFlush = Stopwatch.GetTimestamp();
            _lastDrawCount = drawCount;

            _hasPendingQuery = false;
            _consecutiveQueries = 0;

            _flushTotal++;
        }

        public bool RegisterPendingQuery()
        {
            _hasPendingQuery = true;
            _consecutiveQueries++;
            _remainingQueries--;

            _queryCountHistory[_queryCountHistoryIndex]++;

            // Interrupt render passes to flush queries, so that early results arrive sooner.
            if (++_queryCount == InitialQueryCountForFlush)
            {
                _flushQuery++;
                return true;
            }

            return false;
        }

        public int GetRemainingQueries()
        {
            if (_remainingQueries <= 0)
            {
                _remainingQueries = 16;
            }

            if (_queryCount < InitialQueryCountForFlush)
            {
                return Math.Min(InitialQueryCountForFlush - _queryCount, _remainingQueries);
            }

            return _remainingQueries;
        }

        public bool ShouldFlushQuery()
        {
            if (_hasPendingQuery)
            {
                _flushQuery++;
            }

            return _hasPendingQuery;
        }

        public bool ShouldFlushDraw(ulong drawCount)
        {
            if (_fastFlushMode)
            {
                // NEOV2 NCE R3: a fast guest thread can otherwise turn a GPU wait into a
                // feedback loop that flushes dozens of times per frame. Keep enough flushes
                // to make progress without fragmenting every large draw sequence.
                if (_drawFlushMode == DrawFlushMode.Disabled)
                {
                    _drawFlushesSuppressed++;
                    return false;
                }

                if (_drawFlushMode == DrawFlushMode.Reduced)
                {
                    if (_drawFlushesThisFrame >= 1)
                    {
                        _drawFlushesSuppressed++;
                        return false;
                    }
                }
                else if (_gd.IsTurnip && _drawFlushesThisFrame >= MaxDrawFlushesPerFrame)
                {
                    _drawFlushesSuppressed++;
                    return false;
                }

                long draws = (long)(drawCount - _lastDrawCount);

                if (draws < MinDrawCountForFlush)
                {
                    if (draws == 0)
                    {
                        _lastFlush = Stopwatch.GetTimestamp();
                    }

                    return false;
                }

                long flushTimeout = _drawFlushTimer;

                long now = Stopwatch.GetTimestamp();

                bool flush = now > _lastFlush + flushTimeout;

                if (flush)
                {
                    _flushDraw++;
                    _drawFlushesThisFrame++;
                }

                return flush;
            }

            return false;
        }

        public bool ShouldFlushAttachmentChange(ulong drawCount)
        {
            _queryCount = 0;

            // Flush when there's an attachment change out of a large block of queries.
            if (_consecutiveQueries > MinConsecutiveQueryForFlush)
            {
                _flushQuery++;
                return true;
            }

            _consecutiveQueries = 0;

            long draws = (long)(drawCount - _lastDrawCount);

            if (draws < MinDrawCountForFlush)
            {
                if (draws == 0)
                {
                    _lastFlush = Stopwatch.GetTimestamp();
                }

                return false;
            }

            long flushTimeout = _framebufferFlushTimer;

            long now = Stopwatch.GetTimestamp();

            bool flush = now > _lastFlush + flushTimeout;

            if (flush)
            {
                _flushAttachment++;
            }

            return flush;
        }

        public void Present()
        {
            _drawFlushesThisFrame = 0;

            // Query flush prediction.

            _queryCountHistoryIndex = (_queryCountHistoryIndex + 1) % 3;

            _remainingQueries = _queryCountHistory.Max() + 10;

            _queryCountHistory[_queryCountHistoryIndex] = 0;

            // Fast flush mode toggle.

            _syncWaitHistory[_syncWaitHistoryIndex] = _gd.SyncManager.GetAndResetWaitTicks();

            _syncWaitHistoryIndex = (_syncWaitHistoryIndex + 1) % SyncWaitAverageCount;

            long averageWait = (long)_syncWaitHistory.Average();

            long exitThreshold = _gd.IsTurnip ? _turnipFastFlushExitThreshold : _fastFlushExitThreshold;

            if (_fastFlushMode ? averageWait < exitThreshold : averageWait > _fastFlushEnterThreshold)
            {
                _fastFlushMode = !_fastFlushMode;
                Logger.Debug?.PrintMsg(LogClass.Gpu, $"Switched fast flush mode: ({_fastFlushMode})");
            }

            _presents++;

            if (_fastFlushMode)
            {
                _fastFlushFrames++;
            }

            long nowStats = Stopwatch.GetTimestamp();

            if (GezinjinxPerformanceMode.DetailedTelemetry && nowStats - _statsStart >= _statsInterval)
            {
                int other = Math.Max(0, _flushTotal - _flushAttachment - _flushDraw - _flushQuery);

                StringBuilder origins = new();

                foreach (string key in _externalOrigins.Keys.ToArray())
                {
                    if (_externalOrigins.TryRemove(key, out int count) && count > 0)
                    {
                        origins.Append(' ').Append(key).Append('=').Append(count);
                    }
                }

                Logger.Info?.Print(LogClass.Gpu,
                    $"[NEOV2-ETAPA16] flushes total={_flushTotal} attachment={_flushAttachment} draw={_flushDraw} drawSuppressed={_drawFlushesSuppressed} query={_flushQuery} other={other} frames={_presents} fastFlushFrames={_fastFlushFrames} origins:{origins}");

                _statsStart = nowStats;
                _flushTotal = 0;
                _flushAttachment = 0;
                _flushDraw = 0;
                _drawFlushesSuppressed = 0;
                _flushQuery = 0;
                _presents = 0;
                _fastFlushFrames = 0;
            }
        }
    }
}
