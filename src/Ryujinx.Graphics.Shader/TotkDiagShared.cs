using System;

namespace Ryujinx.Graphics.Shader
{
    /// <summary>
    /// TOTK diagnostics shared between the GPU layer and the shader translator (env-gated).
    /// </summary>
    public static class TotkDiagShared
    {
        /// <summary>Guest code hash (first 16 hex of SHA-256) of the stage currently being translated on this thread.</summary>
        [ThreadStatic]
        public static string CurrentGuestHash;

        /// <summary>SHADOWTAP-E17: replace depth-compare texture results by 1.0 in the selected guest shader.</summary>
        public static readonly bool ShadowTapOne =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_SHADOWTAP_ONE") == "1";

        // E23: "one" (result=1.0), "lod0" (explicit LOD -> 0), "implicit" (drop explicit LOD).
        public static readonly string ShadowTapMode =
            (Environment.GetEnvironmentVariable("RYUJINX_TOTK_SHADOWTAP_MODE") ?? "one").Trim().ToLowerInvariant();

        // E22: target configurable (default keeps the E17 target).
        public static readonly string ShadowTapTarget =
            (Environment.GetEnvironmentVariable("RYUJINX_TOTK_SHADOWTAP_TARGET") ?? "6a7d805adb0cfaae").Trim().ToLowerInvariant();

        // E35: comma-separated list of targets.
        public static bool IsTapTarget(string hash)
        {
            return hash != null && System.Array.IndexOf(ShadowTapTarget.Split(','), hash) >= 0;
        }

        public static string Hash(byte[] code)
        {
            return code == null
                ? null
                : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(code)).Substring(0, 16).ToLowerInvariant();
        }
    }
}
