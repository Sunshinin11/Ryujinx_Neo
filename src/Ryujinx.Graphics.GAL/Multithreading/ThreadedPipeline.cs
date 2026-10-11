using Ryujinx.Graphics.GAL.Multithreading.Commands;
using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;
using Ryujinx.Graphics.Shader;
using System;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    public class ThreadedPipeline : IPipeline
    {
        private const int MaxTrackedBlendStates = 8;
        private const int MaxTrackedVertexAttributes = 32;

        private readonly ThreadedRenderer _renderer;
        private readonly BlendDescriptor[] _lastBlendStates = new BlendDescriptor[MaxTrackedBlendStates];
        private readonly bool[] _lastBlendStateValid = new bool[MaxTrackedBlendStates];
        private readonly VertexAttribDescriptor[] _lastVertexAttribs = new VertexAttribDescriptor[MaxTrackedVertexAttributes];
        private int _lastVertexAttribCount = -1;
        private FrontFace _lastFrontFace;
        private bool _lastFrontFaceValid;
        private ulong _blendStateCalls;
        private ulong _blendStateSkips;
        private ulong _frontFaceCalls;
        private ulong _frontFaceSkips;
        private ulong _vertexAttribCalls;
        private ulong _vertexAttribSkips;

        public ThreadedPipeline(ThreadedRenderer renderer)
        {
            _renderer = renderer;
        }

        private TableRef<T> Ref<T>(T reference)
        {
            return new TableRef<T>(_renderer, reference);
        }

        public unsafe void Barrier()
        {
            _renderer.New<BarrierCommand>();
            _renderer.QueueCommand();
        }

        public unsafe void BeginTransformFeedback(PrimitiveTopology topology)
        {
            _renderer.New<BeginTransformFeedbackCommand>()->Set(topology);
            _renderer.QueueCommand();
        }

        public unsafe void ClearBuffer(BufferHandle destination, int offset, int size, uint value)
        {
            _renderer.New<ClearBufferCommand>()->Set(destination, offset, size, value);
            _renderer.QueueCommand();
        }

        public unsafe void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
        {
            _renderer.New<ClearRenderTargetColorCommand>()->Set(index, layer, layerCount, componentMask, color);
            _renderer.QueueCommand();
        }

        public unsafe void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, int stencilMask)
        {
            _renderer.New<ClearRenderTargetDepthStencilCommand>()->Set(layer, layerCount, depthValue, depthMask, stencilValue, stencilMask);
            _renderer.QueueCommand();
        }

        public unsafe void CommandBufferBarrier()
        {
            _renderer.New<CommandBufferBarrierCommand>();
            _renderer.QueueCommand();
        }

        public unsafe void CopyBuffer(BufferHandle source, BufferHandle destination, int srcOffset, int dstOffset, int size)
        {
            _renderer.New<CopyBufferCommand>()->Set(source, destination, srcOffset, dstOffset, size);
            _renderer.QueueCommand();
        }

        public unsafe void DispatchCompute(int groupsX, int groupsY, int groupsZ)
        {
            _renderer.New<DispatchComputeCommand>()->Set(groupsX, groupsY, groupsZ);
            _renderer.QueueCommand();
        }

        public unsafe void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
        {
            _renderer.New<DrawCommand>()->Set(vertexCount, instanceCount, firstVertex, firstInstance);
            _renderer.QueueCommand();
        }

        public unsafe void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int firstVertex, int firstInstance)
        {
            _renderer.New<DrawIndexedCommand>()->Set(indexCount, instanceCount, firstIndex, firstVertex, firstInstance);
            _renderer.QueueCommand();
        }

        public unsafe void DrawIndexedIndirect(BufferRange indirectBuffer)
        {
            _renderer.New<DrawIndexedIndirectCommand>()->Set(indirectBuffer);
            _renderer.QueueCommand();
        }

        public unsafe void DrawIndexedIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            _renderer.New<DrawIndexedIndirectCountCommand>()->Set(indirectBuffer, parameterBuffer, maxDrawCount, stride);
            _renderer.QueueCommand();
        }

        public unsafe void DrawIndirect(BufferRange indirectBuffer)
        {
            _renderer.New<DrawIndirectCommand>()->Set(indirectBuffer);
            _renderer.QueueCommand();
        }

        public unsafe void DrawIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            _renderer.New<DrawIndirectCountCommand>()->Set(indirectBuffer, parameterBuffer, maxDrawCount, stride);
            _renderer.QueueCommand();
        }

        public unsafe void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion)
        {
            _renderer.New<DrawTextureCommand>()->Set(Ref(texture), Ref(sampler), srcRegion, dstRegion);
            _renderer.QueueCommand();
        }

        public unsafe void EndHostConditionalRendering()
        {
            _renderer.New<EndHostConditionalRenderingCommand>();
            _renderer.QueueCommand();
        }

        public unsafe void EndTransformFeedback()
        {
            _renderer.New<EndTransformFeedbackCommand>();
            _renderer.QueueCommand();
        }

        public unsafe void SetAlphaTest(bool enable, float reference, CompareOp op)
        {
            _renderer.New<SetAlphaTestCommand>()->Set(enable, reference, op);
            _renderer.QueueCommand();
        }

        public unsafe void SetBlendState(AdvancedBlendDescriptor blend)
        {
            // Advanced blending replaces the state for every target, so the
            // per-target producer cache must be invalidated.
            Array.Clear(_lastBlendStateValid);
            _renderer.New<SetBlendStateAdvancedCommand>()->Set(blend);
            _renderer.QueueCommand();
        }

        public unsafe void SetBlendState(int index, BlendDescriptor blend)
        {
            ulong calls = ++_blendStateCalls;

            if (_lastBlendStateValid[index] && BlendStateEquals(_lastBlendStates[index], blend))
            {
                _blendStateSkips++;
                LogProducerDedup(calls, 0xfffff);
                return;
            }

            _lastBlendStates[index] = blend;
            _lastBlendStateValid[index] = true;
            LogProducerDedup(calls, 0xfffff);
            _renderer.New<SetBlendStateCommand>()->Set(index, blend);
            _renderer.QueueCommand();
        }

        public unsafe void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp)
        {
            _renderer.New<SetDepthBiasCommand>()->Set(enables, factor, units, clamp);
            _renderer.QueueCommand();
        }

        public unsafe void SetDepthClamp(bool clamp)
        {
            _renderer.New<SetDepthClampCommand>()->Set(clamp);
            _renderer.QueueCommand();
        }

        public unsafe void SetDepthMode(DepthMode mode)
        {
            _renderer.New<SetDepthModeCommand>()->Set(mode);
            _renderer.QueueCommand();
        }

        public unsafe void SetDepthTest(DepthTestDescriptor depthTest)
        {
            _renderer.New<SetDepthTestCommand>()->Set(depthTest);
            _renderer.QueueCommand();
        }

        public unsafe void SetFaceCulling(bool enable, Face face)
        {
            _renderer.New<SetFaceCullingCommand>()->Set(enable, face);
            _renderer.QueueCommand();
        }

        public unsafe void SetFrontFace(FrontFace frontFace)
        {
            ulong calls = ++_frontFaceCalls;

            if (_lastFrontFaceValid && _lastFrontFace == frontFace)
            {
                _frontFaceSkips++;
                LogProducerDedup(calls, 0x3ffff);
                return;
            }

            _lastFrontFace = frontFace;
            _lastFrontFaceValid = true;
            LogProducerDedup(calls, 0x3ffff);
            _renderer.New<SetFrontFaceCommand>()->Set(frontFace);
            _renderer.QueueCommand();
        }

        public unsafe void SetImage(ShaderStage stage, int binding, ITexture texture)
        {
            _renderer.New<SetImageCommand>()->Set(stage, binding, Ref(texture));
            _renderer.QueueCommand();
        }

        public unsafe void SetImageArray(ShaderStage stage, int binding, IImageArray array)
        {
            _renderer.New<SetImageArrayCommand>()->Set(stage, binding, Ref(array));
            _renderer.QueueCommand();
        }

        public unsafe void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array)
        {
            _renderer.New<SetImageArraySeparateCommand>()->Set(stage, setIndex, Ref(array));
            _renderer.QueueCommand();
        }

        public unsafe void SetIndexBuffer(BufferRange buffer, IndexType type)
        {
            _renderer.New<SetIndexBufferCommand>()->Set(buffer, type);
            _renderer.QueueCommand();
        }

        public unsafe void SetLineParameters(float width, bool smooth)
        {
            _renderer.New<SetLineParametersCommand>()->Set(width, smooth);
            _renderer.QueueCommand();
        }

        public unsafe void SetLogicOpState(bool enable, LogicalOp op)
        {
            _renderer.New<SetLogicOpStateCommand>()->Set(enable, op);
            _renderer.QueueCommand();
        }

        public unsafe void SetMultisampleState(MultisampleDescriptor multisample)
        {
            _renderer.New<SetMultisampleStateCommand>()->Set(multisample);
            _renderer.QueueCommand();
        }

        public unsafe void SetPatchParameters(int vertices, ReadOnlySpan<float> defaultOuterLevel, ReadOnlySpan<float> defaultInnerLevel)
        {
            _renderer.New<SetPatchParametersCommand>()->Set(vertices, defaultOuterLevel, defaultInnerLevel);
            _renderer.QueueCommand();
        }

        public unsafe void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin)
        {
            _renderer.New<SetPointParametersCommand>()->Set(size, isProgramPointSize, enablePointSprite, origin);
            _renderer.QueueCommand();
        }

        public unsafe void SetPolygonMode(PolygonMode frontMode, PolygonMode backMode)
        {
            _renderer.New<SetPolygonModeCommand>()->Set(frontMode, backMode);
            _renderer.QueueCommand();
        }

        public unsafe void SetPrimitiveRestart(bool enable, int index)
        {
            _renderer.New<SetPrimitiveRestartCommand>()->Set(enable, index);
            _renderer.QueueCommand();
        }

        public unsafe void SetPrimitiveTopology(PrimitiveTopology topology)
        {
            _renderer.New<SetPrimitiveTopologyCommand>()->Set(topology);
            _renderer.QueueCommand();
        }

        public unsafe void SetProgram(IProgram program)
        {
            _renderer.New<SetProgramCommand>()->Set(Ref(program));
            _renderer.QueueCommand();
        }

        public unsafe void SetRasterizerDiscard(bool discard)
        {
            _renderer.New<SetRasterizerDiscardCommand>()->Set(discard);
            _renderer.QueueCommand();
        }

        public unsafe void SetRenderTargetColorMasks(ReadOnlySpan<uint> componentMask)
        {
            _renderer.New<SetRenderTargetColorMasksCommand>()->Set(_renderer.CopySpan(componentMask));
            _renderer.QueueCommand();
        }

        public unsafe void SetRenderTargets(Span<ITexture> colors, ITexture depthStencil)
        {
            ITexture[] colorsCopy = SetRenderTargetsCommand.ArrayPool.Rent(colors.Length);
            colors.CopyTo(colorsCopy.AsSpan());
            
            _renderer.New<SetRenderTargetsCommand>()->Set(colors.Length, Ref(colorsCopy), Ref(depthStencil));
            _renderer.QueueCommand();
        }

        public unsafe void SetScissors(ReadOnlySpan<Rectangle<int>> scissors)
        {
            _renderer.New<SetScissorsCommand>()->Set(_renderer.CopySpan(scissors));
            _renderer.QueueCommand();
        }

        public unsafe void SetStencilTest(StencilTestDescriptor stencilTest)
        {
            _renderer.New<SetStencilTestCommand>()->Set(stencilTest);
            _renderer.QueueCommand();
        }

        public unsafe void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _renderer.New<SetStorageBuffersCommand>()->Set(_renderer.CopySpan(buffers));
            _renderer.QueueCommand();
        }

        public unsafe void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
        {
            _renderer.New<SetTextureAndSamplerCommand>()->Set(stage, binding, Ref(texture), Ref(sampler));
            _renderer.QueueCommand();
        }

        public unsafe void SetTextureArray(ShaderStage stage, int binding, ITextureArray array)
        {
            _renderer.New<SetTextureArrayCommand>()->Set(stage, binding, Ref(array));
            _renderer.QueueCommand();
        }

        public unsafe void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array)
        {
            _renderer.New<SetTextureArraySeparateCommand>()->Set(stage, setIndex, Ref(array));
            _renderer.QueueCommand();
        }

        public unsafe void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers)
        {
            _renderer.New<SetTransformFeedbackBuffersCommand>()->Set(_renderer.CopySpan(buffers));
            _renderer.QueueCommand();
        }

        public unsafe void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _renderer.New<SetUniformBuffersCommand>()->Set(_renderer.CopySpan(buffers));
            _renderer.QueueCommand();
        }

        public unsafe void SetUserClipDistance(int index, bool enableClip)
        {
            _renderer.New<SetUserClipDistanceCommand>()->Set(index, enableClip);
            _renderer.QueueCommand();
        }

        public unsafe void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            ulong calls = ++_vertexAttribCalls;
            int count = Math.Min(MaxTrackedVertexAttributes, vertexAttribs.Length);
            ReadOnlySpan<VertexAttribDescriptor> relevantAttribs = vertexAttribs[..count];

            if (_lastVertexAttribCount == count && relevantAttribs.SequenceEqual(_lastVertexAttribs.AsSpan(0, count)))
            {
                _vertexAttribSkips++;
                LogProducerDedup(calls, 0xfffff);
                return;
            }

            relevantAttribs.CopyTo(_lastVertexAttribs);
            _lastVertexAttribCount = count;
            LogProducerDedup(calls, 0xfffff);
            _renderer.New<SetVertexAttribsCommand>()->Set(_renderer.CopySpan(vertexAttribs));
            _renderer.QueueCommand();
        }

        private static bool BlendStateEquals(BlendDescriptor left, BlendDescriptor right)
        {
            return left.Enable == right.Enable &&
                   left.BlendConstant == right.BlendConstant &&
                   left.ColorOp == right.ColorOp &&
                   left.ColorSrcFactor == right.ColorSrcFactor &&
                   left.ColorDstFactor == right.ColorDstFactor &&
                   left.AlphaOp == right.AlphaOp &&
                   left.AlphaSrcFactor == right.AlphaSrcFactor &&
                   left.AlphaDstFactor == right.AlphaDstFactor;
        }

        private void LogProducerDedup(ulong calls, ulong mask)
        {
            if ((calls & mask) == 0)
            {
                Ryujinx.Common.Logging.Logger.Info?.Print(
                    Ryujinx.Common.Logging.LogClass.Gpu,
                    $"[NEOV2-GAL-PRODUCER-DEDUP1] blendCalls={_blendStateCalls} blendSkips={_blendStateSkips} " +
                    $"frontCalls={_frontFaceCalls} frontSkips={_frontFaceSkips} " +
                    $"attribCalls={_vertexAttribCalls} attribSkips={_vertexAttribSkips}");
            }
        }

        public unsafe void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            _renderer.New<SetVertexBuffersCommand>()->Set(_renderer.CopySpan(vertexBuffers));
            _renderer.QueueCommand();
        }

        public unsafe void SetViewports(ReadOnlySpan<Viewport> viewports)
        {
            _renderer.New<SetViewportsCommand>()->Set(_renderer.CopySpan(viewports));
            _renderer.QueueCommand();
        }

        public unsafe void TextureBarrier()
        {
            _renderer.New<TextureBarrierCommand>();
            _renderer.QueueCommand();
        }

        public unsafe void TextureBarrierTiled()
        {
            _renderer.New<TextureBarrierTiledCommand>();
            _renderer.QueueCommand();
        }

        public unsafe bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual)
        {
            ThreadedCounterEvent evt = value as ThreadedCounterEvent;
            if (evt != null)
            {
                if (compare == 0 && evt.Type == CounterType.SamplesPassed && evt.ClearCounter)
                {
                    if (!evt.ReserveForHostAccess())
                    {
                        return false;
                    }

                    _renderer.New<TryHostConditionalRenderingCommand>()->Set(Ref(evt), compare, isEqual);
                    _renderer.QueueCommand();
                    return true;
                }
            }

            _renderer.New<TryHostConditionalRenderingFlushCommand>()->Set(Ref(evt), Ref<ThreadedCounterEvent>(null), isEqual);
            _renderer.QueueCommand();
            return false;
        }

        public unsafe bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
        {
            _renderer.New<TryHostConditionalRenderingFlushCommand>()->Set(Ref(value as ThreadedCounterEvent), Ref(compare as ThreadedCounterEvent), isEqual);
            _renderer.QueueCommand();
            return false;
        }
    }
}
