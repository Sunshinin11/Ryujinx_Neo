using Ryujinx.Common.Memory;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BlendOp = Silk.NET.Vulkan.BlendOp;
using Buffer = Silk.NET.Vulkan.Buffer;
using CompareOp = Ryujinx.Graphics.GAL.CompareOp;
using Format = Silk.NET.Vulkan.Format;
using FrontFace = Ryujinx.Graphics.GAL.FrontFace;
using IndexType = Ryujinx.Graphics.GAL.IndexType;
using PolygonMode = Ryujinx.Graphics.GAL.PolygonMode;
using PrimitiveTopology = Ryujinx.Graphics.GAL.PrimitiveTopology;
using Viewport = Ryujinx.Graphics.GAL.Viewport;

namespace Ryujinx.Graphics.Vulkan
{
    class PipelineBase : IDisposable
    {
        public const int DescriptorSetLayouts = 4;

        public const int UniformSetIndex = 0;
        public const int StorageSetIndex = 1;
        public const int TextureSetIndex = 2;
        public const int ImageSetIndex = 3;

        protected readonly VulkanRenderer Gd;
        protected readonly Device Device;
        public readonly PipelineCache PipelineCache;

        public readonly AutoFlushCounter AutoFlush;
        public readonly Action EndRenderPassDelegate;

        protected PipelineDynamicState DynamicState;
        protected bool IsMainPipeline;
        private PipelineState _newState;

        // NEOV2 Etapa 23: last extended dynamic state values recorded on the current command buffer.
        private bool _extendedDynamicStateValid;
        private ulong _extendedDynamicStateApplied;
        private bool _graphicsStateDirty;
        private bool _computeStateDirty;
        private bool _bindingBarriersDirty;
        private PrimitiveTopology _topology;

        private ulong _currentPipelineHandle;

        // MUA3: the crash scene consistently submits compute work immediately before the
        // graphics command buffers that hang the Adreno command processor. Keep this state
        // across command-buffer rotations so the first matching scene draw can establish a
        // conservative memory dependency on all earlier compute work.
        private bool _mua3ComputeSinceSceneBarrier;
        private static int _mua3SceneBarrierCount;

        protected Auto<DisposablePipeline> Pipeline;

        protected PipelineBindPoint Pbp;

        protected CommandBufferScoped Cbs;
        protected CommandBufferScoped? PreloadCbs;
        protected CommandBuffer CommandBuffer;

        public CommandBufferScoped CurrentCommandBuffer => Cbs;

        private ShaderCollection _program;

        protected FramebufferParams FramebufferParams;
        private Auto<DisposableFramebuffer> _framebuffer;
        private RenderPassHolder _rpHolder;
        private Auto<DisposableRenderPass> _renderPass;
        private RenderPassHolder _nullRenderPass;
        private int _writtenAttachmentCount;

        private bool _framebufferUsingColorWriteMask;

        private ITexture[] _preMaskColors;
        private ITexture _preMaskDepthStencil;

        private readonly DescriptorSetUpdater _descriptorSetUpdater;

        private IndexBufferState _indexBuffer;
        private IndexBufferPattern _indexBufferPattern;
        private ulong _vertexBufferSetCalls;
        private ulong _vertexBufferPipelineChanges;
        private ulong _graphicsPipelineLookupCalls;
        private ulong _graphicsPipelineSameHandle;
        private ulong _drawIndexedProfileCalls;
        private long _drawIndexedProfileSamples;
        private long _drawIndexedProfileIndexTicks;
        private long _drawIndexedProfilePipelineTicks;
        private long _drawIndexedProfileRenderPassTicks;
        private long _drawIndexedProfilePreDrawTicks;
        private long _drawIndexedProfileIssueTicks;
        private long _drawIndexedProfileTotalTicks;
        private long _drawIndexedProfileWindowStart = Stopwatch.GetTimestamp();
        private bool _drawPipelineProfileActive;
        private long _drawPipelineProfilePrepTicks;
        private long _drawPipelineProfileDynamicTicks;
        private long _drawPipelineProfileBufferTicks;
        private long _drawPipelineProfileBindingBarrierTicks;
        private long _drawPipelineProfileCreateTicks;
        private long _drawPipelineProfileExtendedDynamicTicks;
        private long _drawPipelineProfileBarrierFlushTicks;
        private long _drawPipelineProfileDescriptorTicks;
        private long _drawPipelineProfileCreateCount;
        private long _drawPipelineProfileVertexDirtyCount;
        private long _drawPipelineProfileBindingDirtyCount;
        private readonly BufferState[] _transformFeedbackBuffers;
        private readonly VertexBufferState[] _vertexBuffers;
        private ulong _vertexBuffersDirty;
        protected Rectangle<int> ClearScissor;

        private readonly VertexBufferUpdater _vertexBufferUpdater;

        public IndexBufferPattern QuadsToTrisPattern;
        public IndexBufferPattern TriFanToTrisPattern;

        private bool _needsIndexBufferRebind;
        private bool _needsTransformFeedbackBuffersRebind;

        private bool _tfEnabled;
        private bool _tfActive;

        private FeedbackLoopAspects _feedbackLoop;
        private bool _passWritesDepthStencil;

        // TOTK-DRAW-TRACE1
        // Diagnostic only: log each fragment shader hash once when explicitly enabled.
        private static readonly bool _totkDrawTrace = Environment.GetEnvironmentVariable("RYUJINX_TOTK_DRAW_TRACE") == "1";

        // TOTK-RESOURCE-TRACE1
        private static readonly bool _totkResourceTrace = Environment.GetEnvironmentVariable("RYUJINX_TOTK_RESOURCE_TRACE") == "1";
        private static readonly HashSet<string> _totkLoggedResourceSignatures = new(StringComparer.Ordinal);

        // TOTK-PRODUCER-TRACE1
        // Diagnostic only. Traces draws that write the three candidate
        // lighting/environment resources identified by the same-process A/B.
        private static readonly bool _totkProducerTrace = Environment.GetEnvironmentVariable("RYUJINX_TOTK_PRODUCER_TRACE") == "1";
        private static readonly HashSet<string> _totkLoggedProducerSignatures = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _totkLoggedFragmentHashes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _totkTraceLock = new();

        // TOTK-FRAGMENT-SKIP1
        private static readonly HashSet<string> _totkSkippedFragmentHashes = GetTotkSkippedFragmentHashes();
        private static readonly HashSet<string> _totkLoggedSkippedFragmentHashes = new(StringComparer.OrdinalIgnoreCase);

        // TOTK-0EEF-RTMASK2
        private static readonly string _totk0eefMaskRt =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_0EEF_MASK_RT");
        private bool _totk0eefRtMasked;
        private int _totk0eefMaskedRt = -1;
        private PipelineColorBlendAttachmentState _totk0eefSavedRtState;
        private static readonly HashSet<int> _totk0eefLoggedMaskRts = new();

        private static readonly bool _lm3DrawTrace = Environment.GetEnvironmentVariable("RYUJINX_LM3_DRAW_TRACE") == "1";
        private static readonly HashSet<string> _lm3SkippedFragmentHashes = GetLm3SkippedFragmentHashes();
        private static readonly HashSet<string> _lm3LoggedFragmentHashes = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lm3TraceLock = new();
        private long _lm3SkippedDrawCount;

        private readonly PipelineColorBlendAttachmentState[] _storedBlend;
        public ulong DrawCount { get; private set; }
        public bool RenderPassActive { get; private set; }

        // NEOV2 Etapa 6: somente o pipeline principal usa o cache persistido em disco.
        private readonly bool _persistentPipelineCache;

        public unsafe PipelineBase(VulkanRenderer gd, Device device, bool persistentPipelineCache = false)
        {
            Gd = gd;
            Device = device;

            AutoFlush = new AutoFlushCounter(gd);
            EndRenderPassDelegate = EndRenderPass;

            _persistentPipelineCache = persistentPipelineCache;

            if (persistentPipelineCache)
            {
                PipelineCache = PipelineCachePersistence.Create(gd.Api, device);
            }
            else
            {
                PipelineCacheCreateInfo pipelineCacheCreateInfo = new()
                {
                    SType = StructureType.PipelineCacheCreateInfo,
                };

                gd.Api.CreatePipelineCache(device, in pipelineCacheCreateInfo, null, out PipelineCache).ThrowOnError();
            }

            _descriptorSetUpdater = new DescriptorSetUpdater(gd, device);
            _vertexBufferUpdater = new VertexBufferUpdater(gd);

            _transformFeedbackBuffers = new BufferState[Constants.MaxTransformFeedbackBuffers];
            _vertexBuffers = new VertexBufferState[Constants.MaxVertexBuffers + 1];

            const int EmptyVbSize = 16;

            using BufferHolder emptyVb = gd.BufferManager.Create(gd, EmptyVbSize);
            emptyVb.SetData(0, new byte[EmptyVbSize]);
            _vertexBuffers[0] = new VertexBufferState(emptyVb.GetBuffer(), 0, 0, EmptyVbSize);
            _vertexBuffersDirty = ulong.MaxValue >> (64 - _vertexBuffers.Length);

            ClearScissor = new Rectangle<int>(0, 0, 0xffff, 0xffff);

            _storedBlend = new PipelineColorBlendAttachmentState[Constants.MaxRenderTargets];

            _newState.Initialize();
        }

        public void Initialize()
        {
            _descriptorSetUpdater.Initialize(IsMainPipeline);

            QuadsToTrisPattern = new IndexBufferPattern(Gd, 4, 6, 0, [0, 1, 2, 0, 2, 3], 4, false);
            TriFanToTrisPattern = new IndexBufferPattern(Gd, 3, 3, 2, [int.MinValue, -1, 0], 1, true);
        }

        public unsafe void Barrier()
        {
            Gd.Barriers.QueueMemoryBarrier();
        }

        public void ComputeBarrier()
        {
            MemoryBarrier memoryBarrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            };

            Gd.Api.CmdPipelineBarrier(
                CommandBuffer,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                1,
                new ReadOnlySpan<MemoryBarrier>(in memoryBarrier),
                0,
                ReadOnlySpan<BufferMemoryBarrier>.Empty,
                0,
                ReadOnlySpan<ImageMemoryBarrier>.Empty);
        }

        public void BeginTransformFeedback(PrimitiveTopology topology)
        {
            Gd.Barriers.EnableTfbBarriers(true);
            _tfEnabled = true;
        }

        public void ClearBuffer(BufferHandle destination, int offset, int size, uint value)
        {
            EndRenderPass();

            Buffer dst = Gd.BufferManager.GetBuffer(CommandBuffer, destination, offset, size, true).Get(Cbs, offset, size, true).Value;

            BufferHolder.InsertBufferBarrier(
                Gd,
                Cbs.CommandBuffer,
                dst,
                BufferHolder.DefaultAccessFlags,
                AccessFlags.TransferWriteBit,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit,
                offset,
                size);

            Gd.Api.CmdFillBuffer(CommandBuffer, dst, (ulong)offset, (ulong)size, value);

            BufferHolder.InsertBufferBarrier(
                Gd,
                Cbs.CommandBuffer,
                dst,
                AccessFlags.TransferWriteBit,
                BufferHolder.DefaultAccessFlags,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.AllCommandsBit,
                offset,
                size);
        }

        public unsafe void ClearRenderTargetColor(int index, int layer, int layerCount, ColorF color)
        {
            if (FramebufferParams == null || !FramebufferParams.IsValidColorAttachment(index))
            {
                return;
            }

            if (_renderPass == null)
            {
                CreateRenderPass();
            }

            Gd.Barriers.Flush(Cbs, RenderPassActive, _rpHolder, EndRenderPassDelegate);

            ClearValue clearValue = new(new ClearColorValue(color.Red, color.Green, color.Blue, color.Alpha));
            ClearRect clearRect = FramebufferParams.GetClearRect(ClearScissor, layer, layerCount);

            // NEOV2 Etapa 5: clear da area inteira antes de o render pass comecar vira LoadOp.Clear.
            if (TryDeferColorClear(index, clearRect, clearValue))
            {
                return;
            }

            BeginRenderPass();

            ClearAttachment attachment = new(ImageAspectFlags.ColorBit, (uint)index, clearValue);

            Gd.Api.CmdClearAttachments(CommandBuffer, 1, &attachment, 1, &clearRect);
        }

        public unsafe void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, bool stencilMask)
        {
            if (FramebufferParams == null || !FramebufferParams.HasDepthStencil)
            {
                return;
            }

            ClearValue clearValue = new(null, new ClearDepthStencilValue(depthValue, (uint)stencilValue));
            ImageAspectFlags flags = depthMask ? ImageAspectFlags.DepthBit : 0;

            if (stencilMask)
            {
                flags |= ImageAspectFlags.StencilBit;
            }

            flags &= FramebufferParams.GetDepthStencilAspectFlags();

            if (flags == ImageAspectFlags.None)
            {
                return;
            }

            if (_renderPass == null)
            {
                CreateRenderPass();
            }

            Gd.Barriers.Flush(Cbs, RenderPassActive, _rpHolder, EndRenderPassDelegate);

            ClearRect clearRect = FramebufferParams.GetClearRect(ClearScissor, layer, layerCount);

            // NEOV2 Etapa 5: clear da area inteira antes de o render pass comecar vira LoadOp.Clear.
            if (TryDeferDepthStencilClear(flags, clearRect, depthValue, (uint)stencilValue))
            {
                return;
            }

            BeginRenderPass();

            ClearAttachment attachment = new(flags, 0, clearValue);

            Gd.Api.CmdClearAttachments(CommandBuffer, 1, &attachment, 1, &clearRect);
        }

        public unsafe void CommandBufferBarrier()
        {
            Gd.Barriers.QueueCommandBufferBarrier();
        }

        public void CopyBuffer(BufferHandle source, BufferHandle destination, int srcOffset, int dstOffset, int size)
        {
            EndRenderPass();

            Auto<DisposableBuffer> src = Gd.BufferManager.GetBuffer(CommandBuffer, source, srcOffset, size, false);
            Auto<DisposableBuffer> dst = Gd.BufferManager.GetBuffer(CommandBuffer, destination, dstOffset, size, true);

            BufferHolder.Copy(Gd, Cbs, src, dst, srcOffset, dstOffset, size);
        }

        public void DirtyVertexBuffer(Auto<DisposableBuffer> buffer)
        {
            for (int i = 0; i < _vertexBuffers.Length; i++)
            {
                if (_vertexBuffers[i].BoundEquals(buffer))
                {
                    _vertexBuffersDirty |= 1UL << i;
                }
            }
        }

        public void DirtyIndexBuffer(Auto<DisposableBuffer> buffer)
        {
            if (_indexBuffer.BoundEquals(buffer))
            {
                _needsIndexBufferRebind = true;
            }
        }

        public void DispatchCompute(int groupsX, int groupsY, int groupsZ)
        {
            if (!_program.IsLinked)
            {
                return;
            }

            EndRenderPass();
            RecreateComputePipelineIfNeeded();

            RecordMua3Command(Mua3CommandKind.Dispatch, groupsX, groupsY);

            Gd.Api.CmdDispatch(CommandBuffer, (uint)groupsX, (uint)groupsY, (uint)groupsZ);

            if (GraphicsCompatibility.IsMua3)
            {
                _mua3ComputeSinceSceneBarrier = true;
            }
        }

        public void DispatchComputeIndirect(Auto<DisposableBuffer> indirectBuffer, int indirectBufferOffset)
        {
            if (!_program.IsLinked)
            {
                return;
            }

            EndRenderPass();
            RecreateComputePipelineIfNeeded();

            RecordMua3Command(Mua3CommandKind.DispatchIndirect, indirectBufferOffset, 0);

            Gd.Api.CmdDispatchIndirect(CommandBuffer, indirectBuffer.Get(Cbs, indirectBufferOffset, 12).Value, (ulong)indirectBufferOffset);

            if (GraphicsCompatibility.IsMua3)
            {
                _mua3ComputeSinceSceneBarrier = true;
            }
        }

        private unsafe void InsertMua3SceneComputeBarrierIfNeeded()
        {
            if (!_mua3ComputeSinceSceneBarrier ||
                !GraphicsCompatibility.IsMua3 ||
                !GraphicsCompatibility.TryGetMua3Program(_program, out Mua3ShaderIdentity identity) ||
                identity.Geometry != 0x100719F30UL ||
                identity.Fragment != 0x100719E30UL)
            {
                return;
            }

            EndRenderPass();

            MemoryBarrier memoryBarrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            };

            Gd.Api.CmdPipelineBarrier(
                CommandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                1,
                new ReadOnlySpan<MemoryBarrier>(in memoryBarrier),
                0,
                ReadOnlySpan<BufferMemoryBarrier>.Empty,
                0,
                ReadOnlySpan<ImageMemoryBarrier>.Empty);

            _mua3ComputeSinceSceneBarrier = false;

            int count = System.Threading.Interlocked.Increment(ref _mua3SceneBarrierCount);

            if (count == 1 || count % 100 == 0)
            {
                Logger.Info?.Print(
                    LogClass.Gpu,
                    $"[MUA3-SYNC] Full compute-to-scene barrier inserted (count={count}, shaders=[{identity}])");
            }
        }

        private void RecordMua3Command(Mua3CommandKind kind, int argument0, int argument1)
        {
            if (!GraphicsCompatibility.IsMua3)
            {
                return;
            }

            GraphicsCompatibility.TryGetMua3Program(_program, out Mua3ShaderIdentity shaderIdentity);

            Mua3CrashTelemetry.RecordCommand(
                CommandBuffer,
                kind,
                _currentPipelineHandle,
                _newState.Internal.GetHashCode(),
                _program == null ? 0 : RuntimeHelpers.GetHashCode(_program),
                _program?.Stages ?? 0,
                shaderIdentity,
                (int)_topology,
                argument0,
                argument1);
        }

        // TOTK-FRAGMENT-SKIP1
        private static HashSet<string> GetTotkSkippedFragmentHashes()
        {
            string hashes = Environment.GetEnvironmentVariable("RYUJINX_TOTK_SKIP_FRAGMENT_HASHES");

            if (string.IsNullOrWhiteSpace(hashes))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(
                hashes.Split([',', ';', ' ', '\t', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> GetLm3SkippedFragmentHashes()
        {
            string hashes = Environment.GetEnvironmentVariable("RYUJINX_LM3_SKIP_FRAGMENT_HASHES");

            if (string.IsNullOrWhiteSpace(hashes))
            {
                // NEOV2 LM3-TITLE-GATE1: retain the verified LM3 workaround without
                // requiring the canonical launcher to expose it to every other title.
                hashes = "c59ff19a53ddf3e198ba6876bc12aa342005c49c2fd29a0296d25a1250d33d14";
            }

            return new HashSet<string>(
                hashes.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
        }

        // TOTK-PRODUCER-TRACE1
        private void TraceTotkCandidateProducer()
        {
            if (!_totkProducerTrace ||
                _program == null ||
                FramebufferParams == null ||
                FramebufferParams.AttachmentsCount == 0)
            {
                return;
            }

            string producerHash = _program.FragmentShaderHash;

            if (string.IsNullOrEmpty(producerHash))
            {
                return;
            }

            TextureView[] attachments = FramebufferParams.GetAttachmentViews();

            for (int i = 0; i < attachments.Length; i++)
            {
                TextureView view = attachments[i];

                if (view?.Storage == null)
                {
                    continue;
                }

                string format = view.VkFormat.ToString();

                // TOTK-PRODUCER-TRACE2
                // For the 64x64x6 candidate, identify the underlying storage rather
                // than the attachment view. Cubemaps are commonly rendered one face
                // at a time, so the bound view can be 64x64x1 over a 64x64x6 storage.
                bool candidate =
                    ((format == "B10G11R11UfloatPack32" ||
                      view.Storage.VkFormat.ToString() == "B10G11R11UfloatPack32") &&
                     view.Storage.Info.Width == 64 &&
                     view.Storage.Info.Height == 64 &&
                     view.Storage.Info.GetDepthOrLayers() == 6) ||
                    // TOTK-PRODUCER-TRACE3-B10-LOWRES
                    ((format == "B10G11R11UfloatPack32" ||
                      view.Storage.VkFormat.ToString() == "B10G11R11UfloatPack32") &&
                     ((view.Storage.Info.Width == 320 &&
                       view.Storage.Info.Height == 180) ||
                      (view.Storage.Info.Width == 400 &&
                       view.Storage.Info.Height == 225)) &&
                     view.Storage.Info.GetDepthOrLayers() >= 2) ||

                    (format == "R16G16B16A16Sfloat" &&
                     view.Width == 256 &&
                     view.Height == 256) ||

                    (format == "R16G16Unorm" &&
                     view.Width == 500 &&
                     view.Height == 400);

                if (!candidate)
                {
                    continue;
                }

                int storageId =
                    System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(view.Storage);

                long currentGeneration = view.Storage.TotkWriteGeneration;
                long writeGeneration = currentGeneration + 1;

                string role;

                if (i < FramebufferParams.ColorAttachmentsCount)
                {
                    role = "C" + FramebufferParams.AttachmentIndices[i];
                }
                else
                {
                    role = "DS";
                }

                string producerKey =
                    producerHash + "|" +
                    role + "|" +
                    storageId.ToString("X") + "|" +
                    format + "|" +
                    view.Width + "x" + view.Height + "|" +
                    view.Layers + "|" +
                    writeGeneration;

                lock (_totkTraceLock)
                {
                    if (_totkLoggedProducerSignatures.Add(producerKey))
                    {
                        Logger.Info?.Print(
                            LogClass.Gpu,
                            $"[TOTK-PRODUCER-TRACE] HASH={producerHash} DRAW={DrawCount} ATT={role} S={storageId:X} CURRENT_G={currentGeneration} WRITE_G={writeGeneration} VF={format}:{view.Width}x{view.Height}:L{view.Layers}:FL{view.FirstLayer}:FM{view.FirstLevel} SF={view.Storage.VkFormat}:{view.Storage.Info.Width}x{view.Storage.Info.Height}:L{view.Storage.Info.GetDepthOrLayers()}");
                    }
                }
            }
        }

        // TOTK-C827-DEPTH-PRODUCER12
        private static readonly bool _totkC827DepthProducerTrace =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_C827_DEPTH_PRODUCER_TRACE") == "1";

        private void TraceTotkC827DepthAttachmentDraw()
        {
            if (!_totkC827DepthProducerTrace ||
                _program == null ||
                FramebufferParams == null)
            {
                return;
            }

            TextureView depthView = FramebufferParams.GetDepthStencilView();

            if (depthView?.Storage == null)
            {
                return;
            }

            // O B128 observado no c827 e D32Sfloat.
            // Evita encher historico de storages de outros formatos.
            if (!string.Equals(
                depthView.Storage.VkFormat.ToString(),
                "D32Sfloat",
                StringComparison.Ordinal))
            {
                return;
            }

            string hash = _program.FragmentShaderHash ?? "<none>";

            // TOTK-C827-DEPTH-PRODUCER12C
            string viewInfo =
                $"{depthView.VkFormat}:" +
                $"{depthView.Width}x{depthView.Height}:" +
                $"L{depthView.Layers}:" +
                $"FL{depthView.FirstLayer}:" +
                $"FM{depthView.FirstLevel}:" +
                $"DT={_newState.DepthTestEnable}:" +
                $"DW={_newState.DepthWriteEnable}:" +
                $"ST={_newState.StencilTestEnable}";

            depthView.Storage.TotkRecordDepthDraw(
                hash,
                DrawCount,
                _passWritesDepthStencil,
                viewInfo);
        }

        private bool ShouldSkipLm3FragmentDraw()
        {
            TraceTotkC827DepthAttachmentDraw();
            TraceTotkCandidateProducer();
            // TOTK-DRAW-TRACE1
            // Runs for any title only when RYUJINX_TOTK_DRAW_TRACE=1.
            // Does not skip or modify the draw.
            if (_totkDrawTrace && _program != null)
            {
                string totkHash = _program.FragmentShaderHash;

                if (!string.IsNullOrEmpty(totkHash))
                {
                    lock (_totkTraceLock)
                    {
                        if (_totkLoggedFragmentHashes.Add(totkHash))
                        {
                            Logger.Info?.Print(
                                LogClass.Gpu,
                                $"[TOTK-DRAW-TRACE] HASH={totkHash} DRAW={DrawCount} TOPOLOGY={_topology} PIPELINE=0x{_currentPipelineHandle:X}");
                        }
                    }
                }
            }

            // TOTK-RESOURCE-TRACE1
            if (_totkResourceTrace && _program != null)
            {
                string resourceHash = _program.FragmentShaderHash;

                if (!string.IsNullOrEmpty(resourceHash))
                {
                    string resourceSignature = _descriptorSetUpdater.GetTotkFragmentResourceSignature();
                    string resourceKey = resourceHash + "|" + resourceSignature;

                    lock (_totkTraceLock)
                    {
                        if (_totkLoggedResourceSignatures.Add(resourceKey))
                        {
                            Logger.Info?.Print(
                                LogClass.Gpu,
                                $"[TOTK-RESOURCE-TRACE] HASH={resourceHash} DRAW={DrawCount} RES={resourceSignature}");
                        }
                    }
                }
            }

            // TOTK-FRAGMENT-SKIP1
            if (_program != null && _totkSkippedFragmentHashes.Count != 0)
            {
                string totkSkipHash = _program.FragmentShaderHash;

                if (!string.IsNullOrEmpty(totkSkipHash) &&
                    _totkSkippedFragmentHashes.Contains(totkSkipHash))
                {
                    lock (_totkTraceLock)
                    {
                        if (_totkLoggedSkippedFragmentHashes.Add(totkSkipHash))
                        {
                            Logger.Info?.Print(
                                LogClass.Gpu,
                                $"[TOTK-FRAGMENT-SKIP] HASH={totkSkipHash} DRAW={DrawCount}");
                        }
                    }

                    return true;
                }
            }

            if (!GraphicsCompatibility.IsLm3 ||
                _program == null ||
                (!_lm3DrawTrace && _lm3SkippedFragmentHashes.Count == 0))
            {
                return false;
            }

            string hash = _program.FragmentShaderHash;

            if (string.IsNullOrEmpty(hash))
            {
                return false;
            }

            if (_lm3DrawTrace)
            {
                lock (_lm3TraceLock)
                {
                    if (_lm3LoggedFragmentHashes.Add(hash))
                    {
                        Logger.Info?.Print(LogClass.Gpu, $"[LM3-CANDIDATE39] DRAW-FRAGMENT HASH={hash}");
                    }
                }
            }

            if (!_lm3SkippedFragmentHashes.Contains(hash))
            {
                return false;
            }

            long count = ++_lm3SkippedDrawCount;

            if (count == 1 || count % 1000 == 0)
            {
                Logger.Info?.Print(LogClass.Gpu, $"[LM3-CANDIDATE39] SAFE-SKIPPED DRAW count={count} HASH={hash}");
            }

            return true;
        }

        // PipelineFull overrides these hooks to scope VK_EXT_conditional_rendering
        // to the actual guest draw command only.
        protected virtual void BeginHostConditionalRenderingForDraw() { }
        protected virtual void EndHostConditionalRenderingForDraw() { }

        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
        {
            if (vertexCount == 0)
            {
                return;
            }

            if (!RecreateGraphicsPipelineIfNeeded())
            {
                return;
            }

            BeginRenderPass();
            DrawCount++;
            RecordMua3Command(Mua3CommandKind.Draw, vertexCount, instanceCount);

            if (ShouldSkipLm3FragmentDraw())
            {
                return;
            }

            if (Gd.TopologyUnsupported(_topology))
            {
                // Temporarily bind a conversion pattern as an index buffer.
                _needsIndexBufferRebind = true;

                IndexBufferPattern pattern = _topology switch
                {
                    PrimitiveTopology.Quads => QuadsToTrisPattern,
                    PrimitiveTopology.TriangleFan or
                    PrimitiveTopology.Polygon => TriFanToTrisPattern,
                    _ => throw new NotSupportedException($"Unsupported topology: {_topology}"),
                };

                BufferHandle handle = pattern.GetRepeatingBuffer(vertexCount, out int indexCount);
                Auto<DisposableBuffer> buffer = Gd.BufferManager.GetBuffer(CommandBuffer, handle, false);

                Gd.Api.CmdBindIndexBuffer(CommandBuffer, buffer.Get(Cbs, 0, indexCount * sizeof(int)).Value, 0, Silk.NET.Vulkan.IndexType.Uint32);

                BeginRenderPass(); // May have been interrupted to set buffer data.
                ResumeTransformFeedbackInternal();

                BeginHostConditionalRenderingForDraw();
                Gd.Api.CmdDrawIndexed(CommandBuffer, (uint)indexCount, (uint)instanceCount, 0, firstVertex, (uint)firstInstance);
                EndHostConditionalRenderingForDraw();
            }
            else
            {
                ResumeTransformFeedbackInternal();

                BeginHostConditionalRenderingForDraw();
                Gd.Api.CmdDraw(CommandBuffer, (uint)vertexCount, (uint)instanceCount, (uint)firstVertex, (uint)firstInstance);
                EndHostConditionalRenderingForDraw();
            }
        }

        private void UpdateIndexBufferPattern()
        {
            IndexBufferPattern pattern = null;

            if (Gd.TopologyUnsupported(_topology))
            {
                pattern = _topology switch
                {
                    PrimitiveTopology.Quads => QuadsToTrisPattern,
                    PrimitiveTopology.TriangleFan or
                    PrimitiveTopology.Polygon => TriFanToTrisPattern,
                    _ => throw new NotSupportedException($"Unsupported topology: {_topology}"),
                };
            }

            if (_indexBufferPattern != pattern)
            {
                _indexBufferPattern = pattern;
                _needsIndexBufferRebind = true;
            }
        }

        public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int firstVertex, int firstInstance)
        {
            if (indexCount == 0)
            {
                return;
            }

            bool profileDraw = (++_drawIndexedProfileCalls & 0xff) == 0;
            long profileStart = profileDraw ? Stopwatch.GetTimestamp() : 0;

            UpdateIndexBufferPattern();

            long profileAfterIndex = profileDraw ? Stopwatch.GetTimestamp() : 0;

            _drawPipelineProfileActive = profileDraw;
            bool pipelineReady = RecreateGraphicsPipelineIfNeeded();
            _drawPipelineProfileActive = false;

            if (!pipelineReady)
            {
                return;
            }

            long profileAfterPipeline = profileDraw ? Stopwatch.GetTimestamp() : 0;

            BeginRenderPass();
            long profileAfterRenderPass = profileDraw ? Stopwatch.GetTimestamp() : 0;
            DrawCount++;
            RecordMua3Command(Mua3CommandKind.DrawIndexed, indexCount, instanceCount);

            if (ShouldSkipLm3FragmentDraw())
            {
                return;
            }

            long profileAfterPreDraw = profileDraw ? Stopwatch.GetTimestamp() : 0;

            if (_indexBufferPattern != null)
            {
                // Convert the index buffer into a supported topology.
                IndexBufferPattern pattern = _indexBufferPattern;

                int convertedCount = pattern.GetConvertedCount(indexCount);

                if (_needsIndexBufferRebind)
                {
                    _indexBuffer.BindConvertedIndexBuffer(Gd, Cbs, firstIndex, indexCount, convertedCount, pattern);

                    _needsIndexBufferRebind = false;
                }

                BeginRenderPass(); // May have been interrupted to set buffer data.
                ResumeTransformFeedbackInternal();

                BeginHostConditionalRenderingForDraw();
                Gd.Api.CmdDrawIndexed(CommandBuffer, (uint)convertedCount, (uint)instanceCount, 0, firstVertex, (uint)firstInstance);
                EndHostConditionalRenderingForDraw();
            }
            else
            {
                ResumeTransformFeedbackInternal();

                BeginHostConditionalRenderingForDraw();
                Gd.Api.CmdDrawIndexed(CommandBuffer, (uint)indexCount, (uint)instanceCount, (uint)firstIndex, firstVertex, (uint)firstInstance);
                EndHostConditionalRenderingForDraw();
            }

            if (profileDraw)
            {
                long profileEnd = Stopwatch.GetTimestamp();
                RecordDrawIndexedProfile(
                    profileAfterIndex - profileStart,
                    profileAfterPipeline - profileAfterIndex,
                    profileAfterRenderPass - profileAfterPipeline,
                    profileAfterPreDraw - profileAfterRenderPass,
                    profileEnd - profileAfterPreDraw,
                    profileEnd - profileStart,
                    profileEnd);
            }
        }

        private void RecordDrawIndexedProfile(
            long indexTicks,
            long pipelineTicks,
            long renderPassTicks,
            long preDrawTicks,
            long issueTicks,
            long totalTicks,
            long now)
        {
            _drawIndexedProfileSamples++;
            _drawIndexedProfileIndexTicks += indexTicks;
            _drawIndexedProfilePipelineTicks += pipelineTicks;
            _drawIndexedProfileRenderPassTicks += renderPassTicks;
            _drawIndexedProfilePreDrawTicks += preDrawTicks;
            _drawIndexedProfileIssueTicks += issueTicks;
            _drawIndexedProfileTotalTicks += totalTicks;

            long windowTicks = now - _drawIndexedProfileWindowStart;

            if (windowTicks < Stopwatch.Frequency * 5)
            {
                return;
            }

            static double EstimatedMs(long ticks) =>
                ticks * 256000.0 / Stopwatch.Frequency;

            Logger.Info?.Print(
                LogClass.Gpu,
                $"[NEOV2-DRAW-STAGE-PROFILE1] windowMs={windowTicks * 1000.0 / Stopwatch.Frequency:F0} samples={_drawIndexedProfileSamples} " +
                $"indexMs={EstimatedMs(_drawIndexedProfileIndexTicks):F1} " +
                $"pipelineMs={EstimatedMs(_drawIndexedProfilePipelineTicks):F1} " +
                $"prepMs={EstimatedMs(_drawPipelineProfilePrepTicks):F1} " +
                $"dynamicMs={EstimatedMs(_drawPipelineProfileDynamicTicks):F1} " +
                $"buffersMs={EstimatedMs(_drawPipelineProfileBufferTicks):F1} " +
                $"bindingBarrierMs={EstimatedMs(_drawPipelineProfileBindingBarrierTicks):F1} " +
                $"createMs={EstimatedMs(_drawPipelineProfileCreateTicks):F1} " +
                $"extendedMs={EstimatedMs(_drawPipelineProfileExtendedDynamicTicks):F1} " +
                $"barrierFlushMs={EstimatedMs(_drawPipelineProfileBarrierFlushTicks):F1} " +
                $"descriptorMs={EstimatedMs(_drawPipelineProfileDescriptorTicks):F1} " +
                $"createN={_drawPipelineProfileCreateCount} vertexDirtyN={_drawPipelineProfileVertexDirtyCount} bindingDirtyN={_drawPipelineProfileBindingDirtyCount} " +
                $"renderPassMs={EstimatedMs(_drawIndexedProfileRenderPassTicks):F1} " +
                $"preDrawMs={EstimatedMs(_drawIndexedProfilePreDrawTicks):F1} " +
                $"issueMs={EstimatedMs(_drawIndexedProfileIssueTicks):F1} " +
                $"totalMs={EstimatedMs(_drawIndexedProfileTotalTicks):F1}");

            _drawIndexedProfileSamples = 0;
            _drawIndexedProfileIndexTicks = 0;
            _drawIndexedProfilePipelineTicks = 0;
            _drawIndexedProfileRenderPassTicks = 0;
            _drawIndexedProfilePreDrawTicks = 0;
            _drawIndexedProfileIssueTicks = 0;
            _drawIndexedProfileTotalTicks = 0;
            _drawPipelineProfilePrepTicks = 0;
            _drawPipelineProfileDynamicTicks = 0;
            _drawPipelineProfileBufferTicks = 0;
            _drawPipelineProfileBindingBarrierTicks = 0;
            _drawPipelineProfileCreateTicks = 0;
            _drawPipelineProfileExtendedDynamicTicks = 0;
            _drawPipelineProfileBarrierFlushTicks = 0;
            _drawPipelineProfileDescriptorTicks = 0;
            _drawPipelineProfileCreateCount = 0;
            _drawPipelineProfileVertexDirtyCount = 0;
            _drawPipelineProfileBindingDirtyCount = 0;
            _drawIndexedProfileWindowStart = now;
        }

        public void DrawIndexedIndirect(BufferRange indirectBuffer)
        {
            Buffer buffer = Gd.BufferManager
                .GetBuffer(CommandBuffer, indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, false)
                .Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;

            UpdateIndexBufferPattern();

            if (!RecreateGraphicsPipelineIfNeeded())
            {
                return;
            }

            BeginRenderPass();
            DrawCount++;
            RecordMua3Command(Mua3CommandKind.DrawIndexedIndirect, indirectBuffer.Size, 1);

            if (ShouldSkipLm3FragmentDraw())
            {
                return;
            }

            if (_indexBufferPattern != null)
            {
                // Convert the index buffer into a supported topology.
                IndexBufferPattern pattern = _indexBufferPattern;

                Auto<DisposableBuffer> indirectBufferAuto = _indexBuffer.BindConvertedIndexBufferIndirect(
                    Gd,
                    Cbs,
                    indirectBuffer,
                    BufferRange.Empty,
                    pattern,
                    false,
                    1,
                    indirectBuffer.Size);

                _needsIndexBufferRebind = false;

                BeginRenderPass(); // May have been interrupted to set buffer data.
                ResumeTransformFeedbackInternal();

                BeginHostConditionalRenderingForDraw();
                Gd.Api.CmdDrawIndexedIndirect(CommandBuffer, indirectBufferAuto.Get(Cbs, 0, indirectBuffer.Size).Value, 0, 1, (uint)indirectBuffer.Size);
                EndHostConditionalRenderingForDraw();
            }
            else
            {
                ResumeTransformFeedbackInternal();

                BeginHostConditionalRenderingForDraw();
                Gd.Api.CmdDrawIndexedIndirect(CommandBuffer, buffer, (ulong)indirectBuffer.Offset, 1, (uint)indirectBuffer.Size);
                EndHostConditionalRenderingForDraw();
            }
        }

        public void DrawIndexedIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            Buffer countBuffer = Gd.BufferManager
                .GetBuffer(CommandBuffer, parameterBuffer.Handle, parameterBuffer.Offset, parameterBuffer.Size, false)
                .Get(Cbs, parameterBuffer.Offset, parameterBuffer.Size).Value;

            Buffer buffer = Gd.BufferManager
                .GetBuffer(CommandBuffer, indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, false)
                .Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;

            UpdateIndexBufferPattern();

            if (!RecreateGraphicsPipelineIfNeeded())
            {
                return;
            }

            BeginRenderPass();
            DrawCount++;
            RecordMua3Command(Mua3CommandKind.DrawIndexedIndirectCount, maxDrawCount, stride);

            if (ShouldSkipLm3FragmentDraw())
            {
                return;
            }

            if (_indexBufferPattern != null)
            {
                // Convert the index buffer into a supported topology.
                IndexBufferPattern pattern = _indexBufferPattern;

                Auto<DisposableBuffer> indirectBufferAuto = _indexBuffer.BindConvertedIndexBufferIndirect(
                    Gd,
                    Cbs,
                    indirectBuffer,
                    parameterBuffer,
                    pattern,
                    true,
                    maxDrawCount,
                    stride);

                _needsIndexBufferRebind = false;

                BeginRenderPass(); // May have been interrupted to set buffer data.
                ResumeTransformFeedbackInternal();

                if (Gd.Capabilities.SupportsIndirectParameters)
                {
                    BeginHostConditionalRenderingForDraw();
                    Gd.DrawIndirectCountApi.CmdDrawIndexedIndirectCount(
                        CommandBuffer,
                        indirectBufferAuto.Get(Cbs, 0, indirectBuffer.Size).Value,
                        0,
                        countBuffer,
                        (ulong)parameterBuffer.Offset,
                        (uint)maxDrawCount,
                        (uint)stride);
                    EndHostConditionalRenderingForDraw();
                }
                else
                {
                    // This is also fine because the indirect data conversion always zeros
                    // the entries that are past the current draw count.

                    BeginHostConditionalRenderingForDraw();
                    Gd.Api.CmdDrawIndexedIndirect(
                        CommandBuffer,
                        indirectBufferAuto.Get(Cbs, 0, indirectBuffer.Size).Value,
                        0,
                        (uint)maxDrawCount,
                        (uint)stride);
                    EndHostConditionalRenderingForDraw();
                }
            }
            else
            {
                ResumeTransformFeedbackInternal();

                if (Gd.Capabilities.SupportsIndirectParameters)
                {
                    BeginHostConditionalRenderingForDraw();
                    Gd.DrawIndirectCountApi.CmdDrawIndexedIndirectCount(
                        CommandBuffer,
                        buffer,
                        (ulong)indirectBuffer.Offset,
                        countBuffer,
                        (ulong)parameterBuffer.Offset,
                        (uint)maxDrawCount,
                        (uint)stride);
                    EndHostConditionalRenderingForDraw();
                }
                else
                {
                    // Not fully correct, but we can't do much better if the host does not support indirect count.
                    BeginHostConditionalRenderingForDraw();
                    Gd.Api.CmdDrawIndexedIndirect(
                        CommandBuffer,
                        buffer,
                        (ulong)indirectBuffer.Offset,
                        (uint)maxDrawCount,
                        (uint)stride);
                    EndHostConditionalRenderingForDraw();
                }
            }
        }

        public void DrawIndirect(BufferRange indirectBuffer)
        {
            // TODO: Support quads and other unsupported topologies.

            Buffer buffer = Gd.BufferManager
                .GetBuffer(CommandBuffer, indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, false)
                .Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size, false).Value;

            if (!RecreateGraphicsPipelineIfNeeded())
            {
                return;
            }

            BeginRenderPass();
            ResumeTransformFeedbackInternal();
            DrawCount++;
            RecordMua3Command(Mua3CommandKind.DrawIndirect, indirectBuffer.Size, 1);

            if (ShouldSkipLm3FragmentDraw())
            {
                return;
            }

            BeginHostConditionalRenderingForDraw();
            Gd.Api.CmdDrawIndirect(CommandBuffer, buffer, (ulong)indirectBuffer.Offset, 1, (uint)indirectBuffer.Size);
            EndHostConditionalRenderingForDraw();
        }

        public void DrawIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            if (!Gd.Capabilities.SupportsIndirectParameters)
            {
                // TODO: Fallback for when this is not supported.
                throw new NotSupportedException();
            }

            Buffer buffer = Gd.BufferManager
                .GetBuffer(CommandBuffer, indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, false)
                .Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size, false).Value;

            Buffer countBuffer = Gd.BufferManager
                .GetBuffer(CommandBuffer, parameterBuffer.Handle, parameterBuffer.Offset, parameterBuffer.Size, false)
                .Get(Cbs, parameterBuffer.Offset, parameterBuffer.Size, false).Value;

            // TODO: Support quads and other unsupported topologies.

            if (!RecreateGraphicsPipelineIfNeeded())
            {
                return;
            }

            BeginRenderPass();
            ResumeTransformFeedbackInternal();
            DrawCount++;
            RecordMua3Command(Mua3CommandKind.DrawIndirectCount, maxDrawCount, stride);

            if (ShouldSkipLm3FragmentDraw())
            {
                return;
            }

            BeginHostConditionalRenderingForDraw();
            Gd.DrawIndirectCountApi.CmdDrawIndirectCount(
                CommandBuffer,
                buffer,
                (ulong)indirectBuffer.Offset,
                countBuffer,
                (ulong)parameterBuffer.Offset,
                (uint)maxDrawCount,
                (uint)stride);
            EndHostConditionalRenderingForDraw();
        }

        public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion)
        {
            if (texture is TextureView srcTexture)
            {
                CullModeFlags oldCullMode = _newState.CullMode;
                bool oldStencilTestEnable = _newState.StencilTestEnable;
                bool oldDepthTestEnable = _newState.DepthTestEnable;
                bool oldDepthWriteEnable = _newState.DepthWriteEnable;
                Array16<Silk.NET.Vulkan.Viewport> oldViewports = DynamicState.Viewports;
                uint oldViewportsCount = _newState.ViewportsCount;
                PrimitiveTopology oldTopology = _topology;

                _newState.CullMode = CullModeFlags.None;
                _newState.StencilTestEnable = false;
                _newState.DepthTestEnable = false;
                _newState.DepthWriteEnable = false;
                SignalStateChange();

                Gd.HelperShader.DrawTexture(
                    Gd,
                    this,
                    srcTexture,
                    sampler,
                    srcRegion,
                    dstRegion);

                _newState.CullMode = oldCullMode;
                _newState.StencilTestEnable = oldStencilTestEnable;
                _newState.DepthTestEnable = oldDepthTestEnable;
                _newState.DepthWriteEnable = oldDepthWriteEnable;
                SetPrimitiveTopology(oldTopology);

                DynamicState.SetViewports(ref oldViewports, oldViewportsCount);

                _newState.ViewportsCount = oldViewportsCount;
                SignalStateChange();
            }
        }

        public void EndTransformFeedback()
        {
            Gd.Barriers.EnableTfbBarriers(false);
            PauseTransformFeedbackInternal();
            _tfEnabled = false;
        }

        public bool IsCommandBufferActive(CommandBuffer cb)
        {
            return CommandBuffer.Handle == cb.Handle;
        }

        internal void Rebind(Auto<DisposableBuffer> buffer, int offset, int size)
        {
            _descriptorSetUpdater.Rebind(buffer, offset, size);

            if (_indexBuffer.Overlaps(buffer, offset, size))
            {
                _indexBuffer.BindIndexBuffer(Gd, Cbs);
            }

            for (int i = 0; i < _vertexBuffers.Length; i++)
            {
                if (_vertexBuffers[i].Overlaps(buffer, offset, size))
                {
                    _vertexBuffers[i].BindVertexBuffer(Gd, Cbs, (uint)i, ref _newState, _vertexBufferUpdater);
                }
            }

            _vertexBufferUpdater.Commit(Cbs);
        }

        public void SetAlphaTest(bool enable, float reference, CompareOp op)
        {
            // This is currently handled using shader specialization, as Vulkan does not support alpha test.
            // In the future, we may want to use this to write the reference value into the support buffer,
            // to avoid creating one version of the shader per reference value used.
        }

        public void SetBlendState(AdvancedBlendDescriptor blend)
        {
            Span<PipelineColorBlendAttachmentState> colorBlendAttachmentStateSpan = _newState.Internal.ColorBlendAttachmentState.AsSpan();
            
            for (int index = 0; index < Constants.MaxRenderTargets; index++)
            {
                ref PipelineColorBlendAttachmentState vkBlend = ref colorBlendAttachmentStateSpan[index];

                if (index == 0)
                {
                    BlendOp blendOp = blend.Op.Convert();

                    vkBlend = new PipelineColorBlendAttachmentState(
                        blendEnable: true,
                        colorBlendOp: blendOp,
                        alphaBlendOp: blendOp,
                        colorWriteMask: vkBlend.ColorWriteMask);

                    if (Gd.Capabilities.SupportsBlendEquationAdvancedNonPreMultipliedSrcColor)
                    {
                        _newState.AdvancedBlendSrcPreMultiplied = blend.SrcPreMultiplied;
                    }

                    if (Gd.Capabilities.SupportsBlendEquationAdvancedCorrelatedOverlap)
                    {
                        _newState.AdvancedBlendOverlap = blend.Overlap.Convert();
                    }
                }
                else
                {
                    vkBlend = new PipelineColorBlendAttachmentState(
                        colorWriteMask: vkBlend.ColorWriteMask);
                }

                if (vkBlend.ColorWriteMask == 0)
                {
                    _storedBlend[index] = vkBlend;

                    vkBlend = new PipelineColorBlendAttachmentState();
                }
            }

            SignalStateChange();
        }

        public void SetBlendState(int index, BlendDescriptor blend)
        {
            ref PipelineColorBlendAttachmentState vkBlend = ref _newState.Internal.ColorBlendAttachmentState[index];

            if (blend.Enable)
            {
                vkBlend.BlendEnable = blend.Enable;
                vkBlend.SrcColorBlendFactor = blend.ColorSrcFactor.Convert();
                vkBlend.DstColorBlendFactor = blend.ColorDstFactor.Convert();
                vkBlend.ColorBlendOp = blend.ColorOp.Convert();
                vkBlend.SrcAlphaBlendFactor = blend.AlphaSrcFactor.Convert();
                vkBlend.DstAlphaBlendFactor = blend.AlphaDstFactor.Convert();
                vkBlend.AlphaBlendOp = blend.AlphaOp.Convert();
            }
            else
            {
                vkBlend = new PipelineColorBlendAttachmentState(
                    colorWriteMask: vkBlend.ColorWriteMask);
            }

            if (vkBlend.ColorWriteMask == 0)
            {
                _storedBlend[index] = vkBlend;

                vkBlend = new PipelineColorBlendAttachmentState();
            }

            DynamicState.SetBlendConstants(
                blend.BlendConstant.Red,
                blend.BlendConstant.Green,
                blend.BlendConstant.Blue,
                blend.BlendConstant.Alpha);

            // Reset advanced blend state back defaults to the cache to help the pipeline cache.
            _newState.AdvancedBlendSrcPreMultiplied = true;
            _newState.AdvancedBlendDstPreMultiplied = true;
            _newState.AdvancedBlendOverlap = BlendOverlapEXT.UncorrelatedExt;

            SignalStateChange();
        }

        public void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp)
        {
            DynamicState.SetDepthBias(factor, units, clamp);

            _newState.DepthBiasEnable = enables != 0;
            SignalStateChange();
        }

        public void SetDepthClamp(bool clamp)
        {
            _newState.DepthClampEnable = clamp;
            SignalStateChange();
        }

        public void SetDepthMode(DepthMode mode)
        {
            bool oldMode = _newState.DepthMode;
            _newState.DepthMode = mode == DepthMode.MinusOneToOne;
            if (_newState.DepthMode != oldMode)
            {
                SignalStateChange();
            }
        }

        public void SetDepthTest(DepthTestDescriptor depthTest)
        {
            _newState.DepthTestEnable = depthTest.TestEnable;
            _newState.DepthWriteEnable = depthTest.WriteEnable;
            _newState.DepthCompareOp = depthTest.Func.Convert();

            UpdatePassDepthStencil();
            SignalStateChange();
        }

        public void SetFaceCulling(bool enable, Face face)
        {
            _newState.CullMode = enable ? face.Convert() : CullModeFlags.None;
            SignalStateChange();
        }

        public void SetFrontFace(FrontFace frontFace)
        {
            _newState.FrontFace = frontFace.Convert();
            SignalStateChange();
        }

        public void SetImage(ShaderStage stage, int binding, ITexture image)
        {
            _descriptorSetUpdater.SetImage(Cbs, stage, binding, image);

            if (stage == ShaderStage.Fragment && image is TextureView view)
            {
                FramebufferParams?.SetVirtualSize((uint)view.Width, (uint)view.Height, (uint)view.Layers);
            }
        }

        public void SetImage(int binding, Auto<DisposableImageView> image)
        {
            _descriptorSetUpdater.SetImage(binding, image);
        }

        public void SetImageArray(ShaderStage stage, int binding, IImageArray array)
        {
            _descriptorSetUpdater.SetImageArray(Cbs, stage, binding, array);
        }

        public void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array)
        {
            _descriptorSetUpdater.SetImageArraySeparate(Cbs, stage, setIndex, array);
        }

        public void SetIndexBuffer(BufferRange buffer, IndexType type)
        {
            // NEOV2 INDEXBIND-DIRECT1: the exact-binding guard hit only 0.074% in
            // TotK while running for every indexed draw. Keep this hot path direct.
            _indexBuffer = buffer.Handle != BufferHandle.Null
                ? new IndexBufferState(buffer.Handle, buffer.Offset, buffer.Size, type.Convert())
                : IndexBufferState.Null;

            _needsIndexBufferRebind = true;
        }

        public void SetLineParameters(float width, bool smooth)
        {
            _newState.LineWidth = width;
            SignalStateChange();
        }

        public void SetLogicOpState(bool enable, LogicalOp op)
        {
            _newState.LogicOpEnable = enable;
            _newState.LogicOp = op.Convert();
            SignalStateChange();
        }

        public void SetMultisampleState(MultisampleDescriptor multisample)
        {
            _newState.AlphaToCoverageEnable = multisample.AlphaToCoverageEnable;
            _newState.AlphaToOneEnable = multisample.AlphaToOneEnable;
            SignalStateChange();
        }

        public void SetPatchParameters(int vertices, ReadOnlySpan<float> defaultOuterLevel, ReadOnlySpan<float> defaultInnerLevel)
        {
            _newState.PatchControlPoints = (uint)vertices;
            SignalStateChange();

            // TODO: Default levels (likely needs emulation on shaders?)
        }

        public void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin)
        {
            // TODO.
        }

        public void SetPolygonMode(PolygonMode frontMode, PolygonMode backMode)
        {
            // TODO.
        }

        public void SetPrimitiveRestart(bool enable, int index)
        {
            _newState.PrimitiveRestartEnable = enable;
            // TODO: What to do about the index?
            SignalStateChange();
        }

        public void SetPrimitiveTopology(PrimitiveTopology topology)
        {
            _topology = topology;

            Silk.NET.Vulkan.PrimitiveTopology vkTopology = Gd.TopologyRemap(topology).Convert();

            _newState.Topology = vkTopology;

            SignalStateChange();
        }

        public void SetProgram(IProgram program)
        {
            ShaderCollection internalProgram = (ShaderCollection)program;

            if (_program == internalProgram)
            {
                return;
            }

            PipelineShaderStageCreateInfo[] stages = internalProgram.GetInfos();

            _program = internalProgram;

            // TOTK-DEPTHWRITE-TARGET15C
            // O cache de extended dynamic state depende apenas dos valores de estado,
            // nao do shader. Para o teste por hash, force a reaplicacao normal do
            // estado quando o programa mudar.
            if (!string.IsNullOrWhiteSpace(_totkDepthWriteOffTargetHash))
            {
                _extendedDynamicStateValid = false;
            }

            _descriptorSetUpdater.SetProgram(Cbs, internalProgram, _currentPipelineHandle != 0);
            _bindingBarriersDirty = true;

            _newState.PipelineLayout = internalProgram.PipelineLayout;
            _newState.HasTessellationControlShader = internalProgram.HasTessellationControlShader;
            _newState.StagesCount = (uint)stages.Length;

            stages.CopyTo(_newState.Stages.AsSpan()[..stages.Length]);

            SignalStateChange();

            if (internalProgram.IsCompute)
            {
                EndRenderPass();
            }
        }

        public void Specialize<T>(in T data) where T : unmanaged
        {
            ReadOnlySpan<byte> dataSpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in data), 1));

            if (!dataSpan.SequenceEqual(_newState.SpecializationData.Span))
            {
                _newState.SpecializationData = new SpecData(dataSpan);

                SignalStateChange();
            }
        }

        protected virtual void SignalAttachmentChange()
        {
        }

        public void SetRasterizerDiscard(bool discard)
        {
            _newState.RasterizerDiscardEnable = discard;
            SignalStateChange();

            if (!discard && Gd.IsQualcommProprietary)
            {
                // On Adreno, enabling rasterizer discard somehow corrupts the viewport state.
                // Force it to be updated on next use to work around this bug.
                DynamicState.ForceAllDirty();
            }
        }

        public void SetRenderTargetColorMasks(ReadOnlySpan<uint> componentMask)
        {
            int count = Math.Min(Constants.MaxRenderTargets, componentMask.Length);
            int writtenAttachments = 0;
            
            Span<PipelineColorBlendAttachmentState> colorBlendAttachmentStateSpan = _newState.Internal.ColorBlendAttachmentState.AsSpan();

            for (int i = 0; i < count; i++)
            {
                ref PipelineColorBlendAttachmentState vkBlend = ref colorBlendAttachmentStateSpan[i];
                ColorComponentFlags newMask = (ColorComponentFlags)componentMask[i];

                // When color write mask is 0, remove all blend state to help the pipeline cache.
                // Restore it when the mask becomes non-zero.
                if (vkBlend.ColorWriteMask != newMask)
                {
                    if (newMask == 0)
                    {
                        _storedBlend[i] = vkBlend;

                        vkBlend = new PipelineColorBlendAttachmentState();
                    }
                    else if (vkBlend.ColorWriteMask == 0)
                    {
                        vkBlend = _storedBlend[i];
                    }
                }

                vkBlend.ColorWriteMask = newMask;

                if (componentMask[i] != 0)
                {
                    writtenAttachments++;
                }
            }

            if (_framebufferUsingColorWriteMask)
            {
                SetRenderTargetsInternal(_preMaskColors, _preMaskDepthStencil, true);
            }
            else
            {
                SignalStateChange();

                if (writtenAttachments != _writtenAttachmentCount)
                {
                    SignalAttachmentChange();
                    _writtenAttachmentCount = writtenAttachments;
                }
            }
        }

        private void SetRenderTargetsInternal(Span<ITexture> colors, ITexture depthStencil, bool filterWriteMasked)
        {
            // NEOV2 Etapa 5: clears adiados pertencem aos render targets atuais; aplica antes de trocar.
            if (_pendingClearMask != 0)
            {
                EndRenderPass();
            }

            CreateFramebuffer(colors, depthStencil, filterWriteMasked);
            CreateRenderPass();
            SignalStateChange();
            SignalAttachmentChange();
        }

        public void SetRenderTargets(Span<ITexture> colors, ITexture depthStencil)
        {
            _framebufferUsingColorWriteMask = false;
            SetRenderTargetsInternal(colors, depthStencil, Gd.IsTBDR);
        }

        public void SetScissors(ReadOnlySpan<Rectangle<int>> regions)
        {
            int maxScissors = Gd.Capabilities.SupportsMultiView ? Constants.MaxViewports : 1;
            int count = Math.Min(maxScissors, regions.Length);
            if (count > 0)
            {
                ClearScissor = regions[0];
            }

            for (int i = 0; i < count; i++)
            {
                Rectangle<int> region = regions[i];
                Offset2D offset = new(region.X, region.Y);
                Extent2D extent = new((uint)region.Width, (uint)region.Height);

                DynamicState.SetScissor(i, new Rect2D(offset, extent));
            }

            DynamicState.ScissorsCount = count;

            _newState.ScissorsCount = (uint)count;
            SignalStateChange();
        }

        public void SetStencilTest(StencilTestDescriptor stencilTest)
        {
            DynamicState.SetStencilMasks(
                (uint)stencilTest.BackFuncMask,
                (uint)stencilTest.BackMask,
                (uint)stencilTest.BackFuncRef,
                (uint)stencilTest.FrontFuncMask,
                (uint)stencilTest.FrontMask,
                (uint)stencilTest.FrontFuncRef);

            _newState.StencilTestEnable = stencilTest.TestEnable;
            _newState.StencilBackFailOp = stencilTest.BackSFail.Convert();
            _newState.StencilBackPassOp = stencilTest.BackDpPass.Convert();
            _newState.StencilBackDepthFailOp = stencilTest.BackDpFail.Convert();
            _newState.StencilBackCompareOp = stencilTest.BackFunc.Convert();
            _newState.StencilFrontFailOp = stencilTest.FrontSFail.Convert();
            _newState.StencilFrontPassOp = stencilTest.FrontDpPass.Convert();
            _newState.StencilFrontDepthFailOp = stencilTest.FrontDpFail.Convert();
            _newState.StencilFrontCompareOp = stencilTest.FrontFunc.Convert();

            UpdatePassDepthStencil();
            SignalStateChange();
        }

        public void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _descriptorSetUpdater.SetStorageBuffers(CommandBuffer, buffers);
        }

        public void SetStorageBuffers(int first, ReadOnlySpan<Auto<DisposableBuffer>> buffers)
        {
            _descriptorSetUpdater.SetStorageBuffers(CommandBuffer, first, buffers);
        }

        public void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
        {
            _descriptorSetUpdater.SetTextureAndSampler(Cbs, stage, binding, texture, sampler);
        }

        public void SetTextureAndSamplerIdentitySwizzle(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
        {
            _descriptorSetUpdater.SetTextureAndSamplerIdentitySwizzle(Cbs, stage, binding, texture, sampler);
        }

        public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array)
        {
            _descriptorSetUpdater.SetTextureArray(Cbs, stage, binding, array);
        }

        public void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array)
        {
            _descriptorSetUpdater.SetTextureArraySeparate(Cbs, stage, setIndex, array);
        }

        public void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers)
        {
            PauseTransformFeedbackInternal();

            int count = Math.Min(Constants.MaxTransformFeedbackBuffers, buffers.Length);

            for (int i = 0; i < count; i++)
            {
                BufferRange range = buffers[i];

                _transformFeedbackBuffers[i].Dispose();

                if (range.Handle != BufferHandle.Null)
                {
                    _transformFeedbackBuffers[i] =
                        new BufferState(Gd.BufferManager.GetBuffer(CommandBuffer, range.Handle, range.Offset, range.Size, true), range.Offset, range.Size);
                    _transformFeedbackBuffers[i].BindTransformFeedbackBuffer(Gd, Cbs, (uint)i);
                }
                else
                {
                    _transformFeedbackBuffers[i] = BufferState.Null;
                }
            }
        }

        public void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _descriptorSetUpdater.SetUniformBuffers(CommandBuffer, buffers);
        }

        public void SetUserClipDistance(int index, bool enableClip)
        {
            // TODO.
        }

        public void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            FormatCapabilities formatCapabilities = Gd.FormatCapabilities;

            Span<int> newVbScalarSizes = stackalloc int[Constants.MaxVertexBuffers];

            int count = Math.Min(Constants.MaxVertexAttributes, vertexAttribs.Length);
            uint dirtyVbSizes = 0;
            
            Span<VertexInputAttributeDescription> vertexAttributeDescriptionsSpan = _newState.Internal.VertexAttributeDescriptions.AsSpan();

            for (int i = 0; i < count; i++)
            {
                VertexAttribDescriptor attribute = vertexAttribs[i];
                int rawIndex = attribute.BufferIndex;
                int bufferIndex = attribute.IsZero ? 0 : rawIndex + 1;

                if (!attribute.IsZero)
                {
                    newVbScalarSizes[rawIndex] = Math.Max(newVbScalarSizes[rawIndex], attribute.Format.GetScalarSize());
                    dirtyVbSizes |= 1u << rawIndex;
                }

                vertexAttributeDescriptionsSpan[i] = new VertexInputAttributeDescription(
                    (uint)i,
                    (uint)bufferIndex,
                    formatCapabilities.ConvertToVertexVkFormat(attribute.Format),
                    (uint)attribute.Offset);
            }

            while (dirtyVbSizes != 0)
            {
                int dirtyBit = BitOperations.TrailingZeroCount(dirtyVbSizes);

                ref VertexBufferState buffer = ref _vertexBuffers[dirtyBit + 1];

                if (buffer.AttributeScalarAlignment != newVbScalarSizes[dirtyBit])
                {
                    _vertexBuffersDirty |= 1UL << (dirtyBit + 1);
                    buffer.AttributeScalarAlignment = newVbScalarSizes[dirtyBit];
                }

                dirtyVbSizes &= ~(1u << dirtyBit);
            }

            _newState.VertexAttributeDescriptionsCount = (uint)count;
            SignalStateChange();
        }

        public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            int count = Math.Min(Constants.MaxVertexBuffers, vertexBuffers.Length);

            Span<VertexInputBindingDescription> bindingDescriptions =
                _newState.Internal.VertexBindingDescriptions.AsSpan();
            VertexInputBindingDescription zeroBinding =
                new(0, 0, VertexInputRate.Vertex);
            bool pipelineStateChanged =
                bindingDescriptions[0].Binding != zeroBinding.Binding ||
                bindingDescriptions[0].Stride != zeroBinding.Stride ||
                bindingDescriptions[0].InputRate != zeroBinding.InputRate;

            bindingDescriptions[0] = zeroBinding;

            int validCount = 1;

            BufferHandle lastHandle = default;
            Auto<DisposableBuffer> lastBuffer = null;

            for (int i = 0; i < count; i++)
            {
                VertexBufferDescriptor vertexBuffer = vertexBuffers[i];

                // TODO: Support divisor > 1
                VertexInputRate inputRate = vertexBuffer.Divisor != 0 ? VertexInputRate.Instance : VertexInputRate.Vertex;

                if (vertexBuffer.Buffer.Handle != BufferHandle.Null)
                {
                    Auto<DisposableBuffer> vb = (vertexBuffer.Buffer.Handle == lastHandle) ? lastBuffer :
                        Gd.BufferManager.GetBuffer(CommandBuffer, vertexBuffer.Buffer.Handle, false);

                    lastHandle = vertexBuffer.Buffer.Handle;
                    lastBuffer = vb;

                    if (vb != null)
                    {
                        int binding = i + 1;
                        int descriptorIndex = validCount++;

                        VertexInputBindingDescription newDescription = new(
                            (uint)binding,
                            (uint)vertexBuffer.Stride,
                            inputRate);
                        ref VertexInputBindingDescription oldDescription =
                            ref bindingDescriptions[descriptorIndex];

                        pipelineStateChanged |=
                            oldDescription.Binding != newDescription.Binding ||
                            oldDescription.Stride != newDescription.Stride ||
                            oldDescription.InputRate != newDescription.InputRate;
                        oldDescription = newDescription;

                        int vbSize = vertexBuffer.Buffer.Size;

                        if (Gd.Vendor == Vendor.Amd && !Gd.IsMoltenVk && vertexBuffer.Stride > 0)
                        {
                            // AMD has a bug where if offset + stride * count is greater than
                            // the size, then the last attribute will have the wrong value.
                            // As a workaround, simply use the full buffer size.
                            int remainder = vbSize % vertexBuffer.Stride;
                            if (remainder != 0)
                            {
                                vbSize += vertexBuffer.Stride - remainder;
                            }
                        }

                        ref VertexBufferState buffer = ref _vertexBuffers[binding];
                        int oldScalarAlign = buffer.AttributeScalarAlignment;

                        if (Gd.Capabilities.VertexBufferAlignment < 2 &&
                            (vertexBuffer.Stride % FormatExtensions.MaxBufferFormatScalarSize) == 0)
                        {
                            if (!buffer.Matches(vb, descriptorIndex, vertexBuffer.Buffer.Offset, vbSize, vertexBuffer.Stride))
                            {
                                buffer.Dispose();

                                buffer = new VertexBufferState(
                                    vb,
                                    descriptorIndex,
                                    vertexBuffer.Buffer.Offset,
                                    vbSize,
                                    vertexBuffer.Stride);

                                buffer.BindVertexBuffer(Gd, Cbs, (uint)binding, ref _newState, _vertexBufferUpdater);
                            }
                        }
                        else
                        {
                            // May need to be rewritten. Bind this buffer before draw.

                            buffer.Dispose();

                            buffer = new VertexBufferState(
                                vertexBuffer.Buffer.Handle,
                                descriptorIndex,
                                vertexBuffer.Buffer.Offset,
                                vbSize,
                                vertexBuffer.Stride);

                            _vertexBuffersDirty |= 1UL << binding;
                        }

                        buffer.AttributeScalarAlignment = oldScalarAlign;
                    }
                }
            }

            _vertexBufferUpdater.Commit(Cbs);

            pipelineStateChanged |=
                _newState.VertexBindingDescriptionsCount != (uint)validCount;
            _newState.VertexBindingDescriptionsCount = (uint)validCount;

            _vertexBufferSetCalls++;
            _vertexBufferPipelineChanges += pipelineStateChanged ? 1UL : 0UL;

            if ((_vertexBufferSetCalls & 0x3ffff) == 0)
            {
                Logger.Info?.Print(
                    LogClass.Gpu,
                    $"[NEOV2-VERTEXSTATE-DEDUP1] calls={_vertexBufferSetCalls} pipelineChanges={_vertexBufferPipelineChanges}");
            }

            // NEOV2 VERTEXSTATE-DEDUP1: buffer handles, offsets and sizes are dynamic
            // bindings and do not form part of the Vulkan graphics pipeline. Revalidate the
            // pipeline only when stride, input rate, binding index or count actually changes.
            if (pipelineStateChanged)
            {
                SignalStateChange();
            }
        }

        public void SetViewports(ReadOnlySpan<Viewport> viewports)
        {
            int maxViewports = Gd.Capabilities.SupportsMultiView ? Constants.MaxViewports : 1;
            int count = Math.Min(maxViewports, viewports.Length);

            static float Clamp(float value)
            {
                return Math.Clamp(value, 0f, 1f);
            }

            DynamicState.ViewportsCount = (uint)count;

            for (int i = 0; i < count; i++)
            {
                Viewport viewport = viewports[i];

                DynamicState.SetViewport(i, new Silk.NET.Vulkan.Viewport(
                    viewport.Region.X,
                    viewport.Region.Y,
                    viewport.Region.Width == 0f ? 1f : viewport.Region.Width,
                    viewport.Region.Height == 0f ? 1f : viewport.Region.Height,
                    Clamp(viewport.DepthNear),
                    Clamp(viewport.DepthFar)));
            }

            _newState.ViewportsCount = (uint)count;
            SignalStateChange();
        }

        public void SwapBuffer(Auto<DisposableBuffer> from, Auto<DisposableBuffer> to)
        {
            _indexBuffer.Swap(from, to);

            for (int i = 0; i < _vertexBuffers.Length; i++)
            {
                _vertexBuffers[i].Swap(from, to);
            }

            for (int i = 0; i < _transformFeedbackBuffers.Length; i++)
            {
                _transformFeedbackBuffers[i].Swap(from, to);
            }

            _descriptorSetUpdater.SwapBuffer(from, to);

            SignalCommandBufferChange();
        }

        public void ForceTextureDirty()
        {
            _descriptorSetUpdater.ForceTextureDirty();
        }

        public void ForceImageDirty()
        {
            _descriptorSetUpdater.ForceImageDirty();
        }

        public unsafe void TextureBarrier()
        {
            Gd.Barriers.QueueTextureBarrier();
        }

        public void TextureBarrierTiled()
        {
            TextureBarrier();
        }

        protected void SignalCommandBufferChange()
        {
            _needsIndexBufferRebind = true;
            _needsTransformFeedbackBuffersRebind = true;
            _vertexBuffersDirty = ulong.MaxValue >> (64 - _vertexBuffers.Length);

            _descriptorSetUpdater.SignalCommandBufferChange();
            DynamicState.ForceAllDirty();
            _currentPipelineHandle = 0;
            _extendedDynamicStateValid = false;
        }

        private void CreateFramebuffer(Span<ITexture> colors, ITexture depthStencil, bool filterWriteMasked)
        {
            if (filterWriteMasked)
            {
                // TBDR GPUs don't work properly if the same attachment is bound to multiple targets,
                // due to each attachment being a copy of the real attachment, rather than a direct write.

                // Just try to remove duplicate attachments.
                // Save a copy of the array to rebind when mask changes.

                void MaskOut(ReadOnlySpan<ITexture> colors)
                {
                    if (!_framebufferUsingColorWriteMask)
                    {
                        _preMaskColors = colors.ToArray();
                        _preMaskDepthStencil = depthStencil;
                    }

                    // If true, then the framebuffer must be recreated when the mask changes.
                    _framebufferUsingColorWriteMask = true;
                }

                // Look for textures that are masked out.

                Span<PipelineColorBlendAttachmentState> colorBlendAttachmentStateSpan =
                    _newState.Internal.ColorBlendAttachmentState.AsSpan();

                for (int i = 0; i < colors.Length; i++)
                {
                    if (colors[i] == null)
                    {
                        continue;
                    }

                    ref PipelineColorBlendAttachmentState vkBlend = ref colorBlendAttachmentStateSpan[i];

                    for (int j = 0; j < i; j++)
                    {
                        // Check each binding for a duplicate binding before it.

                        if (colors[i] == colors[j])
                        {
                            // Prefer the binding with no write mask.
                            ref PipelineColorBlendAttachmentState vkBlend2 = ref colorBlendAttachmentStateSpan[j];
                            if (vkBlend.ColorWriteMask == 0)
                            {
                                colors[i] = null;
                                MaskOut(colors);
                            }
                            else if (vkBlend2.ColorWriteMask == 0)
                            {
                                colors[j] = null;
                                MaskOut(colors);
                            }
                        }
                    }
                }
            }

            if (IsMainPipeline)
            {
                FramebufferParams?.ClearBindings();
            }

            FramebufferParams = FramebufferParams?.Update(colors, depthStencil) ?? new FramebufferParams(Device, colors, depthStencil);

            if (IsMainPipeline)
            {
                FramebufferParams.AddBindings();

                _newState.FeedbackLoopAspects = FeedbackLoopAspects.None;
                _bindingBarriersDirty = true;
            }

            _passWritesDepthStencil = false;
            UpdatePassDepthStencil();
            UpdatePipelineAttachmentFormats();
        }

        protected void UpdatePipelineAttachmentFormats()
        {
            Span<Format> dstAttachmentFormats = _newState.Internal.AttachmentFormats.AsSpan();
            FramebufferParams.AttachmentFormats.AsSpan(..FramebufferParams.AttachmentsCount).CopyTo(dstAttachmentFormats);
            _newState.Internal.AttachmentIntegerFormatMask = FramebufferParams.AttachmentIntegerFormatMask;
            _newState.Internal.LogicOpsAllowed = FramebufferParams.LogicOpsAllowed;

            for (int i = FramebufferParams.AttachmentsCount; i < dstAttachmentFormats.Length; i++)
            {
                dstAttachmentFormats[i] = 0;
            }

            _newState.ColorBlendAttachmentStateCount = (uint)(FramebufferParams.MaxColorAttachmentIndex + 1);
            _newState.HasDepthStencil = FramebufferParams.HasDepthStencil;
            _newState.SamplesCount = FramebufferParams.AttachmentsCount != 0 ? FramebufferParams.AttachmentSamples[0] : 1;
        }

        protected unsafe void CreateRenderPass()
        {
            bool hasFramebuffer = FramebufferParams != null;

            EndRenderPass();

            if (!hasFramebuffer || FramebufferParams.AttachmentsCount == 0)
            {
                // Use the null framebuffer.
                _nullRenderPass ??= new RenderPassHolder(Gd, Device, new RenderPassCacheKey(), FramebufferParams);

                _rpHolder = _nullRenderPass;
                _renderPass = _nullRenderPass.GetRenderPass();
                _framebuffer = _nullRenderPass.GetFramebuffer(Gd, Cbs, FramebufferParams);
            }
            else
            {
                (_rpHolder, _framebuffer) = FramebufferParams.GetPassAndFramebuffer(Gd, Device, Cbs);

                _renderPass = _rpHolder.GetRenderPass();
            }
        }

        protected void SignalStateChange()
        {
            _graphicsStateDirty = true;
            _computeStateDirty = true;
        }

        private void RecreateComputePipelineIfNeeded()
        {
            if (_computeStateDirty || Pbp != PipelineBindPoint.Compute)
            {
                CreatePipeline(PipelineBindPoint.Compute);
                _computeStateDirty = false;
                Pbp = PipelineBindPoint.Compute;

                if (_bindingBarriersDirty)
                {
                    // Stale barriers may have been activated by switching program. Emit any that are relevant.
                    _descriptorSetUpdater.InsertBindingBarriers(Cbs);

                    _bindingBarriersDirty = false;
                }
            }

            Gd.Barriers.Flush(Cbs, _program, _feedbackLoop != 0, RenderPassActive, _rpHolder, EndRenderPassDelegate);

            _descriptorSetUpdater.UpdateAndBindDescriptorSets(Cbs, PipelineBindPoint.Compute);
        }

        private bool ChangeFeedbackLoop(FeedbackLoopAspects aspects)
        {
            if (_feedbackLoop != aspects)
            {
                if (Gd.Capabilities.SupportsDynamicAttachmentFeedbackLoop)
                {
                    DynamicState.SetFeedbackLoop(aspects);
                }
                else
                {
                    _newState.FeedbackLoopAspects = aspects;
                }

                _feedbackLoop = aspects;

                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool UpdateFeedbackLoop()
        {
            List<TextureView> hazards = _descriptorSetUpdater.FeedbackLoopHazards;

            if ((hazards?.Count ?? 0) > 0)
            {
                FeedbackLoopAspects aspects = 0;

                foreach (TextureView view in hazards)
                {
                    // May need to enforce feedback loop layout here in the future.
                    // Though technically, it should always work with the general layout.

                    if (view.Info.Format.IsDepthOrStencil())
                    {
                        if (_passWritesDepthStencil)
                        {
                            // If depth/stencil isn't written in the pass, it doesn't count as a feedback loop.

                            aspects |= FeedbackLoopAspects.Depth;
                        }
                    }
                    else
                    {
                        aspects |= FeedbackLoopAspects.Color;
                    }
                }

                return ChangeFeedbackLoop(aspects);
            }
            else if (_feedbackLoop != 0)
            {
                return ChangeFeedbackLoop(FeedbackLoopAspects.None);
            }

            return false;
        }

        private void UpdatePassDepthStencil()
        {
            if (!RenderPassActive)
            {
                _passWritesDepthStencil = false;
            }

            // Stencil test being enabled doesn't necessarily mean a write, but it's not critical to check.
            _passWritesDepthStencil |= (_newState.DepthTestEnable && _newState.DepthWriteEnable) || _newState.StencilTestEnable;
        }

        // TOTK-0EEF-RTMASK2
        private void ApplyTotk0eefRtMask()
        {
            int requestedRt = -1;

            if (!string.IsNullOrWhiteSpace(_totk0eefMaskRt))
            {
                int.TryParse(_totk0eefMaskRt, out requestedRt);
            }

            bool is0eef =
                _program != null &&
                string.Equals(
                    _program.FragmentShaderHash,
                    "0eef97d9282a01ff0e5e354b8261000a589e2b7576c2696b344375446f7fd35f",
                    StringComparison.OrdinalIgnoreCase);

            bool shouldMask =
                is0eef &&
                requestedRt >= 0 &&
                requestedRt < Constants.MaxRenderTargets;

            if (_totk0eefRtMasked &&
                (!shouldMask || requestedRt != _totk0eefMaskedRt))
            {
                _newState.Internal.ColorBlendAttachmentState[_totk0eefMaskedRt] =
                    _totk0eefSavedRtState;

                _totk0eefRtMasked = false;
                _totk0eefMaskedRt = -1;
                SignalStateChange();
            }

            if (shouldMask && !_totk0eefRtMasked)
            {
                ref PipelineColorBlendAttachmentState rt =
                    ref _newState.Internal.ColorBlendAttachmentState[requestedRt];

                _totk0eefSavedRtState = rt;
                _totk0eefMaskedRt = requestedRt;

                rt.ColorWriteMask = 0;

                _totk0eefRtMasked = true;
                SignalStateChange();

                lock (_totkTraceLock)
                {
                    if (_totk0eefLoggedMaskRts.Add(requestedRt))
                    {
                        Logger.Info?.Print(
                            LogClass.Gpu,
                            $"[TOTK-0EEF-RTMASK2] ACTIVE RT={requestedRt}");
                    }
                }
            }
        }

        // TOTK-7F0FA-DEPTHWRITE-OFF13
        private static readonly bool _totk7f0faDepthWriteOff =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_7F0FA_DEPTHWRITE_OFF") == "1";

        private bool _totk7f0faDepthWriteOffLogged;

        // TOTK-6451-DEPTHWRITE-OFF14
        private static readonly bool _totk6451DepthWriteOff =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_6451_DEPTHWRITE_OFF") == "1";

        private bool _totk6451DepthWriteOffLogged;

        // TOTK-DEPTHWRITE-TARGET15
        private static readonly string _totkDepthWriteOffTargetHash =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_DEPTHWRITE_OFF_HASH");

        private bool _totkDepthWriteOffTargetLogged;

        private bool GetTotkDepthWriteEnable()
        {
            bool enabled = _newState.DepthWriteEnable;

            if (enabled &&
                !string.IsNullOrWhiteSpace(_totkDepthWriteOffTargetHash) &&
                _program != null &&
                string.Equals(
                    _program.FragmentShaderHash,
                    _totkDepthWriteOffTargetHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                TextureView totkTargetDepthView =
                    FramebufferParams?.GetDepthStencilView();

                if (totkTargetDepthView?.Storage != null &&
                    string.Equals(
                        totkTargetDepthView.Storage.VkFormat.ToString(),
                        "D32Sfloat",
                        StringComparison.Ordinal) &&
                    (
                        (totkTargetDepthView.Width == 1280 &&
                         totkTargetDepthView.Height == 720) ||
                        (totkTargetDepthView.Width == 1600 &&
                         totkTargetDepthView.Height == 900)
                    ))
                {
                    if (!_totkDepthWriteOffTargetLogged)
                    {
                        int totkTargetStorageId =
                            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(
                                totkTargetDepthView.Storage);

                        Logger.Info?.Print(
                            LogClass.Gpu,
                            $"[TOTK-DEPTHWRITE-TARGET15] ACTIVE " +
                            $"HASH={_program.FragmentShaderHash} " +
                            $"DRAW={DrawCount} " +
                            $"S={totkTargetStorageId:X} " +
                            $"DS={totkTargetDepthView.VkFormat}:" +
                            $"{totkTargetDepthView.Width}x{totkTargetDepthView.Height} " +
                            $"DT={_newState.DepthTestEnable} " +
                            $"guestDW={_newState.DepthWriteEnable} " +
                            $"hostDW=False");

                        _totkDepthWriteOffTargetLogged = true;
                    }

                    return false;
                }
            }

            if (_totk6451DepthWriteOff &&
                enabled &&
                _program != null &&
                string.Equals(
                    _program.FragmentShaderHash,
                    "6451d153996862a938aef16844d39b8c073ce2da74de0621114d002df9784b6c",
                    StringComparison.OrdinalIgnoreCase))
            {
                TextureView depthView6451 =
                    FramebufferParams?.GetDepthStencilView();

                if (depthView6451?.Storage != null &&
                    string.Equals(
                        depthView6451.Storage.VkFormat.ToString(),
                        "D32Sfloat",
                        StringComparison.Ordinal) &&
                    (
                        (depthView6451.Width == 1600 && depthView6451.Height == 900) ||
                        (depthView6451.Width == 1280 && depthView6451.Height == 720)
                    ))
                {
                    if (!_totk6451DepthWriteOffLogged)
                    {
                        int storageId =
                            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(
                                depthView6451.Storage);

                        Logger.Info?.Print(
                            LogClass.Gpu,
                            $"[TOTK-6451-DEPTHWRITE-OFF14] ACTIVE " +
                            $"HASH={_program.FragmentShaderHash} " +
                            $"DRAW={DrawCount} " +
                            $"S={storageId:X} " +
                            $"DS={depthView6451.VkFormat}:{depthView6451.Width}x{depthView6451.Height} " +
                            $"DT={_newState.DepthTestEnable} " +
                            $"guestDW={_newState.DepthWriteEnable} " +
                            $"hostDW=False");

                        _totk6451DepthWriteOffLogged = true;
                    }

                    return false;
                }
            }

            if (!_totk7f0faDepthWriteOff ||
                !enabled ||
                _program == null ||
                !string.Equals(
                    _program.FragmentShaderHash,
                    "7f0fa6eb658bd7929e1a305ee13e9524491f3c4e73d0dad0897d2c92ed59e541",
                    StringComparison.OrdinalIgnoreCase))
            {
                return enabled;
            }

            TextureView depthView =
                FramebufferParams?.GetDepthStencilView();

            if (depthView?.Storage == null ||
                !string.Equals(
                    depthView.Storage.VkFormat.ToString(),
                    "D32Sfloat",
                    StringComparison.Ordinal) ||
                !(
                    (depthView.Width == 1600 && depthView.Height == 900) ||
                    (depthView.Width == 1280 && depthView.Height == 720)
                 ))
            {
                return enabled;
            }

            if (!_totk7f0faDepthWriteOffLogged)
            {
                int storageId =
                    System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(
                        depthView.Storage);

                Logger.Info?.Print(
                    LogClass.Gpu,
                    $"[TOTK-7F0FA-DEPTHWRITE-OFF13] ACTIVE " +
                    $"HASH={_program.FragmentShaderHash} " +
                    $"S={storageId:X} " +
                    $"DS={depthView.VkFormat}:{depthView.Width}x{depthView.Height} " +
                    $"guestDepthWrite={_newState.DepthWriteEnable} " +
                    $"hostDepthWrite=False");

                _totk7f0faDepthWriteOffLogged = true;
            }

            return false;
        }

        private bool RecreateGraphicsPipelineIfNeeded()
        {
            // NEOV2-DRAW-PIPELINE-PROFILE2: sampled breakdown of the dominant
            // graphics backend path. Sampling is armed by DrawIndexed only.
            bool profile = _drawPipelineProfileActive;
            long profileStart = profile ? Stopwatch.GetTimestamp() : 0;

            InsertMua3SceneComputeBarrierIfNeeded();

            if (FramebufferParams != null &&
                FramebufferParams.AttachmentsCount == 0 &&
                FramebufferParams.Width == 1 &&
                FramebufferParams.Height == 1 &&
                DynamicState.ViewportsCount != 0)
            {
                // An attachmentless fragment pass can reach its first draw before the storage
                // image descriptor is rebound. At that point the null framebuffer still has
                // its constructor fallback of 1x1, even though the guest viewport already
                // describes the real render area. Seed the virtual framebuffer from that
                // viewport so the first storage-image draw is not clipped to one pixel.
                Silk.NET.Vulkan.Viewport viewport = DynamicState.Viewports[0];
                uint width = (uint)Math.Max(1f, Math.Abs(viewport.Width));
                uint height = (uint)Math.Max(1f, Math.Abs(viewport.Height));

                FramebufferParams.SetVirtualSize(width, height, 1);
            }

            if (AutoFlush.ShouldFlushDraw(DrawCount))
            {
                Gd.FlushAllCommands();
            }

            long profileAfterPrep = profile ? Stopwatch.GetTimestamp() : 0;

            DynamicState.ReplayIfDirty(Gd, CommandBuffer);

            long profileAfterDynamic = profile ? Stopwatch.GetTimestamp() : 0;

            if (_needsIndexBufferRebind && _indexBufferPattern == null)
            {
                _indexBuffer.BindIndexBuffer(Gd, Cbs);
                _needsIndexBufferRebind = false;
            }

            if (_needsTransformFeedbackBuffersRebind)
            {
                PauseTransformFeedbackInternal();

                for (int i = 0; i < Constants.MaxTransformFeedbackBuffers; i++)
                {
                    _transformFeedbackBuffers[i].BindTransformFeedbackBuffer(Gd, Cbs, (uint)i);
                }

                _needsTransformFeedbackBuffersRebind = false;
            }

            if (_vertexBuffersDirty != 0)
            {
                if (profile)
                {
                    _drawPipelineProfileVertexDirtyCount++;
                }

                while (_vertexBuffersDirty != 0)
                {
                    int i = BitOperations.TrailingZeroCount(_vertexBuffersDirty);

                    _vertexBuffers[i].BindVertexBuffer(Gd, Cbs, (uint)i, ref _newState, _vertexBufferUpdater);

                    _vertexBuffersDirty &= ~(1UL << i);
                }

                _vertexBufferUpdater.Commit(Cbs);
            }

            long profileAfterBuffers = profile ? Stopwatch.GetTimestamp() : 0;

            if (_bindingBarriersDirty)
            {
                if (profile)
                {
                    _drawPipelineProfileBindingDirtyCount++;
                }

                // Stale barriers may have been activated by switching program. Emit any that are relevant.
                _descriptorSetUpdater.InsertBindingBarriers(Cbs);

                _bindingBarriersDirty = false;
            }

            long profileAfterBindingBarriers = profile ? Stopwatch.GetTimestamp() : 0;

            // TOTK-0EEF-RTMASK2: apply after guest state updates, immediately before pipeline creation.
            ApplyTotk0eefRtMask();

            bool createPipeline = UpdateFeedbackLoop() || _graphicsStateDirty || Pbp != PipelineBindPoint.Graphics;

            if (createPipeline)
            {
                if (profile)
                {
                    _drawPipelineProfileCreateCount++;
                }

                if (!CreatePipeline(PipelineBindPoint.Graphics))
                {
                    return false;
                }

                _graphicsStateDirty = false;
                Pbp = PipelineBindPoint.Graphics;
            }

            long profileAfterCreate = profile ? Stopwatch.GetTimestamp() : 0;

            if (PipelineState.UsesExtendedDynamicState(Gd))
            {
                ApplyExtendedDynamicState();
            }

            long profileAfterExtendedDynamic = profile ? Stopwatch.GetTimestamp() : 0;

            Gd.Barriers.Flush(Cbs, _program, _feedbackLoop != 0, RenderPassActive, _rpHolder, EndRenderPassDelegate);

            long profileAfterBarrierFlush = profile ? Stopwatch.GetTimestamp() : 0;

            _descriptorSetUpdater.UpdateAndBindDescriptorSets(Cbs, PipelineBindPoint.Graphics);

            if (profile)
            {
                long profileEnd = Stopwatch.GetTimestamp();
                _drawPipelineProfilePrepTicks += profileAfterPrep - profileStart;
                _drawPipelineProfileDynamicTicks += profileAfterDynamic - profileAfterPrep;
                _drawPipelineProfileBufferTicks += profileAfterBuffers - profileAfterDynamic;
                _drawPipelineProfileBindingBarrierTicks += profileAfterBindingBarriers - profileAfterBuffers;
                _drawPipelineProfileCreateTicks += profileAfterCreate - profileAfterBindingBarriers;
                _drawPipelineProfileExtendedDynamicTicks += profileAfterExtendedDynamic - profileAfterCreate;
                _drawPipelineProfileBarrierFlushTicks += profileAfterBarrierFlush - profileAfterExtendedDynamic;
                _drawPipelineProfileDescriptorTicks += profileEnd - profileAfterBarrierFlush;
            }

            return true;
        }

        private void ApplyExtendedDynamicState()
        {
            ulong key = _newState.Internal.Id6 & PipelineState.ExtendedDynamicStateMask;

            if (_extendedDynamicStateValid && key == _extendedDynamicStateApplied)
            {
                return;
            }

            Silk.NET.Vulkan.Extensions.EXT.ExtExtendedDynamicState api = Gd.ExtendedDynamicStateApi;

            api.CmdSetCullMode(CommandBuffer, _newState.CullMode);
            api.CmdSetFrontFace(CommandBuffer, _newState.FrontFace);
            api.CmdSetDepthTestEnable(CommandBuffer, _newState.DepthTestEnable);
            // TOTK-7F0FA-DEPTHWRITE-OFF13
            api.CmdSetDepthWriteEnable(CommandBuffer, GetTotkDepthWriteEnable());
            api.CmdSetDepthCompareOp(CommandBuffer, _newState.DepthCompareOp);
            api.CmdSetStencilTestEnable(CommandBuffer, _newState.StencilTestEnable);
            api.CmdSetStencilOp(
                CommandBuffer,
                StencilFaceFlags.FaceFrontBit,
                _newState.StencilFrontFailOp,
                _newState.StencilFrontPassOp,
                _newState.StencilFrontDepthFailOp,
                _newState.StencilFrontCompareOp);
            api.CmdSetStencilOp(
                CommandBuffer,
                StencilFaceFlags.FaceBackBit,
                _newState.StencilBackFailOp,
                _newState.StencilBackPassOp,
                _newState.StencilBackDepthFailOp,
                _newState.StencilBackCompareOp);

            _extendedDynamicStateApplied = key;
            _extendedDynamicStateValid = true;
        }

        private bool CreatePipeline(PipelineBindPoint pbp)
        {
            // We can only create a pipeline if the have the shader stages set.
            if (_newState.Stages != null)
            {
                if (pbp == PipelineBindPoint.Graphics && _renderPass == null)
                {
                    CreateRenderPass();
                }

                if (!_program.IsLinked)
                {
                    // Background compile failed, we likely can't create the pipeline because the shader is broken
                    // or the driver failed to compile it.

                    return false;
                }

                Auto<DisposablePipeline> pipeline = pbp == PipelineBindPoint.Compute
                    ? _newState.CreateComputePipeline(Gd, Device, _program, PipelineCache)
                    : _newState.CreateGraphicsPipeline(Gd, Device, _program, PipelineCache, _renderPass!.Get(Cbs).Value);

                if (pipeline == null)
                {
                    // Host failed to create the pipeline, likely due to driver bugs.

                    return false;
                }

                ulong pipelineHandle = pipeline.GetUnsafe().Value.Handle;

                if (pbp == PipelineBindPoint.Graphics)
                {
                    ulong calls = ++_graphicsPipelineLookupCalls;

                    if (_currentPipelineHandle == pipelineHandle)
                    {
                        _graphicsPipelineSameHandle++;
                    }

                    if ((calls & 0x3ffff) == 0)
                    {
                        Logger.Info?.Print(
                            LogClass.Gpu,
                            $"[NEOV2-PIPELINE-REUSE1] calls={calls} sameHandle={_graphicsPipelineSameHandle}");
                    }
                }

                if (_currentPipelineHandle != pipelineHandle)
                {
                    _currentPipelineHandle = pipelineHandle;
                    Pipeline = pipeline;

                    PauseTransformFeedbackInternal();
                    Gd.Api.CmdBindPipeline(CommandBuffer, pbp, Pipeline.Get(Cbs).Value);
                }
            }

            return true;
        }

        private unsafe void BeginRenderPass()
        {
            if (!RenderPassActive)
            {
                FramebufferParams.InsertLoadOpBarriers(Gd, Cbs);

                Rect2D renderArea = new(null, new Extent2D(FramebufferParams.Width, FramebufferParams.Height));

                // NEOV2 Etapa 5: se houver clears adiados, usa a variante do render pass com LoadOp.Clear.
                Auto<DisposableRenderPass> renderPass = _renderPass;
                ClearValue* clearValues = stackalloc ClearValue[Constants.MaxRenderTargets + 1];
                clearValues[0] = new ClearValue();
                uint clearValueCount = 1;

                if (_pendingClearMask != 0 && _rpHolder != null && !_rpHolder.IsDisposed)
                {
                    renderPass = _rpHolder.GetRenderPassWithClears(Gd, Device, _pendingClearMask);

                    int count = Math.Min(FramebufferParams.AttachmentsCount, Constants.MaxRenderTargets + 1);

                    for (int i = 0; i < count; i++)
                    {
                        clearValues[i] = _pendingClearValues[i];
                    }

                    clearValueCount = (uint)Math.Max(count, 1);
                }

                _pendingClearMask = 0;

                RenderPassBeginInfo renderPassBeginInfo = new()
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = renderPass.Get(Cbs).Value,
                    Framebuffer = _framebuffer.Get(Cbs).Value,
                    RenderArea = renderArea,
                    PClearValues = clearValues,
                    ClearValueCount = clearValueCount,
                };

                Gd.Api.CmdBeginRenderPass(CommandBuffer, in renderPassBeginInfo, SubpassContents.Inline);
                RenderPassActive = true;
            }
        }

        // NEOV2 Etapa 5: clears de area inteira registrados antes do inicio do render pass.
        private uint _pendingClearMask;
        private readonly ClearValue[] _pendingClearValues = new ClearValue[Constants.MaxRenderTargets + 1];

        private bool CanDeferClear(in ClearRect rect)
        {
            return IsMainPipeline &&
                   Gd.IsTBDR &&
                   !RenderPassActive &&
                   _rpHolder != null &&
                   !_rpHolder.IsDisposed &&
                   _rpHolder != _nullRenderPass &&
                   FramebufferParams != null &&
                   FramebufferParams.AttachmentsCount > 0 &&
                   rect.Rect.Offset.X == 0 &&
                   rect.Rect.Offset.Y == 0 &&
                   rect.Rect.Extent.Width == FramebufferParams.Width &&
                   rect.Rect.Extent.Height == FramebufferParams.Height &&
                   rect.BaseArrayLayer == 0 &&
                   rect.LayerCount == FramebufferParams.Layers;
        }

        private bool TryDeferColorClear(int bindIndex, in ClearRect rect, ClearValue value)
        {
            if (!CanDeferClear(rect))
            {
                return false;
            }

            int colorCount = Math.Min(FramebufferParams.ColorAttachmentsCount, Constants.MaxRenderTargets);

            for (int i = 0; i < colorCount; i++)
            {
                if (FramebufferParams.AttachmentIndices[i] == bindIndex)
                {
                    _pendingClearValues[i] = value;
                    _pendingClearMask |= 1u << i;
                    return true;
                }
            }

            return false;
        }

        private bool TryDeferDepthStencilClear(ImageAspectFlags flags, in ClearRect rect, float depthValue, uint stencilValue)
        {
            if (!CanDeferClear(rect) || !FramebufferParams.HasDepthStencil)
            {
                return false;
            }

            int dsIndex = FramebufferParams.AttachmentsCount - 1;

            ClearDepthStencilValue value = (_pendingClearMask & (RenderPassHolder.DepthClearBit | RenderPassHolder.StencilClearBit)) != 0
                ? _pendingClearValues[dsIndex].DepthStencil
                : default;

            if (flags.HasFlag(ImageAspectFlags.DepthBit))
            {
                value.Depth = depthValue;
                _pendingClearMask |= RenderPassHolder.DepthClearBit;
            }

            if (flags.HasFlag(ImageAspectFlags.StencilBit))
            {
                value.Stencil = stencilValue;
                _pendingClearMask |= RenderPassHolder.StencilClearBit;
            }

            _pendingClearValues[dsIndex] = new ClearValue(null, value);

            return true;
        }

        private void FlushPendingClears()
        {
            if (_rpHolder == null || _rpHolder.IsDisposed || FramebufferParams == null || _framebuffer == null)
            {
                _pendingClearMask = 0;
                return;
            }

            Gd.Barriers.Flush(Cbs, false, _rpHolder, EndRenderPassDelegate);

            BeginRenderPass();
        }

        public void EndRenderPass()
        {
            // NEOV2 Etapa 5: operacoes fora do render pass vem depois dos clears adiados.
            if (!RenderPassActive && _pendingClearMask != 0)
            {
                FlushPendingClears();
            }

            if (RenderPassActive)
            {
                // TOTK-STORE-COMMIT-TRACE3
                FramebufferParams.AddStoreOpUsage((view, role, generation) =>
                {
                    if (!_totkProducerTrace || view?.Storage == null)
                    {
                        return;
                    }

                    string storageFormat = view.Storage.VkFormat.ToString();
                    int storageWidth = view.Storage.Info.Width;
                    int storageHeight = view.Storage.Info.Height;
                    int storageLayers = view.Storage.Info.GetDepthOrLayers();

                    bool lowResB10 =
                        storageFormat == "B10G11R11UfloatPack32" &&
                        (((storageWidth == 320) && (storageHeight == 180)) ||
                         ((storageWidth == 400) && (storageHeight == 225))) &&
                        storageLayers >= 2;

                    if (!lowResB10)
                    {
                        return;
                    }

                    int storageId =
                        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(view.Storage);

                    string hash = _program?.FragmentShaderHash ?? "<none>";

                    Logger.Info?.Print(
                        LogClass.Gpu,
                        $"[TOTK-STORE-COMMIT3] HASH={hash} DRAW={DrawCount} ATT={role} S={storageId:X} G={generation} VF={view.VkFormat}:{view.Width}x{view.Height}:L{view.Layers}:FL{view.FirstLayer}:FM{view.FirstLevel} SF={storageFormat}:{storageWidth}x{storageHeight}:L{storageLayers}");
                });

                PauseTransformFeedbackInternal();
                Gd.Api.CmdEndRenderPass(CommandBuffer);
                SignalRenderPassEnd();
                RenderPassActive = false;
            }
        }

        protected virtual void SignalRenderPassEnd()
        {
        }

        private void PauseTransformFeedbackInternal()
        {
            if (_tfEnabled && _tfActive)
            {
                EndTransformFeedbackInternal();
                _tfActive = false;
            }
        }

        private void ResumeTransformFeedbackInternal()
        {
            if (_tfEnabled && !_tfActive)
            {
                BeginTransformFeedbackInternal();
                _tfActive = true;
            }
        }

        private unsafe void BeginTransformFeedbackInternal()
        {
            Gd.TransformFeedbackApi.CmdBeginTransformFeedback(CommandBuffer, 0, 0, null, null);
        }

        private unsafe void EndTransformFeedbackInternal()
        {
            Gd.TransformFeedbackApi.CmdEndTransformFeedback(CommandBuffer, 0, 0, null, null);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _nullRenderPass?.Dispose();
                _newState.Dispose();
                _descriptorSetUpdater.Dispose();
                _vertexBufferUpdater.Dispose();

                for (int i = 0; i < _vertexBuffers.Length; i++)
                {
                    _vertexBuffers[i].Dispose();
                }

                for (int i = 0; i < _transformFeedbackBuffers.Length; i++)
                {
                    _transformFeedbackBuffers[i].Dispose();
                }

                Pipeline?.Dispose();

                if (_persistentPipelineCache)
                {
                    PipelineCachePersistence.SaveAndRelease(PipelineCache);
                }

                unsafe
                {
                    Gd.Api.DestroyPipelineCache(Device, PipelineCache, null);
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
