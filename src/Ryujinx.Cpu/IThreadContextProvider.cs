using ARMeilleure.State;
using Ryujinx.Common.Memory;

namespace Ryujinx.Cpu
{
    // Called only while the kernel owns a stopped thread. Never a signal-handler API.
    public interface IThreadContextProvider
    {
        bool SupportsThreadContext { get; }
        void InitializeThreadEntry(ulong entrypoint);
        bool TryGetThreadContext(out CpuThreadContext context);
    }

    public struct CpuThreadContext
    {
        public Array32<ulong> Registers; // X0-X30 and architectural SP.
        public Array32<V128> Vectors;
        public ulong Pc;
        public uint Pstate;
        public uint Fpcr;
        public uint Fpsr;
        public long TpidrroEl0;
    }
}
