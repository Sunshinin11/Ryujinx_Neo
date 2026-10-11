using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Vulkan.Queries;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Vulkan
{
    class PipelineFull : PipelineBase, IPipeline
    {
        private const ulong MinByteWeightForFlush = 256 * 1024 * 1024; // MiB

        private readonly List<(QueryPool, bool)> _activeQueries;
        private CounterQueueEvent _activeConditionalRender;
        private bool _activeConditionalRenderInverted;
        private bool _conditionalRenderingActive;

        private readonly List<BufferedQuery> _pendingQueryCopies;
        private readonly List<BufferHolder> _activeBufferMirrors;

        private ulong _byteWeight;

        private readonly List<BufferHolder> _backingSwaps;

        public PipelineFull(VulkanRenderer gd, Device device) : base(gd, device, persistentPipelineCache: true)
        {
            _activeQueries = [];
            _pendingQueryCopies = [];
            _backingSwaps = [];
            _activeBufferMirrors = [];

            CommandBuffer = (Cbs = gd.CommandBufferPool.Rent()).CommandBuffer;

            IsMainPipeline = true;
        }

        private void CopyPendingQuery()
        {
            foreach (BufferedQuery query in _pendingQueryCopies)
            {
                query.PoolCopy(Cbs);
            }

            _pendingQueryCopies.Clear();
        }

        public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
        {
            if (FramebufferParams == null)
            {
                return;
            }

            if (componentMask != 0xf || Gd.IsQualcommProprietary)
            {
                // We can't use CmdClearAttachments if not writing all components,
                // because on Vulkan, the pipeline state does not affect clears.
                // On proprietary Adreno drivers, CmdClearAttachments appears to execute out of order, so it's better to not use it at all.
                TextureView dstTexture = FramebufferParams.GetColorView(index);
                if (dstTexture == null)
                {
                    return;
                }

                Span<float> clearColor = stackalloc float[4];
                clearColor[0] = color.Red;
                clearColor[1] = color.Green;
                clearColor[2] = color.Blue;
                clearColor[3] = color.Alpha;

                // TODO: Clear only the specified layer.
                Gd.HelperShader.Clear(
                    Gd,
                    dstTexture,
                    clearColor,
                    componentMask,
                    (int)FramebufferParams.Width,
                    (int)FramebufferParams.Height,
                    FramebufferParams.GetAttachmentComponentType(index),
                    ClearScissor);
            }
            else
            {
                ClearRenderTargetColor(index, layer, layerCount, color);
            }
        }

        public void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, int stencilMask)
        {
            if (FramebufferParams == null)
            {
                return;
            }

            if ((stencilMask != 0 && stencilMask != 0xff) || Gd.IsQualcommProprietary)
            {
                // We can't use CmdClearAttachments if not clearing all (mask is all ones, 0xFF) or none (mask is 0) of the stencil bits,
                // because on Vulkan, the pipeline state does not affect clears.
                // On proprietary Adreno drivers, CmdClearAttachments appears to execute out of order, so it's better to not use it at all.
                TextureView dstTexture = FramebufferParams.GetDepthStencilView();
                if (dstTexture == null)
                {
                    return;
                }

                // TODO: Clear only the specified layer.
                Gd.HelperShader.Clear(
                    Gd,
                    dstTexture,
                    depthValue,
                    depthMask,
                    stencilValue,
                    stencilMask,
                    (int)FramebufferParams.Width,
                    (int)FramebufferParams.Height,
                    FramebufferParams.AttachmentFormats[FramebufferParams.AttachmentsCount - 1],
                    ClearScissor);
            }
            else
            {
                ClearRenderTargetDepthStencil(layer, layerCount, depthValue, depthMask, stencilValue, stencilMask != 0);
            }
        }

        public void EndHostConditionalRendering()
        {
            if (_conditionalRenderingActive)
            {
                Gd.ConditionalRenderingApi.CmdEndConditionalRendering(CommandBuffer);
                _conditionalRenderingActive = false;
            }

            _activeConditionalRender?.ReleaseHostAccess();
            _activeConditionalRender = null;
        }

        protected override void BeginHostConditionalRenderingForDraw()
        {
            if (_activeConditionalRender == null || _conditionalRenderingActive)
            {
                return;
            }

            var buffer = _activeConditionalRender.GetBuffer().Get(Cbs, 0, sizeof(long)).Value;
            var flags = _activeConditionalRenderInverted
                ? ConditionalRenderingFlagsEXT.InvertedBitExt
                : 0;

            var beginInfo = new ConditionalRenderingBeginInfoEXT
            {
                SType = StructureType.ConditionalRenderingBeginInfoExt,
                Buffer = buffer,
                Offset = 0,
                Flags = flags,
            };

            BufferHolder.InsertBufferBarrier(
                Gd,
                CommandBuffer,
                buffer,
                AccessFlags.TransferWriteBit,
                AccessFlags.ConditionalRenderingReadBitExt,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.ConditionalRenderingBitExt,
                0,
                sizeof(long));

            Gd.ConditionalRenderingApi.CmdBeginConditionalRendering(
                CommandBuffer,
                in beginInfo);

            _conditionalRenderingActive = true;
        }

        protected override void EndHostConditionalRenderingForDraw()
        {
            if (_conditionalRenderingActive)
            {
                Gd.ConditionalRenderingApi.CmdEndConditionalRendering(CommandBuffer);
                _conditionalRenderingActive = false;
            }
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual)
        {
            if (value is CounterQueueEvent evt)
            {
                if (Gd.Capabilities.SupportsConditionalRendering &&
                    compare == 0 &&
                    evt.Type == CounterType.SamplesPassed &&
                    evt.ClearCounter)
                {
                    if (!value.ReserveForHostAccess())
                    {
                        return false;
                    }

                    _activeConditionalRender = evt;
                    _activeConditionalRenderInverted = isEqual;
                    return true;
                }
            }

            FlushPendingQuery();
            return false;
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
        {
            FlushPendingQuery(); // The thread will be stalled manually flushing the counter, so flush commands now.
            return false;
        }

        private void FlushPendingQuery()
        {
            if (AutoFlush.ShouldFlushQuery())
            {
                FlushCommandsImpl();
            }
        }

        public CommandBufferScoped GetPreloadCommandBuffer()
        {
            PreloadCbs ??= Gd.CommandBufferPool.Rent();

            return PreloadCbs.Value;
        }

        public void FlushCommandsIfWeightExceeding(IAuto disposedResource, ulong byteWeight)
        {
            bool usedByCurrentCb = disposedResource.HasCommandBufferDependency(Cbs);

            if (PreloadCbs != null && !usedByCurrentCb)
            {
                usedByCurrentCb = disposedResource.HasCommandBufferDependency(PreloadCbs.Value);
            }

            if (usedByCurrentCb)
            {
                // Since we can only free memory after the command buffer that uses a given resource was executed,
                // keeping the command buffer might cause a high amount of memory to be in use.
                // To prevent that, we force submit command buffers if the memory usage by resources
                // in use by the current command buffer is above a given limit, and those resources were disposed.
                _byteWeight += byteWeight;

                if (_byteWeight >= MinByteWeightForFlush)
                {
                    FlushCommandsImpl();
                }
            }
        }

        public void Restore()
        {
            if (Pipeline != null)
            {
                Gd.Api.CmdBindPipeline(CommandBuffer, Pbp, Pipeline.Get(Cbs).Value);
            }

            SignalCommandBufferChange();

            if (Pipeline != null && Pbp == PipelineBindPoint.Graphics)
            {
                DynamicState.ReplayIfDirty(Gd, CommandBuffer);
            }
        }

        public void FlushCommandsImpl()
        {
            AutoFlush.RegisterFlush(DrawCount);
            EndRenderPass();

            foreach ((QueryPool queryPool, _) in _activeQueries)
            {
                Gd.Api.CmdEndQuery(CommandBuffer, queryPool, 0);
            }

            _byteWeight = 0;

            if (PreloadCbs != null)
            {
                PreloadCbs.Value.Dispose();
                PreloadCbs = null;
            }

            Gd.Barriers.Flush(Cbs, false, null, null);
            CommandBuffer = (Cbs = Gd.CommandBufferPool.ReturnAndRent(Cbs)).CommandBuffer;
            Gd.RegisterFlush();

            // Restore per-command buffer state.
            foreach (BufferHolder buffer in _activeBufferMirrors)
            {
                buffer.ClearMirrors();
            }

            _activeBufferMirrors.Clear();

            foreach ((QueryPool queryPool, bool isOcclusion) in _activeQueries)
            {
                bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;

                Gd.Api.CmdResetQueryPool(CommandBuffer, queryPool, 0, 1);
                Gd.Api.CmdBeginQuery(CommandBuffer, queryPool, 0, isPrecise ? QueryControlFlags.PreciseBit : 0);
            }

            Gd.ResetCounterPool();

            Restore();
        }

        public void RegisterActiveMirror(BufferHolder buffer)
        {
            _activeBufferMirrors.Add(buffer);
        }

        public void BeginQuery(BufferedQuery query, QueryPool pool, bool needsReset, bool isOcclusion, bool fromSamplePool)
        {
            if (needsReset)
            {
                EndRenderPass();

                Gd.Api.CmdResetQueryPool(CommandBuffer, pool, 0, 1);

                if (fromSamplePool)
                {
                    // Try reset some additional queries in advance.

                    Gd.ResetFutureCounters(CommandBuffer, AutoFlush.GetRemainingQueries());
                }
            }

            bool isPrecise = Gd.Capabilities.SupportsPreciseOcclusionQueries && isOcclusion;
            Gd.Api.CmdBeginQuery(CommandBuffer, pool, 0, isPrecise ? QueryControlFlags.PreciseBit : 0);

            _activeQueries.Add((pool, isOcclusion));
        }

        public void EndQuery(QueryPool pool)
        {
            Gd.Api.CmdEndQuery(CommandBuffer, pool, 0);

            for (int i = 0; i < _activeQueries.Count; i++)
            {
                if (_activeQueries[i].Item1.Handle == pool.Handle)
                {
                    _activeQueries.RemoveAt(i);
                    break;
                }
            }
        }

        public void CopyQueryResults(BufferedQuery query)
        {
            _pendingQueryCopies.Add(query);

            if (AutoFlush.RegisterPendingQuery())
            {
                FlushCommandsImpl();
            }
        }

        protected override void SignalAttachmentChange()
        {
            if (AutoFlush.ShouldFlushAttachmentChange(DrawCount))
            {
                FlushCommandsImpl();
            }
        }

        protected override void SignalRenderPassEnd()
        {
            CopyPendingQuery();
        }
    }
}
