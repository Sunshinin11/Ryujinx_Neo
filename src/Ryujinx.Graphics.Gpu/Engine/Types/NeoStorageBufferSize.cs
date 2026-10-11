using Ryujinx.Graphics.Gpu.Memory;

namespace Ryujinx.Graphics.Gpu.Engine.Types
{
    static class NeoStorageBufferSize
    {
        private const uint FallbackMaxSize = 8 * 1024 * 1024;

        public static uint FromDriverDescriptor(
            MemoryManager memoryManager,
            in SbDescriptor descriptor,
            int stage,
            int cbSlot)
        {
            if (!Ryujinx.Common.Diagnostics.NeoGraphicsSettings.ExperimentalGameFixesEnabled)
            {
                return cbSlot == Constants.DriverReservedUniformBuffer
                    ? (uint)descriptor.Size
                    : (uint)memoryManager.GetMappedSize(
                        descriptor.PackAddress(),
                        Constants.MaxUnknownStorageSize);
            }

            uint packedSize = (uint)descriptor.Size;
            uint upper = (uint)descriptor.Padding;
            ulong address = descriptor.PackAddress();

            if (address == 0)
            {
                return 0;
            }

            if (upper == 0 &&
                packedSize != 0 &&
                memoryManager.GetMemoryLayoutSize(address, packedSize) >= packedSize)
            {
                return packedSize;
            }

            return (uint)memoryManager.GetMemoryLayoutSize(
                address,
                FallbackMaxSize);
        }
    }
}
