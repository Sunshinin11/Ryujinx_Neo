using ARMeilleure.Memory;

namespace Ryujinx.Cpu.Nce
{
    public class NceEngine : ICpuEngine
    {
        public ITickSource TickSource{ get; }

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