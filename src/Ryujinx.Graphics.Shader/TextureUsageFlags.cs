using System;

namespace Ryujinx.Graphics.Shader
{
    /// <summary>
    /// Flags that indicate how a texture will be used in a shader.
    /// </summary>
    [Flags]
    public enum TextureUsageFlags
    {
        None = 0,

        // Integer sampled textures must be noted for resolution scaling.
        ResScaleUnsupported = 1 << 0,
        NeedsScaleValue = 1 << 1,
        ImageStore = 1 << 2,
        ImageCoherent = 1 << 3,

        // NEOV2-ARRAYFB1: the shader writes the pool indices it samples to the texture array feedback buffer.
        ArrayFeedback = 1 << 4,
    }
}
