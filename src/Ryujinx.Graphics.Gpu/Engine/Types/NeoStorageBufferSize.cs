using Ryujinx.Graphics.Gpu.Memory;

namespace Ryujinx.Graphics.Gpu.Engine.Types
{
    /// <summary>
    /// NEOV2-COSMIC3/7/8: tamanho do storage buffer lido do descritor, com a regra do Eden
    /// (PR #3206 "nvn descriptor layout fix" e PR #4041, buffer_cache.h StorageBufferBinding).
    ///
    /// No MARVEL Cosmic Invasion a segunda palavra de 64 bits do descritor nao e um tamanho, e sim outro
    /// endereco de GPU. Usar os 32 bits baixos dela como tamanho gerava um buffer de ~3 GB.
    ///
    /// Eden #4041: o valor so e tamanho se for nao zero, os 32 bits altos forem zero e ele
    /// couber na regiao do mesmo tipo de memoria; senao usa min(essa regiao, 8 MiB), em qualquer slot.
    /// So vale com a chave por jogo "Compatibilidade grafica 2D"; desligada, e a regra original.
    /// </summary>
    static class NeoStorageBufferSize
    {
        private const uint FallbackMaxSize = 8 * 1024 * 1024; // Eden: 8_MiB

        /// <param name="memoryManager">Gerenciador de memoria da GPU</param>
        /// <param name="descriptor">Descritor do storage buffer</param>
        /// <param name="stage">Estagio do shader grafico, ou -1 para compute</param>
        /// <param name="cbSlot">Slot do constant buffer de onde o descritor foi lido</param>
        public static uint FromDriverDescriptor(MemoryManager memoryManager, in SbDescriptor descriptor, int stage, int cbSlot)
        {
            if (!Ryujinx.Common.Diagnostics.NeoGraphicsSettings.ExperimentalGameFixesEnabled)
            {
                return cbSlot == Constants.DriverReservedUniformBuffer
                    ? (uint)descriptor.Size
                    : (uint)memoryManager.GetMappedSize(descriptor.PackAddress(), Constants.MaxUnknownStorageSize);
            }

            uint packedSize = (uint)descriptor.Size;
            uint upper = (uint)descriptor.Padding;
            ulong address = descriptor.PackAddress();

            // Como o Eden: endereco 0 e binding nulo.
            if (address == 0)
            {
                return 0;
            }

            if (upper == 0 && packedSize != 0 && memoryManager.GetMemoryLayoutSize(address, packedSize) >= packedSize)
            {
                return packedSize;
            }

            return (uint)memoryManager.GetMemoryLayoutSize(address, FallbackMaxSize);
        }
    }
}
