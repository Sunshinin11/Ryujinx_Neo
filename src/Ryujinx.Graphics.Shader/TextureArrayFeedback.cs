namespace Ryujinx.Graphics.Shader
{
    /// <summary>
    /// NEOV2-ARRAYFB1: storage buffer where bindless texture array accesses record the pool index they sample.
    /// The GPU side reads it to create only the textures the game really uses (Eden only creates sampled textures,
    /// Ryujinx used to create every entry of the texture pool for bindless arrays).
    /// The buffer is identified by a constant buffer slot/offset pair that no guest storage buffer can use.
    /// </summary>
    public static class TextureArrayFeedback
    {
        public const int SbCbSlot = 0xFF;
        public const int SbCbOffset = 0xFFFF;

        /// <summary>
        /// Maximum number of pool entries tracked (texture pool IDs are 20 bits).
        /// </summary>
        public const int MaxEntries = 1 << 20;

        public static bool IsFeedbackBuffer(in BufferDescriptor descriptor)
        {
            return descriptor.SbCbSlot == SbCbSlot && descriptor.SbCbOffset == SbCbOffset;
        }
    }
}
