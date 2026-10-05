using Ryujinx.Graphics.GAL;
using System.Diagnostics;

namespace Ryujinx.Graphics.Gpu.Image
{
    /// <summary>
    /// NEOV2-ARRAYMEM1: limits host textures created only because a shader uses a bindless texture array.
    /// Ryujinx fills those arrays with every valid entry of the texture pool (or every handle in the constant buffer),
    /// even entries the shader never samples. Eden only creates the textures a draw actually uses.
    /// DOOM Eternal on the AYN Thor created 5918 pool textures (4.5 GB) in 4 seconds on the load screen and ran
    /// Android out of RAM. With "Memoria de GPU estilo Eden" on, array entries whose texture does not exist yet
    /// are skipped when they are backed by placeholder/unmapped pages or when the memory budget is exhausted.
    /// Skipped entries are retried later, when the budget or the GPU mapping changes.
    /// </summary>
    class TextureArrayBudget
    {
        // Above this much device memory, arrays stop creating new textures (normal bindings are never limited).
        public const long MaxDeviceBytes = 2048L * GpuMemoryStats.MiB;

        // Android must keep at least this much RAM available for arrays to keep creating textures.
        public const long MinHostAvailableBytes = 2048L * GpuMemoryStats.MiB;

        // Retry interval for arrays with skipped entries.
        public static readonly long RetryTicks = Stopwatch.Frequency / 2;

        public int SkippedSparse;
        public int SkippedBudget;
        public bool BudgetAvailable;

        public static bool Enabled => GraphicsConfig.EdenGpuMemory;

        public static bool CanCreate()
        {
            if (GpuMemoryStats.HostPressureLevel != 0 || GpuMemoryStats.TotalBytes >= MaxDeviceBytes)
            {
                return false;
            }

            long available = GpuMemoryStats.ReadHostAvailableBytesCached();

            return available < 0 || available >= MinHostAvailableBytes;
        }

        public void Begin()
        {
            SkippedSparse = 0;
            SkippedBudget = 0;
            BudgetAvailable = CanCreate();
        }

        public void OnCreated()
        {

            // Re-evaluate after every creation, so a single array rebuild cannot exceed the budget.
            BudgetAvailable = CanCreate();
        }

        public int Skipped => SkippedSparse + SkippedBudget;
    }
}
