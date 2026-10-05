using ARMeilleure.Memory;
using Ryujinx.Common;
using Ryujinx.Memory;

namespace Ryujinx.Cpu.Nce
{
    public class NceEngine : ICpuEngine
    {
        public ITickSource TickSource{ get; }

        public static bool IsSupportedHostPageSize => !PlatformInfo.IsBionic || SupportsBionicPageSize(MemoryBlock.GetPageSize());

        public static bool SupportsBionicPageSize(ulong size) => size == 0x1000;

        public NceEngine(ITickSource tickSource)
        {
            TickSource = tickSource;
        }

        public ICpuContext CreateCpuContext(IMemoryManager memoryManager, bool for64Bit)
        {
            return new NceCpuContext(TickSource, memoryManager, for64Bit);
        }
    }
}