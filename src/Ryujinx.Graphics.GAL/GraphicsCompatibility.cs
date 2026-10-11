using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Graphics.GAL
{
    public readonly struct Mua3ShaderIdentity
    {
        public readonly ulong VertexA;
        public readonly ulong VertexB;
        public readonly ulong TessControl;
        public readonly ulong TessEvaluation;
        public readonly ulong Geometry;
        public readonly ulong Fragment;

        public Mua3ShaderIdentity(ulong vertexA, ulong vertexB, ulong tessControl,
            ulong tessEvaluation, ulong geometry, ulong fragment)
        {
            VertexA = vertexA;
            VertexB = vertexB;
            TessControl = tessControl;
            TessEvaluation = tessEvaluation;
            Geometry = geometry;
            Fragment = fragment;
        }

        public override string ToString()
        {
            return $"VA={VertexA:X},VB={VertexB:X},TC={TessControl:X},TE={TessEvaluation:X},G={Geometry:X},F={Fragment:X}";
        }
    }

    /// <summary>
    /// Centralizes title-specific graphics compatibility switches.
    /// Experimental workarounds must remain opt-in so they cannot affect other games.
    /// </summary>
    public static class GraphicsCompatibility
    {
        public const string Mua3TitleId = "010060700ac50000";
        public const string Lm3TitleId = "0100dca0064a6000";

        private static string _titleId;
        private static int _isMua3;
        private static int _isLm3;
        private static readonly ConditionalWeakTable<IProgram, ShaderIdentityHolder> _mua3Programs = new();

        private sealed class ShaderIdentityHolder
        {
            public Mua3ShaderIdentity Identity { get; }

            public ShaderIdentityHolder(Mua3ShaderIdentity identity)
            {
                Identity = identity;
            }
        }

        public static string TitleId
        {
            get => Volatile.Read(ref _titleId);
            set
            {
                Volatile.Write(ref _titleId, value);
                Volatile.Write(
                    ref _isMua3,
                    string.Equals(value, Mua3TitleId, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                Volatile.Write(
                    ref _isLm3,
                    string.Equals(value, Lm3TitleId, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            }
        }

        // NEOV2 TITLE-FLAGS1: these properties are queried from graphics hot paths.
        // Resolve the title string once on load instead of comparing it on every draw.
        public static bool IsMua3 => Volatile.Read(ref _isMua3) != 0;
        public static bool IsLm3 => Volatile.Read(ref _isLm3) != 0;

        public static void RegisterMua3Program(IProgram program, Mua3ShaderIdentity identity)
        {
            if (!IsMua3 || program == null)
            {
                return;
            }

            _mua3Programs.GetValue(program, _ => new ShaderIdentityHolder(identity));
        }

        public static bool TryGetMua3Program(IProgram program, out Mua3ShaderIdentity identity)
        {
            if (program != null && _mua3Programs.TryGetValue(program, out ShaderIdentityHolder holder))
            {
                identity = holder.Identity;
                return true;
            }

            identity = default;
            return false;
        }

        public static void TransferMua3Program(IProgram source, IProgram destination)
        {
            if (IsMua3 && destination != null && TryGetMua3Program(source, out Mua3ShaderIdentity identity))
            {
                _mua3Programs.GetValue(destination, _ => new ShaderIdentityHolder(identity));
            }
        }
    }
}
