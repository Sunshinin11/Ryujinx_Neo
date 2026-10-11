using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Shader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Ryujinx.Graphics.Gpu.Engine
{
    /// <summary>
    /// TOTK-DIAG-E12: env-gated diagnostics (RYUJINX_TOTK_DIAG_DIR).
    ///  - "skip.txt": guest shader hashes (16 hex, one per line) whose draws (fragment hash) are
    ///    rasterizer-discarded / whose compute dispatches are not executed. Hot-reloaded.
    ///  - "timeline-now": trigger file; logs the next N draws/dispatches, then deletes the trigger.
    /// </summary>
    static class TotkDiagE12
    {
        public static readonly string Dir = Environment.GetEnvironmentVariable("RYUJINX_TOTK_DIAG_DIR");
        public static readonly bool Enabled = !string.IsNullOrEmpty(Dir);

        private static readonly ConditionalWeakTable<byte[], string> _hashes = new();
        private static HashSet<string> _skip = new();
        private static DateTime _skipMtime;
        private static Dictionary<string, float> _bias = new();
        private static DateTime _biasMtime;
        private static long _lastPoll;
        private static int _timelineLeft;

        /// <summary>DUMPTARGET-E19: fragment hash whose draw is dumped by the E16 trigger (diag dir/"dump-target.txt").</summary>
        public static volatile string DumpTargetFs = "6a7d805adb0cfaae";

        /// <summary>MULTITARGET-E26: all fragment hashes listed in dump-target.txt (dumped in the same frame).</summary>
        public static volatile string[] DumpTargets = { "6a7d805adb0cfaae" };
        private static int _timelineSeq;
        private static readonly object _lock = new();

        public static string Hash(byte[] code)
        {
            if (code == null)
            {
                return null;
            }

            return _hashes.GetValue(code, c => Convert.ToHexString(SHA256.HashData(c)).Substring(0, 16).ToLowerInvariant());
        }

        public static string FragmentHash(CachedShaderProgram gs)
        {
            return gs?.Shaders != null && gs.Shaders.Length >= 6 ? Hash(gs.Shaders[5]?.Code) : null;
        }

        public static string VertexHash(CachedShaderProgram gs)
        {
            return gs?.Shaders != null && gs.Shaders.Length >= 2 ? Hash(gs.Shaders[1]?.Code) : null;
        }

        private static void Poll()
        {
            long now = Environment.TickCount64;
            if (now - _lastPoll < 1000)
            {
                return;
            }

            _lastPoll = now;

            try
            {
                string skipFile = Path.Combine(Dir, "skip.txt");
                DateTime mtime = File.Exists(skipFile) ? File.GetLastWriteTimeUtc(skipFile) : DateTime.MinValue;

                if (mtime != _skipMtime)
                {
                    _skipMtime = mtime;
                    var set = new HashSet<string>();
                    if (mtime != DateTime.MinValue)
                    {
                        foreach (string line in File.ReadAllLines(skipFile))
                        {
                            string t = line.Trim().ToLowerInvariant();
                            if (t.Length >= 16 && !t.StartsWith('#'))
                            {
                                set.Add(t.Substring(0, 16));
                            }
                        }
                    }

                    _skip = set;
                    Logger.Info?.Print(LogClass.Gpu, $"[TOTK-DIAG-E12] skip set reloaded: {set.Count} [{string.Join(",", set)}]");
                }

                string biasFile = Path.Combine(Dir, "bias.txt");
                DateTime bmtime = File.Exists(biasFile) ? File.GetLastWriteTimeUtc(biasFile) : DateTime.MinValue;

                if (bmtime != _biasMtime)
                {
                    _biasMtime = bmtime;
                    var map = new Dictionary<string, float>();
                    if (bmtime != DateTime.MinValue)
                    {
                        foreach (string line in File.ReadAllLines(biasFile))
                        {
                            string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 2 && parts[0].Length >= 16 && !parts[0].StartsWith('#') &&
                                float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float u))
                            {
                                map[parts[0].ToLowerInvariant().Substring(0, 16)] = u;
                            }
                        }
                    }

                    _bias = map;
                    Logger.Info?.Print(LogClass.Gpu, $"[TOTK-DIAG-E13] bias map reloaded: {map.Count} [{string.Join(",", map)}]");
                }

                string targetFile = Path.Combine(Dir, "dump-target.txt");
                if (File.Exists(targetFile))
                {
                    string t = File.ReadAllText(targetFile).Trim().ToLowerInvariant();
                    string[] list = t.Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    var parsed = new List<string>();
                    foreach (string e in list)
                    {
                        if (e.Length >= 16)
                        {
                            parsed.Add(e.Substring(0, 16));
                        }
                    }

                    string joined = string.Join(",", parsed);
                    if (parsed.Count > 0 && joined != string.Join(",", DumpTargets))
                    {
                        DumpTargets = parsed.ToArray();
                        DumpTargetFs = parsed[0];
                        Logger.Info?.Print(LogClass.Gpu, $"[TOTK-DIAG-E26] dump targets = {joined}");
                    }
                }

                if (_timelineLeft == 0 && File.Exists(Path.Combine(Dir, "timeline-now")))
                {
                    File.Delete(Path.Combine(Dir, "timeline-now"));
                    _timelineLeft = 6000;
                    _timelineSeq = 0;
                    Logger.Info?.Print(LogClass.Gpu, "[TOTK-TIMELINE-E12] BEGIN");
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"[TOTK-DIAG-E12] poll failed: {ex.Message}");
            }
        }

        public static bool ShouldSkip(string hash)
        {
            if (!Enabled || hash == null)
            {
                return false;
            }

            lock (_lock)
            {
                Poll();
                return _skip.Contains(hash);
            }
        }

        public static float? GetBias(string hash)
        {
            if (!Enabled || hash == null)
            {
                return null;
            }

            lock (_lock)
            {
                Poll();
                return _bias.TryGetValue(hash, out float u) ? u : null;
            }
        }

        public static void Timeline(string text)
        {
            if (!Enabled)
            {
                return;
            }

            lock (_lock)
            {
                Poll();

                if (_timelineLeft <= 0)
                {
                    return;
                }

                _timelineLeft--;
                Logger.Info?.Print(LogClass.Gpu, $"[TOTK-TIMELINE-E12] #{_timelineSeq++} {text}");

                if (_timelineLeft == 0)
                {
                    Logger.Info?.Print(LogClass.Gpu, "[TOTK-TIMELINE-E12] END");
                }
            }
        }
    }
}
