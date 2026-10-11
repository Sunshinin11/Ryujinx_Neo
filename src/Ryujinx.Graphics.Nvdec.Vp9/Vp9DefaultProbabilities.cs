using Ryujinx.Common.Memory;
using Ryujinx.Graphics.Nvdec.Vp9.Types;
using Ryujinx.Graphics.Video;

namespace Ryujinx.Graphics.Nvdec.Vp9
{
    public static class Vp9DefaultProbabilities
    {
        public static unsafe Vp9EntropyProbs Create()
        {
            Vp9EntropyProbs probabilities = default;

            Vp9EntropyProbs[] frameContexts =
                new Vp9EntropyProbs[Constants.FrameContexts];

            fixed (Vp9EntropyProbs* contexts = frameContexts)
            {
                Vp9Common common = default;

                common.Fc =
                    new Ptr<Vp9EntropyProbs>(
                        ref probabilities);

                common.FrameContexts =
                    new ArrayPtr<Vp9EntropyProbs>(
                        contexts,
                        frameContexts.Length);

                common.FrameType = FrameType.KeyFrame;

                EntropyMode.SetupPastIndependence(
                    ref common);
            }

            return probabilities;
        }
    }
}
