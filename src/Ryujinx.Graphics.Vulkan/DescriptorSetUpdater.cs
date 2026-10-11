using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Buffer = Silk.NET.Vulkan.Buffer;
using CompareOp = Ryujinx.Graphics.GAL.CompareOp;
using Format = Ryujinx.Graphics.GAL.Format;
using SamplerCreateInfo = Ryujinx.Graphics.GAL.SamplerCreateInfo;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.Vulkan
{
    class DescriptorSetUpdater
    {
        private const ulong StorageBufferMaxMirrorable = 0x2000;

        private const int ArrayGrowthSize = 16;

        private record struct BufferRef
        {
            public Auto<DisposableBuffer> Buffer;
            public int Offset;
            public bool Write;

            public BufferRef(Auto<DisposableBuffer> buffer)
            {
                Buffer = buffer;
                Offset = 0;
                Write = true;
            }

            public BufferRef(Auto<DisposableBuffer> buffer, ref BufferRange range)
            {
                Buffer = buffer;
                Offset = range.Offset;
                Write = range.Write;
            }
        }

        private record struct TextureRef
        {
            public ShaderStage Stage;
            public TextureView View;
            public Auto<DisposableImageView> ImageView;
            public Auto<DisposableSampler> Sampler;

            // TOTK-C827-SAMPLERTRACE5
            public SamplerHolder SamplerHolder;

            public TextureRef(ShaderStage stage, TextureView view, Auto<DisposableImageView> imageView, SamplerHolder samplerHolder)
            {
                Stage = stage;
                View = view;
                ImageView = imageView;
                SamplerHolder = samplerHolder;
                Sampler = samplerHolder?.GetSampler();
            }
        }

        private record struct ImageRef
        {
            public ShaderStage Stage;
            public TextureView View;
            public Auto<DisposableImageView> ImageView;

            public ImageRef(ShaderStage stage, TextureView view, Auto<DisposableImageView> imageView)
            {
                Stage = stage;
                View = view;
                ImageView = imageView;
            }
        }

        private readonly record struct ArrayRef<T>(ShaderStage Stage, T Array);

        private readonly VulkanRenderer _gd;
        private readonly Device _device;
        private ShaderCollection _program;

        private readonly BufferRef[] _uniformBufferRefs;
        private readonly BufferRef[] _storageBufferRefs;
        private readonly TextureRef[] _textureRefs;
        private readonly ImageRef[] _imageRefs;
        private readonly TextureBuffer[] _bufferTextureRefs;
        private readonly TextureBuffer[] _bufferImageRefs;

        private ArrayRef<TextureArray>[] _textureArrayRefs;
        private ArrayRef<ImageArray>[] _imageArrayRefs;

        private ArrayRef<TextureArray>[] _textureArrayExtraRefs;
        private ArrayRef<ImageArray>[] _imageArrayExtraRefs;

        private readonly DescriptorBufferInfo[] _uniformBuffers;
        private readonly DescriptorBufferInfo[] _uniformDynamicDescriptors;
        private readonly uint[] _uniformDynamicOffsets;
        private readonly DescriptorBufferInfo[] _storageBuffers;
        private readonly DescriptorImageInfo[] _textures;
        private readonly DescriptorImageInfo[] _images;
        private readonly BufferView[] _bufferTextures;
        private readonly BufferView[] _bufferImages;

        private readonly DescriptorSetTemplateUpdater _templateUpdater;

        private BitMapStruct<Array2<long>> _uniformSet;
        private BitMapStruct<Array2<long>> _storageSet;
        private BitMapStruct<Array2<long>> _uniformMirrored;
        private BitMapStruct<Array2<long>> _storageMirrored;
        private readonly int[] _uniformSetPd;
        private int _pdSequence = 1;

        private bool _updateDescriptorCacheCbIndex;
        private DescriptorSet _lastUniformDescriptorSet;
        private bool _lastUniformDescriptorSetValid;

        [Flags]
        private enum DirtyFlags
        {
            None = 0,
            Uniform = 1 << 0,
            Storage = 1 << 1,
            Texture = 1 << 2,
            Image = 1 << 3,
            All = Uniform | Storage | Texture | Image,
        }

        private DirtyFlags _dirty;

        private ulong _uniformAssignments;
        private ulong _uniformBindingChanges;
        private ulong _uniformBufferChanges;
        private ulong _uniformOffsetOnlyChanges;
        private ulong _uniformRangeOnlyChanges;
        private ulong _uniformOffsetRangeChanges;
        private ulong _uniformOtherSameBufferChanges;
        private bool _uniformPendingOffsetChange;
        private bool _uniformPendingDescriptorChange;
        private ulong _uniformFlushes;
        private ulong _uniformOffsetOnlyFlushes;
        private ulong _uniformDescriptorFlushes;
        private ulong _uniformOtherFlushes;
        private ulong _dynamicUniformFlushes;
        private ulong _dynamicUniformDescriptorUpdates;
        private ulong _dynamicUniformOffsetRebinds;
        private ulong _dynamicUniformResolvedChanges;
        private ulong _programSwitches;
        private ulong _sameLayoutProgramSwitches;
        private ulong _fullyReusedProgramSwitches;
        private ulong _partiallyReusedProgramSwitches;

        private readonly BufferHolder _dummyBuffer;
        private readonly TextureView _dummyTexture;
        private readonly SamplerHolder _dummySampler;

        // TOTK-C827-LINEAR-B128-6
        private readonly SamplerHolder _totkC827LinearSampler;
        private static readonly bool _totkC827LinearB128 =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_C827_LINEAR_B128") == "1";
        private bool _totkC827LinearB128Logged;

        // TOTK-C827-DEPTHCONST4
        private readonly TextureView _totkDepthZeroTexture;
        private readonly TextureView _totkDepthOneTexture;
        private static readonly string _totkC827DepthConst =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_C827_DEPTH_CONST");
        private bool _totkC827DepthConstLogged;

        public List<TextureView> FeedbackLoopHazards { get; private set; }

        // TOTK-C827-SAMPLERTRACE5
        private bool _totkC827SamplerLogged;

        // TOTK-C827-DEPTH-PRODUCER12-DS
        private static readonly bool _totkC827DepthProducerTrace =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_C827_DEPTH_PRODUCER_TRACE") == "1";

        private readonly HashSet<int> _totkC827DepthProducerDumpedStorages = new();

        // TOTK-C827-DEPTH-COPY19B
        private static readonly bool _totkC827DepthCopy19b =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_C827_DEPTH_COPY") == "1";

        private TextureView _totkC827DepthCopyView19b;
        private bool _totkC827DepthCopyLogged19b;

        private TextureView GetTotkC827DepthCopy19b(
            CommandBufferScoped cbs,
            ShaderCollection program,
            int binding,
            TextureView source)
        {
            if (!_totkC827DepthCopy19b ||
                program == null ||
                binding != 128 ||
                source == null ||
                !string.Equals(
                    program.FragmentShaderHash,
                    "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (_totkC827DepthCopyView19b == null ||
                _totkC827DepthCopyView19b.Info != source.Info)
            {
                _totkC827DepthCopyView19b?.Dispose();
                _totkC827DepthCopyView19b =
                    _gd.CreateTextureView(source.Info);

                _totkC827DepthCopyLogged19b = false;
            }

            source.CopyTo(_totkC827DepthCopyView19b, 0, 0);

            // TextureCopy.Copy faz a ida:
            // DefaultAccessMask -> TransferRead
            //
            // Fazemos explicitamente o retorno do SOURCE para nao deixar
            // nosso diagnostico introduzir uma dependencia incompleta.
            TextureView.InsertImageBarrier(
                _gd.Api,
                cbs.CommandBuffer,
                source.GetImage().Get(cbs).Value,
                AccessFlags.TransferReadBit,
                TextureStorage.DefaultAccessMask,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.AllCommandsBit,
                source.Info.Format.ConvertAspectFlags(),
                source.FirstLayer,
                source.FirstLevel,
                source.Info.GetLayers(),
                source.Info.Levels);

            _totkC827DepthCopyView19b.PrepareForUsage(
                cbs,
                PipelineStageFlags.FragmentShaderBit,
                FeedbackLoopHazards);

            if (!_totkC827DepthCopyLogged19b)
            {
                int srcStorage =
                    System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(
                        source.Storage);

                int dstStorage =
                    System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(
                        _totkC827DepthCopyView19b.Storage);

                Logger.Info?.Print(
                    LogClass.Gpu,
                    $"[TOTK-C827-DEPTH-COPY19B] ACTIVE " +
                    $"SRC={srcStorage:X} DST={dstStorage:X} " +
                    $"FORMAT={source.VkFormat} " +
                    $"SIZE={source.Width}x{source.Height}");

                _totkC827DepthCopyLogged19b = true;
            }

            return _totkC827DepthCopyView19b;
        }


        // TOTK-C827-BARRIER-TRACE18
        private static readonly bool _totkC827BarrierTrace18 =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_C827_BARRIER_TRACE") == "1";

        private readonly HashSet<string> _totkC827BarrierStates18 =
            new(StringComparer.Ordinal);

        private void TraceTotkC827BarrierState18(
            ShaderCollection program,
            int binding,
            TextureView view)
        {
            if (!_totkC827BarrierTrace18 ||
                program == null ||
                binding != 128 ||
                view?.Storage == null ||
                !string.Equals(
                    program.FragmentShaderHash,
                    "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            int storageId =
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(
                    view.Storage);

            string signature =
                view.Storage.TotkGetBarrierDebugSignature18();

            string key =
                storageId.ToString("X") + "|" + signature;

            if (!_totkC827BarrierStates18.Add(key))
            {
                return;
            }

            Logger.Info?.Print(
                LogClass.Gpu,
                $"[TOTK-C827-BARRIER-TRACE18] " +
                $"B128 STORAGE={storageId:X} " +
                $"FORMAT={view.Storage.VkFormat} " +
                $"SIZE={view.Storage.Info.Width}x{view.Storage.Info.Height} " +
                view.Storage.TotkGetBarrierDebugState18());
        }

        private void TraceTotkC827DepthProducer(
            ShaderCollection program,
            int binding,
            TextureView view)
        {
            if (!_totkC827DepthProducerTrace ||
                program == null ||
                binding != 128 ||
                view?.Storage == null ||
                !string.Equals(
                    program.FragmentShaderHash,
                    "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            int storageId =
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(
                    view.Storage);

            if (!_totkC827DepthProducerDumpedStorages.Add(storageId))
            {
                return;
            }

            string[] history = view.Storage.TotkGetDepthDrawHistory();

            Logger.Info?.Print(
                LogClass.Gpu,
                $"[TOTK-C827-DEPTH-PRODUCER12] " +
                $"B128 STORAGE={storageId:X} " +
                $"FORMAT={view.Storage.VkFormat} " +
                $"SIZE={view.Storage.Info.Width}x{view.Storage.Info.Height} " +
                $"HISTORY={history.Length}");

            if (history.Length == 0)
            {
                Logger.Info?.Print(
                    LogClass.Gpu,
                    $"[TOTK-C827-DEPTH-PRODUCER12] " +
                    $"STORAGE={storageId:X} NO-PRIOR-DEPTH-DRAWS");

                return;
            }

            for (int i = 0; i < history.Length; i++)
            {
                Logger.Info?.Print(
                    LogClass.Gpu,
                    $"[TOTK-C827-DEPTH-PRODUCER12] " +
                    $"STORAGE={storageId:X} " +
                    $"SEQ={i} " +
                    history[i]);
            }
        }

        private void LogTotkC827Sampler()
        {
            if (_totkC827SamplerLogged ||
                _program == null ||
                !string.Equals(
                    _program.FragmentShaderHash,
                    "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ref TextureRef texture = ref _textureRefs[128];

            if (texture.View == null || texture.SamplerHolder == null)
            {
                return;
            }

            SamplerCreateInfo info = texture.SamplerHolder.Info;

            Logger.Info?.Print(
                LogClass.Gpu,
                $"[TOTK-C827-SAMPLERTRACE5] " +
                $"B128 FORMAT={texture.View.VkFormat} " +
                $"SIZE={texture.View.Width}x{texture.View.Height} " +
                $"MIN={info.MinFilter} MAG={info.MagFilter} " +
                $"ADDRU={info.AddressU} ADDRV={info.AddressV} ADDRP={info.AddressP} " +
                $"COMPAREMODE={info.CompareMode} COMPAREOP={info.CompareOp} " +
                $"MINLOD={info.MinLod} MAXLOD={info.MaxLod} " +
                $"BIAS={info.MipLodBias} ANISO={info.MaxAnisotropy} " +
                $"SEAMLESS={info.SeamlessCubemap}");

            _totkC827SamplerLogged = true;
        }

        // TOTK-C827-NEUTRAL1
        private static readonly string _totkC827NeutralBinding =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_C827_NEUTRAL_BINDING");
        private readonly HashSet<int> _totkC827NeutralLogged = new();
        private bool _totkC827BindingsLogged;

        // TOTK-C827-DEPTHCONST4
        // TOTK-C827-LINEAR-B128-6
        private bool ShouldUseTotkC827LinearB128(ShaderCollection program, int binding)
        {
            bool active =
                _totkC827LinearB128 &&
                binding == 128 &&
                program != null &&
                string.Equals(
                    program.FragmentShaderHash,
                    "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                    StringComparison.OrdinalIgnoreCase);

            if (active && !_totkC827LinearB128Logged)
            {
                Logger.Info?.Print(
                    LogClass.Gpu,
                    "[TOTK-C827-LINEAR-B128-6] ACTIVE B128 SAMPLER=LINEAR");

                _totkC827LinearB128Logged = true;
            }

            return active;
        }

        private bool ShouldUseTotkC827DepthConst(ShaderCollection program, int binding)
        {
            bool active =
                binding == 128 &&
                program != null &&
                string.Equals(
                    program.FragmentShaderHash,
                    "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                    StringComparison.OrdinalIgnoreCase) &&
                (_totkC827DepthConst == "0" || _totkC827DepthConst == "1");

            if (active && !_totkC827DepthConstLogged)
            {
                Logger.Info?.Print(
                    LogClass.Gpu,
                    $"[TOTK-C827-DEPTHCONST4] ACTIVE B128 VALUE={_totkC827DepthConst}");

                _totkC827DepthConstLogged = true;
            }

            return active;
        }

        private bool ShouldNeutralizeTotkC827Binding(ShaderCollection program, int binding)
        {
            if (program == null ||
                !string.Equals(
                    program.FragmentShaderHash,
                    "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                    StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(_totkC827NeutralBinding, out int requested) ||
                requested != binding)
            {
                return false;
            }

            if (_totkC827NeutralLogged.Add(binding))
            {
                Logger.Info?.Print(
                    LogClass.Gpu,
                    $"[TOTK-C827-NEUTRAL1] ACTIVE T{binding}");
            }

            return true;
        }

        // TOTK-RESOURCE-TRACE1
        // Diagnostic-only fingerprint of resources visible to the fragment stage.
        public string GetTotkFragmentResourceSignature()
        {
            System.Text.StringBuilder sb = new();

            for (int i = 0; i < _textureRefs.Length; i++)
            {
                ref TextureRef texture = ref _textureRefs[i];

                if (texture.Stage == ShaderStage.Fragment && texture.View != null)
                {
                    TextureView view = texture.View;
                    int viewId = RuntimeHelpers.GetHashCode(view);
                    int storageId = view.Storage != null ? RuntimeHelpers.GetHashCode(view.Storage) : 0;
                    int samplerId = texture.Sampler != null ? RuntimeHelpers.GetHashCode(texture.Sampler) : 0;

                    sb.Append("T")
                      .Append(i)
                      .Append(":V").Append(viewId.ToString("X"))
                      .Append(":S").Append(storageId.ToString("X"))
                      // TOTK-WRITE-GENERATION1
                      .Append(":G").Append(view.Storage?.TotkWriteGeneration ?? 0)
                      .Append(":F").Append(view.VkFormat)
                      .Append(":").Append(view.Width).Append("x").Append(view.Height)
                      .Append(":L").Append(view.Layers)
                      .Append(":FL").Append(view.FirstLayer)
                      .Append(":FM").Append(view.FirstLevel)
                      .Append(":SP").Append(samplerId.ToString("X"))
                      .Append(';');
                }
            }

            for (int i = 0; i < _imageRefs.Length; i++)
            {
                ref ImageRef image = ref _imageRefs[i];

                if (image.Stage == ShaderStage.Fragment && image.View != null)
                {
                    TextureView view = image.View;
                    int viewId = RuntimeHelpers.GetHashCode(view);
                    int storageId = view.Storage != null ? RuntimeHelpers.GetHashCode(view.Storage) : 0;

                    sb.Append("I")
                      .Append(i)
                      .Append(":V").Append(viewId.ToString("X"))
                      .Append(":S").Append(storageId.ToString("X"))
                      // TOTK-WRITE-GENERATION1
                      .Append(":G").Append(view.Storage?.TotkWriteGeneration ?? 0)
                      .Append(":F").Append(view.VkFormat)
                      .Append(":").Append(view.Width).Append("x").Append(view.Height)
                      .Append(":L").Append(view.Layers)
                      .Append(":FL").Append(view.FirstLayer)
                      .Append(":FM").Append(view.FirstLevel)
                      .Append(';');
                }
            }

            for (int i = 0; i < _textureArrayRefs.Length; i++)
            {
                ref ArrayRef<TextureArray> arrayRef = ref _textureArrayRefs[i];

                if (arrayRef.Stage == ShaderStage.Fragment && arrayRef.Array != null)
                {
                    sb.Append("TA")
                      .Append(i)
                      .Append(":").Append(RuntimeHelpers.GetHashCode(arrayRef.Array).ToString("X"))
                      .Append(';');
                }
            }

            for (int i = 0; i < _imageArrayRefs.Length; i++)
            {
                ref ArrayRef<ImageArray> arrayRef = ref _imageArrayRefs[i];

                if (arrayRef.Stage == ShaderStage.Fragment && arrayRef.Array != null)
                {
                    sb.Append("IA")
                      .Append(i)
                      .Append(":").Append(RuntimeHelpers.GetHashCode(arrayRef.Array).ToString("X"))
                      .Append(';');
                }
            }

            return sb.ToString();
        }


        public DescriptorSetUpdater(VulkanRenderer gd, Device device)
        {
            _gd = gd;
            _device = device;

            // Some of the bindings counts needs to be multiplied by 2 because we have buffer and
            // regular textures/images interleaved on the same descriptor set.

            _uniformBufferRefs = new BufferRef[Constants.MaxUniformBufferBindings];
            _storageBufferRefs = new BufferRef[Constants.MaxStorageBufferBindings];
            _textureRefs = new TextureRef[Constants.MaxTextureBindings * 2];
            _imageRefs = new ImageRef[Constants.MaxImageBindings * 2];
            _bufferTextureRefs = new TextureBuffer[Constants.MaxTextureBindings * 2];
            _bufferImageRefs = new TextureBuffer[Constants.MaxImageBindings * 2];

            _textureArrayRefs = [];
            _imageArrayRefs = [];

            _textureArrayExtraRefs = [];
            _imageArrayExtraRefs = [];

            _uniformBuffers = new DescriptorBufferInfo[Constants.MaxUniformBufferBindings];
            _uniformDynamicDescriptors = new DescriptorBufferInfo[Constants.MaxUniformBufferBindings];
            _uniformDynamicOffsets = new uint[Constants.MaxUniformBufferBindings];
            _storageBuffers = new DescriptorBufferInfo[Constants.MaxStorageBufferBindings];
            _textures = new DescriptorImageInfo[Constants.MaxTexturesPerStage];
            _images = new DescriptorImageInfo[Constants.MaxImagesPerStage];
            _bufferTextures = new BufferView[Constants.MaxTexturesPerStage];
            _bufferImages = new BufferView[Constants.MaxImagesPerStage];

            _uniformSetPd = new int[Constants.MaxUniformBufferBindings];

            DescriptorImageInfo initialImageInfo = new()
            {
                ImageLayout = ImageLayout.General,
            };

            _textures.AsSpan().Fill(initialImageInfo);
            _images.AsSpan().Fill(initialImageInfo);

            if (gd.Capabilities.SupportsNullDescriptors)
            {
                // If null descriptors are supported, we can pass null as the handle.
                _dummyBuffer = null;
            }
            else
            {
                // If null descriptors are not supported, we need to pass the handle of a dummy buffer on unused bindings.
                _dummyBuffer = gd.BufferManager.Create(gd, 0x10000, forConditionalRendering: false, baseType: BufferAllocationType.DeviceLocal);
            }

            _dummyTexture = gd.CreateTextureView(new TextureCreateInfo(
                1,
                1,
                1,
                1,
                1,
                1,
                1,
                4,
                Format.R8G8B8A8Unorm,
                DepthStencilMode.Depth,
                Target.Texture2D,
                SwizzleComponent.Red,
                SwizzleComponent.Green,
                SwizzleComponent.Blue,
                SwizzleComponent.Alpha));

            // TOTK-C827-DEPTHCONST4
            _totkDepthZeroTexture = gd.CreateTextureView(new TextureCreateInfo(
                1,
                1,
                1,
                1,
                1,
                1,
                1,
                4,
                Format.D32Float,
                DepthStencilMode.Depth,
                Target.Texture2D,
                SwizzleComponent.Red,
                SwizzleComponent.Green,
                SwizzleComponent.Blue,
                SwizzleComponent.Alpha));

            _totkDepthOneTexture = gd.CreateTextureView(new TextureCreateInfo(
                1,
                1,
                1,
                1,
                1,
                1,
                1,
                4,
                Format.D32Float,
                DepthStencilMode.Depth,
                Target.Texture2D,
                SwizzleComponent.Red,
                SwizzleComponent.Green,
                SwizzleComponent.Blue,
                SwizzleComponent.Alpha));

            // TOTK-C827-LINEAR-B128-6
            _totkC827LinearSampler = (SamplerHolder)gd.CreateSampler(
                SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear));

            _dummySampler = (SamplerHolder)gd.CreateSampler(new SamplerCreateInfo(
                MinFilter.Nearest,
                MagFilter.Nearest,
                false,
                AddressMode.Repeat,
                AddressMode.Repeat,
                AddressMode.Repeat,
                CompareMode.None,
                CompareOp.Always,
                new ColorF(0, 0, 0, 0),
                0,
                0,
                0,
                1f));

            _templateUpdater = new();
        }

        public void Initialize(bool isMainPipeline)
        {
            MemoryOwner<byte> dummyTextureData = MemoryOwner<byte>.RentCleared(4);
            _dummyTexture.SetData(dummyTextureData);

            // TOTK-C827-DEPTHCONST4
            MemoryOwner<byte> totkDepthZeroData = MemoryOwner<byte>.RentCleared(4);
            _totkDepthZeroTexture.SetData(totkDepthZeroData);

            MemoryOwner<byte> totkDepthOneData = MemoryOwner<byte>.RentCleared(4);
            BitConverter.TryWriteBytes(totkDepthOneData.Span, 1.0f);
            _totkDepthOneTexture.SetData(totkDepthOneData);

            if (isMainPipeline)
            {
                FeedbackLoopHazards = new();
            }
        }

        private static bool BindingOverlaps(ref DescriptorBufferInfo info, int bindingOffset, int offset, int size)
        {
            return offset < bindingOffset + (int)info.Range && (offset + size) > bindingOffset;
        }

        internal void Rebind(Auto<DisposableBuffer> buffer, int offset, int size)
        {
            if (_program == null)
            {
                return;
            }

            // Check stage bindings

            _uniformMirrored.Union(_uniformSet).SignalSet((int binding, int count) =>
            {
                for (int i = 0; i < count; i++)
                {
                    ref BufferRef bufferRef = ref _uniformBufferRefs[binding];
                    if (bufferRef.Buffer == buffer)
                    {
                        ref DescriptorBufferInfo info = ref _uniformBuffers[binding];
                        int bindingOffset = bufferRef.Offset;

                        if (BindingOverlaps(ref info, bindingOffset, offset, size))
                        {
                            _uniformSet.Clear(binding);
                            _uniformSetPd[binding] = 0;
                            SignalDirty(DirtyFlags.Uniform);
                        }
                    }

                    binding++;
                }
            });

            _storageMirrored.Union(_storageSet).SignalSet((int binding, int count) =>
            {
                for (int i = 0; i < count; i++)
                {
                    ref BufferRef bufferRef = ref _storageBufferRefs[binding];
                    if (bufferRef.Buffer == buffer)
                    {
                        ref DescriptorBufferInfo info = ref _storageBuffers[binding];
                        int bindingOffset = bufferRef.Offset;

                        if (BindingOverlaps(ref info, bindingOffset, offset, size))
                        {
                            _storageSet.Clear(binding);
                            SignalDirty(DirtyFlags.Storage);
                        }
                    }

                    binding++;
                }
            });
        }

        public void InsertBindingBarriers(CommandBufferScoped cbs)
        {
            if ((FeedbackLoopHazards?.Count ?? 0) > 0)
            {
                // Clear existing hazards - they will be rebuilt.

                foreach (TextureView hazard in FeedbackLoopHazards)
                {
                    hazard.DecrementHazardUses();
                }

                FeedbackLoopHazards.Clear();
            }

            foreach (ResourceBindingSegment segment in _program.BindingSegments[PipelineBase.TextureSetIndex])
            {
                if (segment.Type == ResourceType.TextureAndSampler)
                {
                    if (!segment.IsArray)
                    {
                        for (int i = 0; i < segment.Count; i++)
                        {
                            ref TextureRef texture = ref _textureRefs[segment.Binding + i];
                            texture.View?.PrepareForUsage(cbs, texture.Stage.ConvertToPipelineStageFlags(), FeedbackLoopHazards);
                        }
                    }
                    else
                    {
                        ref ArrayRef<TextureArray> arrayRef = ref _textureArrayRefs[segment.Binding];
                        PipelineStageFlags stageFlags = arrayRef.Stage.ConvertToPipelineStageFlags();
                        arrayRef.Array?.QueueWriteToReadBarriers(cbs, stageFlags);
                    }
                }
            }

            foreach (ResourceBindingSegment segment in _program.BindingSegments[PipelineBase.ImageSetIndex])
            {
                if (segment.Type == ResourceType.Image)
                {
                    if (!segment.IsArray)
                    {
                        for (int i = 0; i < segment.Count; i++)
                        {
                            ref ImageRef image = ref _imageRefs[segment.Binding + i];
                            image.View?.PrepareForUsage(cbs, image.Stage.ConvertToPipelineStageFlags(), FeedbackLoopHazards);
                        }
                    }
                    else
                    {
                        ref ArrayRef<ImageArray> arrayRef = ref _imageArrayRefs[segment.Binding];
                        PipelineStageFlags stageFlags = arrayRef.Stage.ConvertToPipelineStageFlags();
                        arrayRef.Array?.QueueWriteToReadBarriers(cbs, stageFlags);
                    }
                }
            }

            for (int setIndex = PipelineBase.DescriptorSetLayouts; setIndex < _program.BindingSegments.Length; setIndex++)
            {
                ResourceBindingSegment[] bindingSegments = _program.BindingSegments[setIndex];

                if (bindingSegments.Length == 0)
                {
                    continue;
                }

                ResourceBindingSegment segment = bindingSegments[0];

                if (segment.IsArray)
                {
                    if (segment.Type is ResourceType.Texture
                        or ResourceType.Sampler
                        or ResourceType.TextureAndSampler
                        or ResourceType.BufferTexture)
                    {
                        ref ArrayRef<TextureArray> arrayRef = ref _textureArrayExtraRefs[setIndex - PipelineBase.DescriptorSetLayouts];
                        PipelineStageFlags stageFlags = arrayRef.Stage.ConvertToPipelineStageFlags();
                        arrayRef.Array?.QueueWriteToReadBarriers(cbs, stageFlags);
                    }
                    else if (segment.Type is ResourceType.Image or ResourceType.BufferImage)
                    {
                        ref ArrayRef<ImageArray> arrayRef = ref _imageArrayExtraRefs[setIndex - PipelineBase.DescriptorSetLayouts];
                        PipelineStageFlags stageFlags = arrayRef.Stage.ConvertToPipelineStageFlags();
                        arrayRef.Array?.QueueWriteToReadBarriers(cbs, stageFlags);
                    }
                }
            }
        }

        public void AdvancePdSequence()
        {
            if (++_pdSequence == 0)
            {
                _pdSequence = 1;
            }
        }

        public void SetProgram(CommandBufferScoped cbs, ShaderCollection program, bool isBound)
        {
            ShaderCollection previousProgram = _program;
            bool sameLayout = program.HasSameLayout(previousProgram);
            bool sameBindPoint = previousProgram != null && program.IsCompute == previousProgram.IsCompute;
            DirtyFlags programDirty = DirtyFlags.All;

            if (sameLayout && sameBindPoint && isBound)
            {
                programDirty = GetProgramSwitchDirtyFlags(previousProgram, program);
            }

            if (!sameLayout)
            {
                // When the pipeline layout changes, push descriptor bindings are invalidated.

                AdvancePdSequence();
            }

            _program = program;

            // TOTK-C827-BINDINGS3
            if (!_totkC827BindingsLogged &&
                string.Equals(
                    program.FragmentShaderHash,
                    "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                    StringComparison.OrdinalIgnoreCase))
            {
                System.Text.StringBuilder sb = new();

                if (program.BindingSegments.Length > PipelineBase.TextureSetIndex)
                {
                    foreach (ResourceBindingSegment segment in program.BindingSegments[PipelineBase.TextureSetIndex])
                    {
                        sb.Append("B")
                          .Append(segment.Binding)
                          .Append(":C").Append(segment.Count)
                          .Append(":T").Append(segment.Type)
                          .Append(":A").Append(segment.IsArray ? 1 : 0)
                          .Append(';');
                    }
                }

                Logger.Info?.Print(
                    LogClass.Gpu,
                    $"[TOTK-C827-BINDINGS3] TEXTURES={sb}");

                _totkC827BindingsLogged = true;
            }

            // TOTK-C827-FORCE-TEXTURE-DIRTY2
            // The descriptor layout can be fully reused across program switches.
            // Force the texture set to be rebuilt while this diagnostic is active,
            // otherwise the neutral binding may never reach Vulkan.
            if ((!string.IsNullOrWhiteSpace(_totkC827NeutralBinding) ||
                 !string.IsNullOrWhiteSpace(_totkC827DepthConst) ||
                 _totkC827LinearB128) &&
                string.Equals(
                    program.FragmentShaderHash,
                    "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                    StringComparison.OrdinalIgnoreCase))
            {
                programDirty |= DirtyFlags.Texture;
            }

            if (programDirty != DirtyFlags.None)
            {
                _updateDescriptorCacheCbIndex = true;
                _dirty |= programDirty;

                if ((programDirty & DirtyFlags.Uniform) != 0)
                {
                    _lastUniformDescriptorSetValid = false;
                }
            }

            _programSwitches++;

            if (sameLayout)
            {
                _sameLayoutProgramSwitches++;
            }

            if (programDirty == DirtyFlags.None)
            {
                _fullyReusedProgramSwitches++;
            }
            else if (programDirty != DirtyFlags.All)
            {
                _partiallyReusedProgramSwitches++;
            }

            if ((_programSwitches & 0xffff) == 0)
            {
                Ryujinx.Common.Logging.Logger.Info?.Print(
                    Ryujinx.Common.Logging.LogClass.Gpu,
                    $"[NEOV2-DESCRIPTOR-LAYOUT-REUSE1] switches={_programSwitches} sameLayout={_sameLayoutProgramSwitches} " +
                    $"fullReuse={_fullyReusedProgramSwitches} partialReuse={_partiallyReusedProgramSwitches}");
            }
        }

        // Descriptor sets remain bound across compatible Vulkan pipeline layouts. When
        // the next program only uses segments already populated by the previous program,
        // keep those sets bound instead of allocating and rewriting identical copies.
        private static DirtyFlags GetProgramSwitchDirtyFlags(ShaderCollection previousProgram, ShaderCollection program)
        {
            DirtyFlags dirty = DirtyFlags.None;

            for (int setIndex = 0; setIndex < PipelineBase.DescriptorSetLayouts; setIndex++)
            {
                if (setIndex >= previousProgram.BindingSegments.Length ||
                    setIndex >= program.BindingSegments.Length ||
                    !SegmentsCovered(previousProgram.BindingSegments[setIndex], program.BindingSegments[setIndex]))
                {
                    dirty |= setIndex switch
                    {
                        PipelineBase.UniformSetIndex => DirtyFlags.Uniform,
                        PipelineBase.StorageSetIndex => DirtyFlags.Storage,
                        PipelineBase.TextureSetIndex => DirtyFlags.Texture,
                        PipelineBase.ImageSetIndex => DirtyFlags.Image,
                        _ => DirtyFlags.All,
                    };
                }
            }

            return dirty;
        }

        private static bool SegmentsCovered(ResourceBindingSegment[] previousSegments, ResourceBindingSegment[] segments)
        {
            foreach (ResourceBindingSegment segment in segments)
            {
                bool found = false;

                foreach (ResourceBindingSegment previousSegment in previousSegments)
                {
                    if (segment.Binding == previousSegment.Binding &&
                        segment.Count == previousSegment.Count &&
                        segment.Type == previousSegment.Type &&
                        segment.IsArray == previousSegment.IsArray)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        public void SetImage(CommandBufferScoped cbs, ShaderStage stage, int binding, ITexture image)
        {
            if (image is TextureBuffer imageBuffer)
            {
                _bufferImageRefs[binding] = imageBuffer;
            }
            else if (image is TextureView view)
            {
                ref ImageRef iRef = ref _imageRefs[binding];

                iRef.View?.ClearUsage(FeedbackLoopHazards);
                view?.PrepareForUsage(cbs, stage.ConvertToPipelineStageFlags(), FeedbackLoopHazards);

                iRef = new(stage, view, view.GetIdentityImageView());
            }
            else
            {
                _imageRefs[binding] = default;
                _bufferImageRefs[binding] = null;
            }

            SignalDirty(DirtyFlags.Image);
        }

        public void SetImage(int binding, Auto<DisposableImageView> image)
        {
            _imageRefs[binding] = new(ShaderStage.Compute, null, image);

            SignalDirty(DirtyFlags.Image);
        }

        public void SetStorageBuffers(CommandBuffer commandBuffer, ReadOnlySpan<BufferAssignment> buffers)
        {
            bool changed = false;

            for (int i = 0; i < buffers.Length; i++)
            {
                BufferAssignment assignment = buffers[i];
                BufferRange buffer = assignment.Range;
                int index = assignment.Binding;

                Auto<DisposableBuffer> vkBuffer = buffer.Handle == BufferHandle.Null
                    ? null
                    : _gd.BufferManager.GetBuffer(commandBuffer, buffer.Handle, buffer.Write, isSSBO: true);

                ref BufferRef currentBufferRef = ref _storageBufferRefs[index];

                DescriptorBufferInfo info = new()
                {
                    Offset = (ulong)buffer.Offset,
                    Range = (ulong)buffer.Size,
                };

                BufferRef newRef = new(vkBuffer, ref buffer);

                ref DescriptorBufferInfo currentInfo = ref _storageBuffers[index];

                if (!currentBufferRef.Equals(newRef) || currentInfo.Range != info.Range)
                {
                    _storageSet.Clear(index);

                    currentInfo = info;
                    currentBufferRef = newRef;
                    changed = true;
                }
            }

            // NEOV2-DESCRIPTOR-DIRTY-DEDUP1: setting an identical binding must not
            // allocate/update/bind another Vulkan descriptor set at the next draw.
            if (changed)
            {
                SignalDirty(DirtyFlags.Storage);
            }
        }

        public void SetStorageBuffers(CommandBuffer commandBuffer, int first, ReadOnlySpan<Auto<DisposableBuffer>> buffers)
        {
            bool changed = false;

            for (int i = 0; i < buffers.Length; i++)
            {
                Auto<DisposableBuffer> vkBuffer = buffers[i];
                int index = first + i;

                ref BufferRef currentBufferRef = ref _storageBufferRefs[index];

                DescriptorBufferInfo info = new()
                {
                    Offset = 0,
                    Range = Vk.WholeSize,
                };

                BufferRef newRef = new(vkBuffer);

                ref DescriptorBufferInfo currentInfo = ref _storageBuffers[index];

                if (!currentBufferRef.Equals(newRef) || currentInfo.Range != info.Range)
                {
                    _storageSet.Clear(index);

                    currentInfo = info;
                    currentBufferRef = newRef;
                    changed = true;
                }
            }

            if (changed)
            {
                SignalDirty(DirtyFlags.Storage);
            }
        }

        public void SetTextureAndSampler(
            CommandBufferScoped cbs,
            ShaderStage stage,
            int binding,
            ITexture texture,
            ISampler sampler)
        {
            if (texture is TextureBuffer textureBuffer)
            {
                _bufferTextureRefs[binding] = textureBuffer;
            }
            else if (texture is TextureView view)
            {
                ref TextureRef iRef = ref _textureRefs[binding];

                iRef.View?.ClearUsage(FeedbackLoopHazards);
                view?.PrepareForUsage(cbs, stage.ConvertToPipelineStageFlags(), FeedbackLoopHazards);

                iRef = new(stage, view, view.GetImageView(), (SamplerHolder)sampler);
            }
            else
            {
                _textureRefs[binding] = default;
                _bufferTextureRefs[binding] = null;
            }

            SignalDirty(DirtyFlags.Texture);
        }

        public void SetTextureAndSamplerIdentitySwizzle(
            CommandBufferScoped cbs,
            ShaderStage stage,
            int binding,
            ITexture texture,
            ISampler sampler)
        {
            if (texture is TextureView view)
            {
                view.Storage.QueueWriteToReadBarrier(cbs, AccessFlags.ShaderReadBit, stage.ConvertToPipelineStageFlags());

                _textureRefs[binding] = new(stage, view, view.GetIdentityImageView(), (SamplerHolder)sampler);

                SignalDirty(DirtyFlags.Texture);
            }
            else
            {
                SetTextureAndSampler(cbs, stage, binding, texture, sampler);
            }
        }

        public void SetTextureArray(CommandBufferScoped cbs, ShaderStage stage, int binding, ITextureArray array)
        {
            ref ArrayRef<TextureArray> arrayRef = ref GetArrayRef(ref _textureArrayRefs, binding, ArrayGrowthSize);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef.Array?.DecrementBindCount();

                if (array is TextureArray textureArray)
                {
                    textureArray.IncrementBindCount();
                    textureArray.QueueWriteToReadBarriers(cbs, stage.ConvertToPipelineStageFlags());
                }

                arrayRef = new ArrayRef<TextureArray>(stage, array as TextureArray);

                SignalDirty(DirtyFlags.Texture);
            }
        }

        public void SetTextureArraySeparate(CommandBufferScoped cbs, ShaderStage stage, int setIndex, ITextureArray array)
        {
            ref ArrayRef<TextureArray> arrayRef = ref GetArrayRef(ref _textureArrayExtraRefs, setIndex - PipelineBase.DescriptorSetLayouts);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef.Array?.DecrementBindCount();

                if (array is TextureArray textureArray)
                {
                    textureArray.IncrementBindCount();
                    textureArray.QueueWriteToReadBarriers(cbs, stage.ConvertToPipelineStageFlags());
                }

                arrayRef = new ArrayRef<TextureArray>(stage, array as TextureArray);

                SignalDirty(DirtyFlags.Texture);
            }
        }

        public void SetImageArray(CommandBufferScoped cbs, ShaderStage stage, int binding, IImageArray array)
        {
            ref ArrayRef<ImageArray> arrayRef = ref GetArrayRef(ref _imageArrayRefs, binding, ArrayGrowthSize);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef.Array?.DecrementBindCount();

                if (array is ImageArray imageArray)
                {
                    imageArray.IncrementBindCount();
                    imageArray.QueueWriteToReadBarriers(cbs, stage.ConvertToPipelineStageFlags());
                }

                arrayRef = new ArrayRef<ImageArray>(stage, array as ImageArray);

                SignalDirty(DirtyFlags.Image);
            }
        }

        public void SetImageArraySeparate(CommandBufferScoped cbs, ShaderStage stage, int setIndex, IImageArray array)
        {
            ref ArrayRef<ImageArray> arrayRef = ref GetArrayRef(ref _imageArrayExtraRefs, setIndex - PipelineBase.DescriptorSetLayouts);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef.Array?.DecrementBindCount();

                if (array is ImageArray imageArray)
                {
                    imageArray.IncrementBindCount();
                    imageArray.QueueWriteToReadBarriers(cbs, stage.ConvertToPipelineStageFlags());
                }

                arrayRef = new ArrayRef<ImageArray>(stage, array as ImageArray);

                SignalDirty(DirtyFlags.Image);
            }
        }

        private static ref ArrayRef<T> GetArrayRef<T>(ref ArrayRef<T>[] array, int index, int growthSize = 1)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);

            if (array.Length <= index)
            {
                Array.Resize(ref array, index + growthSize);
            }

            return ref array[index];
        }

        public void SetUniformBuffers(CommandBuffer commandBuffer, ReadOnlySpan<BufferAssignment> buffers)
        {
            bool changed = false;

            for (int i = 0; i < buffers.Length; i++)
            {
                ulong assignments = ++_uniformAssignments;
                BufferAssignment assignment = buffers[i];
                BufferRange buffer = assignment.Range;
                int index = assignment.Binding;

                Auto<DisposableBuffer> vkBuffer = buffer.Handle == BufferHandle.Null
                    ? null
                    : _gd.BufferManager.GetBuffer(commandBuffer, buffer.Handle, false);

                ref BufferRef currentBufferRef = ref _uniformBufferRefs[index];

                DescriptorBufferInfo info = new()
                {
                    Offset = (ulong)buffer.Offset,
                    Range = (ulong)buffer.Size,
                };

                BufferRef newRef = new(vkBuffer, ref buffer);

                ref DescriptorBufferInfo currentInfo = ref _uniformBuffers[index];

                if (!currentBufferRef.Equals(newRef) || currentInfo.Range != info.Range)
                {
                    _uniformBindingChanges++;

                    bool bufferChanged = currentBufferRef.Buffer != newRef.Buffer;
                    bool offsetChanged = currentBufferRef.Offset != newRef.Offset;
                    bool rangeChanged = currentInfo.Range != info.Range;
                    bool writeChanged = currentBufferRef.Write != newRef.Write;

                    if (bufferChanged || rangeChanged || writeChanged)
                    {
                        _uniformPendingDescriptorChange = true;
                    }
                    else if (offsetChanged)
                    {
                        _uniformPendingOffsetChange = true;
                    }

                    if (bufferChanged)
                    {
                        _uniformBufferChanges++;
                    }
                    else if (offsetChanged && rangeChanged && !writeChanged)
                    {
                        _uniformOffsetRangeChanges++;
                    }
                    else if (offsetChanged && !rangeChanged && !writeChanged)
                    {
                        _uniformOffsetOnlyChanges++;
                    }
                    else if (!offsetChanged && rangeChanged && !writeChanged)
                    {
                        _uniformRangeOnlyChanges++;
                    }
                    else
                    {
                        _uniformOtherSameBufferChanges++;
                    }

                    _uniformSet.Clear(index);
                    _uniformSetPd[index] = 0;

                    currentInfo = info;
                    currentBufferRef = newRef;
                    changed = true;
                }

                if ((assignments & 0xfffff) == 0)
                {
                    Ryujinx.Common.Logging.Logger.Info?.Print(
                        Ryujinx.Common.Logging.LogClass.Gpu,
                        $"[NEOV2-UNIFORM-CHANGE-PROFILE1] assignments={assignments} changes={_uniformBindingChanges} " +
                        $"buffer={_uniformBufferChanges} offsetOnly={_uniformOffsetOnlyChanges} rangeOnly={_uniformRangeOnlyChanges} " +
                        $"offsetRange={_uniformOffsetRangeChanges} otherSameBuffer={_uniformOtherSameBufferChanges} " +
                        $"flushes={_uniformFlushes} offsetOnlyFlushes={_uniformOffsetOnlyFlushes} " +
                        $"descriptorFlushes={_uniformDescriptorFlushes} otherFlushes={_uniformOtherFlushes} " +
                        $"dynamicFlushes={_dynamicUniformFlushes} dynamicUpdates={_dynamicUniformDescriptorUpdates} " +
                        $"dynamicRebinds={_dynamicUniformOffsetRebinds} dynamicResolvedChanges={_dynamicUniformResolvedChanges}");
                }
            }

            if (changed)
            {
                SignalDirty(DirtyFlags.Uniform);
            }
        }

        private void SignalDirty(DirtyFlags flag)
        {
            _dirty |= flag;
        }

        public void UpdateAndBindDescriptorSets(CommandBufferScoped cbs, PipelineBindPoint pbp)
        {
            // TOTK-C827-SAMPLERTRACE5
            LogTotkC827Sampler();

            if ((_dirty & DirtyFlags.All) == 0)
            {
                return;
            }

            ShaderCollection program = _program;

            if ((_dirty & DirtyFlags.Uniform) == DirtyFlags.Uniform)
            {
                _uniformFlushes++;

                if (_uniformPendingDescriptorChange)
                {
                    _uniformDescriptorFlushes++;
                }
                else if (_uniformPendingOffsetChange)
                {
                    _uniformOffsetOnlyFlushes++;
                }
                else
                {
                    _uniformOtherFlushes++;
                }

                if (program.UsePushDescriptors)
                {
                    UpdateAndBindUniformBufferPd(cbs);
                }
                else
                {
                    if (program.UseDynamicUniformBuffers)
                    {
                        UpdateAndBindDynamicUniformBuffers(cbs, program, pbp);
                    }
                    else
                    {
                        UpdateAndBind(cbs, program, PipelineBase.UniformSetIndex, pbp);
                    }
                }

                _uniformPendingOffsetChange = false;
                _uniformPendingDescriptorChange = false;
            }

            if ((_dirty & DirtyFlags.Storage) == DirtyFlags.Storage)
            {
                UpdateAndBind(cbs, program, PipelineBase.StorageSetIndex, pbp);
            }

            if ((_dirty & DirtyFlags.Texture) == DirtyFlags.Texture)
            {
                if (program.UpdateTexturesWithoutTemplate)
                {
                    UpdateAndBindTexturesWithoutTemplate(cbs, program, pbp);
                }
                else
                {
                    UpdateAndBind(cbs, program, PipelineBase.TextureSetIndex, pbp);
                }
            }

            if ((_dirty & DirtyFlags.Image) == DirtyFlags.Image)
            {
                UpdateAndBind(cbs, program, PipelineBase.ImageSetIndex, pbp);
            }

            if (program.BindingSegments.Length > PipelineBase.DescriptorSetLayouts)
            {
                // Program is using extra sets, we need to bind those too.

                BindExtraSets(cbs, program, pbp);
            }

            _dirty = DirtyFlags.None;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool UpdateBuffer(
            CommandBufferScoped cbs,
            ref DescriptorBufferInfo info,
            ref BufferRef buffer,
            Auto<DisposableBuffer> dummyBuffer,
            bool mirrorable)
        {
            int offset = buffer.Offset;
            bool mirrored = false;

            if (mirrorable)
            {
                info.Buffer = buffer.Buffer?.GetMirrorable(cbs, ref offset, (int)info.Range, out mirrored).Value ?? default;
            }
            else
            {
                info.Buffer = buffer.Buffer?.Get(cbs, offset, (int)info.Range, buffer.Write).Value ?? default;
            }

            info.Offset = (ulong)offset;

            // The spec requires that buffers with null handle have offset as 0 and range as VK_WHOLE_SIZE.
            if (info.Buffer.Handle == 0)
            {
                info.Buffer = dummyBuffer?.Get(cbs).Value ?? default;
                info.Offset = 0;
                info.Range = Vk.WholeSize;
            }

            return mirrored;
        }

        // NEOV2-DYNAMIC-UBO1
        // Keep the VkBuffer and range in the descriptor set, and move the frequently
        // changing byte offset to vkCmdBindDescriptorSets. This avoids allocating and
        // rewriting a descriptor set when only a uniform offset changed.
        private void UpdateAndBindDynamicUniformBuffers(
            CommandBufferScoped cbs,
            ShaderCollection program,
            PipelineBindPoint pbp)
        {
            ResourceBindingSegment[] bindingSegments = program.BindingSegments[PipelineBase.UniformSetIndex];

            if (bindingSegments.Length == 0)
            {
                return;
            }

            _dynamicUniformFlushes++;

            Auto<DisposableBuffer> dummyBuffer = _dummyBuffer?.GetBuffer();
            bool descriptorChanged = !_lastUniformDescriptorSetValid;
            int dynamicOffsetCount = 0;
            Span<uint> dynamicOffsets = stackalloc uint[Constants.MaxUniformBufferBindings];

            foreach (ResourceBindingSegment segment in bindingSegments)
            {
                int binding = segment.Binding;
                int count = segment.Count;

                for (int i = 0; i < count; i++)
                {
                    int index = binding + i;
                    bool needsResolve = _uniformSet.Set(index);

                    if (needsResolve)
                    {
                        ref BufferRef buffer = ref _uniformBufferRefs[index];
                        ref DescriptorBufferInfo resolved = ref _uniformBuffers[index];

                        bool mirrored = UpdateBuffer(cbs, ref resolved, ref buffer, dummyBuffer, true);
                        _uniformMirrored.Set(index, mirrored);
                    }

                    if (needsResolve || !_lastUniformDescriptorSetValid)
                    {
                        DescriptorBufferInfo resolved = _uniformBuffers[index];

                        // Vulkan dynamic offsets are uint32. Preserve any upper bits in
                        // the descriptor base offset so their sum is the original offset.
                        ulong descriptorBaseOffset = resolved.Offset & ~((ulong)uint.MaxValue);
                        uint dynamicOffset = (uint)(resolved.Offset - descriptorBaseOffset);

                        DescriptorBufferInfo descriptor = new()
                        {
                            Buffer = resolved.Buffer,
                            Offset = descriptorBaseOffset,
                            Range = resolved.Range,
                        };

                        ref DescriptorBufferInfo currentDescriptor = ref _uniformDynamicDescriptors[index];
                        bool resolvedDescriptorChanged =
                            currentDescriptor.Buffer.Handle != descriptor.Buffer.Handle ||
                            currentDescriptor.Offset != descriptor.Offset ||
                            currentDescriptor.Range != descriptor.Range;

                        if (_lastUniformDescriptorSetValid && resolvedDescriptorChanged)
                        {
                            _dynamicUniformResolvedChanges++;
                        }

                        descriptorChanged |= resolvedDescriptorChanged;
                        currentDescriptor = descriptor;
                        _uniformDynamicOffsets[index] = dynamicOffset;
                    }

                    dynamicOffsets[dynamicOffsetCount++] = _uniformDynamicOffsets[index];
                }
            }

            if (descriptorChanged)
            {
                if (_updateDescriptorCacheCbIndex)
                {
                    _updateDescriptorCacheCbIndex = false;
                    program.UpdateDescriptorCacheCommandBufferIndex(cbs.CommandBufferIndex);
                }

                DescriptorSetCollection dsc = program.GetNewDescriptorSetCollection(
                    PipelineBase.UniformSetIndex,
                    out bool isNew).Get(cbs);

                if (!program.HasMinimalLayout && isNew)
                {
                    Initialize(cbs, PipelineBase.UniformSetIndex, dsc);
                }

                DescriptorSetTemplateWriter tu = _templateUpdater.Begin(program.Templates[PipelineBase.UniformSetIndex]);

                foreach (ResourceBindingSegment segment in bindingSegments)
                {
                    ReadOnlySpan<DescriptorBufferInfo> descriptors = _uniformDynamicDescriptors;
                    tu.Push(descriptors.Slice(segment.Binding, segment.Count));
                }

                DescriptorSet[] sets = dsc.GetSets();
                _templateUpdater.Commit(_gd, _device, sets[0]);

                _lastUniformDescriptorSet = sets[0];
                _lastUniformDescriptorSetValid = true;
                _dynamicUniformDescriptorUpdates++;
            }
            else
            {
                _dynamicUniformOffsetRebinds++;
            }

            _gd.Api.CmdBindDescriptorSets(
                cbs.CommandBuffer,
                pbp,
                program.PipelineLayout,
                PipelineBase.UniformSetIndex,
                1,
                MemoryMarshal.CreateReadOnlySpan(ref _lastUniformDescriptorSet, 1),
                (uint)dynamicOffsetCount,
                dynamicOffsets[..dynamicOffsetCount]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateAndBind(CommandBufferScoped cbs, ShaderCollection program, int setIndex, PipelineBindPoint pbp)
        {
            ResourceBindingSegment[] bindingSegments = program.BindingSegments[setIndex];

            if (bindingSegments.Length == 0)
            {
                return;
            }

            Auto<DisposableBuffer> dummyBuffer = _dummyBuffer?.GetBuffer();

            if (_updateDescriptorCacheCbIndex)
            {
                _updateDescriptorCacheCbIndex = false;
                program.UpdateDescriptorCacheCommandBufferIndex(cbs.CommandBufferIndex);
            }

            DescriptorSetCollection dsc = program.GetNewDescriptorSetCollection(setIndex, out bool isNew).Get(cbs);

            if (!program.HasMinimalLayout)
            {
                if (isNew)
                {
                    Initialize(cbs, setIndex, dsc);
                }
            }

            DescriptorSetTemplate template = program.Templates[setIndex];

            DescriptorSetTemplateWriter tu = _templateUpdater.Begin(template);

            foreach (ResourceBindingSegment segment in bindingSegments)
            {
                int binding = segment.Binding;
                int count = segment.Count;

                if (setIndex == PipelineBase.UniformSetIndex)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int index = binding + i;

                        if (_uniformSet.Set(index))
                        {
                            ref BufferRef buffer = ref _uniformBufferRefs[index];

                            bool mirrored = UpdateBuffer(cbs, ref _uniformBuffers[index], ref buffer, dummyBuffer, true);

                            _uniformMirrored.Set(index, mirrored);
                        }
                    }

                    ReadOnlySpan<DescriptorBufferInfo> uniformBuffers = _uniformBuffers;

                    tu.Push(uniformBuffers.Slice(binding, count));
                }
                else if (setIndex == PipelineBase.StorageSetIndex)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int index = binding + i;

                        ref BufferRef buffer = ref _storageBufferRefs[index];

                        if (_storageSet.Set(index))
                        {
                            ref DescriptorBufferInfo info = ref _storageBuffers[index];

                            bool mirrored = UpdateBuffer(cbs,
                                ref info,
                                ref _storageBufferRefs[index],
                                dummyBuffer,
                                !buffer.Write && info.Range <= StorageBufferMaxMirrorable);

                            _storageMirrored.Set(index, mirrored);
                        }
                    }

                    ReadOnlySpan<DescriptorBufferInfo> storageBuffers = _storageBuffers;

                    tu.Push(storageBuffers.Slice(binding, count));
                }
                else if (setIndex == PipelineBase.TextureSetIndex)
                {
                    if (!segment.IsArray)
                    {
                        if (segment.Type != ResourceType.BufferTexture)
                        {
                            Span<DescriptorImageInfo> textures = _textures;

                            for (int i = 0; i < count; i++)
                            {
                                ref DescriptorImageInfo texture = ref textures[i];
                                ref TextureRef refs = ref _textureRefs[binding + i];

                                int textureBinding = binding + i;

                                // TOTK-C827-DEPTH-PRODUCER12-DS
                                TraceTotkC827DepthProducer(
                                    program,
                                    textureBinding,
                                    refs.View);

                                TextureView totkDepthCopy19b =
                                    GetTotkC827DepthCopy19b(
                                        cbs,
                                        program,
                                        textureBinding,
                                        refs.View);

                                TraceTotkC827BarrierState18(
                                    program,
                                    textureBinding,
                                    refs.View);

                                if (totkDepthCopy19b != null)
                                {
                                    texture.ImageView =
                                        totkDepthCopy19b.GetImageView().Get(cbs).Value;
                                }
                                else if (ShouldUseTotkC827DepthConst(program, textureBinding))
                                {
                                    TextureView depthConstTexture =
                                        _totkC827DepthConst == "1"
                                            ? _totkDepthOneTexture
                                            : _totkDepthZeroTexture;

                                    texture.ImageView = depthConstTexture.GetImageView().Get(cbs).Value;
                                }
                                else if (ShouldNeutralizeTotkC827Binding(program, textureBinding))
                                {
                                    texture.ImageView = _dummyTexture.GetImageView().Get(cbs).Value;
                                }
                                else
                                {
                                    texture.ImageView = refs.ImageView?.Get(cbs).Value ?? default;
                                }

                                texture.Sampler = refs.Sampler?.Get(cbs).Value ?? default;

                                if (ShouldUseTotkC827LinearB128(program, textureBinding))
                                {
                                    texture.Sampler = _totkC827LinearSampler.GetSampler().Get(cbs).Value;
                                }

                                if (texture.ImageView.Handle == 0)
                                {
                                    texture.ImageView = _dummyTexture.GetImageView().Get(cbs).Value;
                                }

                                if (texture.Sampler.Handle == 0)
                                {
                                    texture.Sampler = _dummySampler.GetSampler().Get(cbs).Value;
                                }
                            }

                            tu.Push<DescriptorImageInfo>(textures[..count]);
                        }
                        else
                        {
                            Span<BufferView> bufferTextures = _bufferTextures;

                            for (int i = 0; i < count; i++)
                            {
                                bufferTextures[i] = _bufferTextureRefs[binding + i]?.GetBufferView(cbs, false) ?? default;
                            }

                            tu.Push<BufferView>(bufferTextures[..count]);
                        }
                    }
                    else
                    {
                        if (segment.Type != ResourceType.BufferTexture)
                        {
                            tu.Push(_textureArrayRefs[binding].Array.GetImageInfos(_gd, cbs, _dummyTexture, _dummySampler));
                        }
                        else
                        {
                            tu.Push(_textureArrayRefs[binding].Array.GetBufferViews(cbs));
                        }
                    }
                }
                else if (setIndex == PipelineBase.ImageSetIndex)
                {
                    if (!segment.IsArray)
                    {
                        if (segment.Type != ResourceType.BufferImage)
                        {
                            Span<DescriptorImageInfo> images = _images;

                            for (int i = 0; i < count; i++)
                            {
                                images[i].ImageView = _imageRefs[binding + i].ImageView?.Get(cbs).Value ?? default;
                            }

                            tu.Push<DescriptorImageInfo>(images[..count]);
                        }
                        else
                        {
                            Span<BufferView> bufferImages = _bufferImages;

                            for (int i = 0; i < count; i++)
                            {
                                bufferImages[i] = _bufferImageRefs[binding + i]?.GetBufferView(cbs, true) ?? default;
                            }

                            tu.Push<BufferView>(bufferImages[..count]);
                        }
                    }
                    else
                    {
                        if (segment.Type != ResourceType.BufferTexture)
                        {
                            tu.Push(_imageArrayRefs[binding].Array.GetImageInfos(_gd, cbs, _dummyTexture));
                        }
                        else
                        {
                            tu.Push(_imageArrayRefs[binding].Array.GetBufferViews(cbs));
                        }
                    }
                }
            }

            DescriptorSet[] sets = dsc.GetSets();
            _templateUpdater.Commit(_gd, _device, sets[0]);

            _gd.Api.CmdBindDescriptorSets(cbs.CommandBuffer, pbp, _program.PipelineLayout, (uint)setIndex, 1, sets, 0, ReadOnlySpan<uint>.Empty);
        }

        private void UpdateAndBindTexturesWithoutTemplate(CommandBufferScoped cbs, ShaderCollection program, PipelineBindPoint pbp)
        {
            int setIndex = PipelineBase.TextureSetIndex;
            ResourceBindingSegment[] bindingSegments = program.BindingSegments[setIndex];

            if (bindingSegments.Length == 0)
            {
                return;
            }

            if (_updateDescriptorCacheCbIndex)
            {
                _updateDescriptorCacheCbIndex = false;
                program.UpdateDescriptorCacheCommandBufferIndex(cbs.CommandBufferIndex);
            }

            DescriptorSetCollection dsc = program.GetNewDescriptorSetCollection(setIndex, out _).Get(cbs);

            foreach (ResourceBindingSegment segment in bindingSegments)
            {
                int binding = segment.Binding;
                int count = segment.Count;

                if (!segment.IsArray)
                {
                    if (segment.Type != ResourceType.BufferTexture)
                    {
                        Span<DescriptorImageInfo> textures = _textures;

                        for (int i = 0; i < count; i++)
                        {
                            ref DescriptorImageInfo texture = ref textures[i];
                            ref TextureRef refs = ref _textureRefs[binding + i];

                            int textureBinding = binding + i;

                            // TOTK-C827-DEPTH-PRODUCER12-DS
                            TraceTotkC827DepthProducer(
                                program,
                                textureBinding,
                                refs.View);

                            if (ShouldUseTotkC827DepthConst(program, textureBinding))
                            {
                                TextureView depthConstTexture =
                                    _totkC827DepthConst == "1"
                                        ? _totkDepthOneTexture
                                        : _totkDepthZeroTexture;

                                texture.ImageView = depthConstTexture.GetImageView().Get(cbs).Value;
                            }
                            else if (ShouldNeutralizeTotkC827Binding(program, textureBinding))
                            {
                                texture.ImageView = _dummyTexture.GetImageView().Get(cbs).Value;
                            }
                            else
                            {
                                texture.ImageView = refs.ImageView?.Get(cbs).Value ?? default;
                            }

                            texture.Sampler = refs.Sampler?.Get(cbs).Value ?? default;

                            if (ShouldUseTotkC827LinearB128(program, textureBinding))
                            {
                                texture.Sampler = _totkC827LinearSampler.GetSampler().Get(cbs).Value;
                            }

                            if (texture.ImageView.Handle == 0)
                            {
                                texture.ImageView = _dummyTexture.GetImageView().Get(cbs).Value;
                            }

                            if (texture.Sampler.Handle == 0)
                            {
                                texture.Sampler = _dummySampler.GetSampler().Get(cbs).Value;
                            }
                        }

                        dsc.UpdateImages(0, binding, textures[..count], DescriptorType.CombinedImageSampler);
                    }
                    else
                    {
                        Span<BufferView> bufferTextures = _bufferTextures;

                        for (int i = 0; i < count; i++)
                        {
                            bufferTextures[i] = _bufferTextureRefs[binding + i]?.GetBufferView(cbs, false) ?? default;
                        }

                        dsc.UpdateBufferImages(0, binding, bufferTextures[..count], DescriptorType.UniformTexelBuffer);
                    }
                }
                else
                {
                    if (segment.Type != ResourceType.BufferTexture)
                    {
                        dsc.UpdateImages(0, binding, _textureArrayRefs[binding].Array.GetImageInfos(_gd, cbs, _dummyTexture, _dummySampler), DescriptorType.CombinedImageSampler);
                    }
                    else
                    {
                        dsc.UpdateBufferImages(0, binding, _textureArrayRefs[binding].Array.GetBufferViews(cbs), DescriptorType.UniformTexelBuffer);
                    }
                }
            }

            DescriptorSet[] sets = dsc.GetSets();

            _gd.Api.CmdBindDescriptorSets(cbs.CommandBuffer, pbp, _program.PipelineLayout, (uint)setIndex, 1, sets, 0, ReadOnlySpan<uint>.Empty);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateAndBindUniformBufferPd(CommandBufferScoped cbs)
        {
            int sequence = _pdSequence;
            ResourceBindingSegment[] bindingSegments = _program.BindingSegments[PipelineBase.UniformSetIndex];
            Auto<DisposableBuffer> dummyBuffer = _dummyBuffer?.GetBuffer();

            long updatedBindings = 0;
            DescriptorSetTemplateWriter writer = _templateUpdater.Begin(32 * Unsafe.SizeOf<DescriptorBufferInfo>());

            foreach (ResourceBindingSegment segment in bindingSegments)
            {
                int binding = segment.Binding;
                int count = segment.Count;

                ReadOnlySpan<DescriptorBufferInfo> uniformBuffers = _uniformBuffers;

                for (int i = 0; i < count; i++)
                {
                    int index = binding + i;

                    if (_uniformSet.Set(index))
                    {
                        ref BufferRef buffer = ref _uniformBufferRefs[index];

                        bool mirrored = UpdateBuffer(cbs, ref _uniformBuffers[index], ref buffer, dummyBuffer, true);

                        _uniformMirrored.Set(index, mirrored);
                    }

                    if (_uniformSetPd[index] != sequence)
                    {
                        // Need to set this push descriptor (even if the buffer binding has not changed)

                        _uniformSetPd[index] = sequence;
                        updatedBindings |= 1L << index;

                        writer.Push(MemoryMarshal.CreateReadOnlySpan(ref _uniformBuffers[index], 1));
                    }
                }
            }

            if (updatedBindings > 0)
            {
                DescriptorSetTemplate template = _program.GetPushDescriptorTemplate(updatedBindings);
                _templateUpdater.CommitPushDescriptor(_gd, cbs, template, _program.PipelineLayout);
            }
        }

        private void Initialize(CommandBufferScoped cbs, int setIndex, DescriptorSetCollection dsc)
        {
            // We don't support clearing texture descriptors currently.
            if (setIndex != PipelineBase.UniformSetIndex && setIndex != PipelineBase.StorageSetIndex)
            {
                return;
            }

            Buffer dummyBuffer = _dummyBuffer?.GetBuffer().Get(cbs).Value ?? default;

            foreach (ResourceBindingSegment segment in _program.ClearSegments[setIndex])
            {
                DescriptorType descriptorType = _program.UseDynamicUniformBuffers &&
                    setIndex == PipelineBase.UniformSetIndex &&
                    segment.Type == ResourceType.UniformBuffer
                        ? DescriptorType.UniformBufferDynamic
                        : segment.Type.Convert();

                dsc.InitializeBuffers(0, segment.Binding, segment.Count, descriptorType, dummyBuffer);
            }
        }

        private void BindExtraSets(CommandBufferScoped cbs, ShaderCollection program, PipelineBindPoint pbp)
        {
            for (int setIndex = PipelineBase.DescriptorSetLayouts; setIndex < program.BindingSegments.Length; setIndex++)
            {
                ResourceBindingSegment[] bindingSegments = program.BindingSegments[setIndex];

                if (bindingSegments.Length == 0)
                {
                    continue;
                }

                ResourceBindingSegment segment = bindingSegments[0];

                if (segment.IsArray)
                {
                    DescriptorSet[] sets = null;

                    if (segment.Type is ResourceType.Texture
                        or ResourceType.Sampler
                        or ResourceType.TextureAndSampler
                        or ResourceType.BufferTexture)
                    {
                        sets = _textureArrayExtraRefs[setIndex - PipelineBase.DescriptorSetLayouts].Array.GetDescriptorSets(
                            _device,
                            cbs,
                            _templateUpdater,
                            program,
                            setIndex,
                            _dummyTexture,
                            _dummySampler);
                    }
                    else if (segment.Type is ResourceType.Image or ResourceType.BufferImage)
                    {
                        sets = _imageArrayExtraRefs[setIndex - PipelineBase.DescriptorSetLayouts].Array.GetDescriptorSets(
                            _device,
                            cbs,
                            _templateUpdater,
                            program,
                            setIndex,
                            _dummyTexture);
                    }

                    if (sets != null)
                    {
                        _gd.Api.CmdBindDescriptorSets(cbs.CommandBuffer, pbp, _program.PipelineLayout, (uint)setIndex, 1, sets, 0, ReadOnlySpan<uint>.Empty);
                    }
                }
            }
        }

        public void SignalCommandBufferChange()
        {
            _updateDescriptorCacheCbIndex = true;
            _lastUniformDescriptorSetValid = false;
            _dirty = DirtyFlags.All;

            _uniformSet.Clear();
            _storageSet.Clear();
            AdvancePdSequence();
        }

        public void ForceTextureDirty()
        {
            SignalDirty(DirtyFlags.Texture);
        }

        public void ForceImageDirty()
        {
            SignalDirty(DirtyFlags.Image);
        }

        private static void SwapBuffer(BufferRef[] list, Auto<DisposableBuffer> from, Auto<DisposableBuffer> to)
        {
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].Buffer == from)
                {
                    list[i].Buffer = to;
                }
            }
        }

        public void SwapBuffer(Auto<DisposableBuffer> from, Auto<DisposableBuffer> to)
        {
            SwapBuffer(_uniformBufferRefs, from, to);
            SwapBuffer(_storageBufferRefs, from, to);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _totkDepthZeroTexture.Dispose();
                _totkDepthOneTexture.Dispose();
                _dummyTexture.Dispose();
                _totkC827LinearSampler.Dispose();
                _dummySampler.Dispose();
                _templateUpdater.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
