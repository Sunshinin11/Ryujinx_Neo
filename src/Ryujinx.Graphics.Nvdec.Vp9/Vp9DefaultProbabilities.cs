using Ryujinx.Common.Memory;
using Ryujinx.Graphics.Nvdec.Vp9.Types;
using Ryujinx.Graphics.Video;

namespace Ryujinx.Graphics.Nvdec.Vp9
{
    /// <summary>
    /// NEOV2-VP9EDEN1: probabilidades iniciais do VP9 (as mesmas do libvpx/FFmpeg), usadas pelo
    /// caminho FFmpeg para calcular os deltas do cabecalho comprimido.
    /// </summary>
    public static class Vp9DefaultProbabilities
    {
        public static unsafe Vp9EntropyProbs Create()
        {
            Vp9EntropyProbs probabilities = default;
            Vp9EntropyProbs[] frameContexts = new Vp9EntropyProbs[Constants.FrameContexts];

            // SetupPastIndependence copia as probabilidades para todos os contextos,
            // entao FrameContexts precisa apontar para um vetor valido.
            fixed (Vp9EntropyProbs* contexts = frameContexts)
            {
                Vp9Common common = default;
                common.Fc = new Ptr<Vp9EntropyProbs>(ref probabilities);
                common.FrameContexts = new ArrayPtr<Vp9EntropyProbs>(contexts, frameContexts.Length);
                common.FrameType = FrameType.KeyFrame;
                EntropyMode.SetupPastIndependence(ref common);
            }

            return probabilities;
        }
    }
}
