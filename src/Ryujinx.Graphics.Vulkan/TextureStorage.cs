using Ryujinx.Common.Diagnostics;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Format = Ryujinx.Graphics.GAL.Format;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Ryujinx.Graphics.Vulkan
{
    class TextureStorage : IDisposable
    {
        private struct TextureSliceInfo
        {
            public int BindCount;
        }

        private const MemoryPropertyFlags DefaultImageMemoryFlags =
            MemoryPropertyFlags.DeviceLocalBit;

        private const ImageUsageFlags DefaultUsageFlags =
            ImageUsageFlags.SampledBit |
            ImageUsageFlags.TransferSrcBit |
            ImageUsageFlags.TransferDstBit;

        public const AccessFlags DefaultAccessMask =
            AccessFlags.ShaderReadBit |
            AccessFlags.ShaderWriteBit |
            AccessFlags.ColorAttachmentReadBit |
            AccessFlags.ColorAttachmentWriteBit |
            AccessFlags.DepthStencilAttachmentReadBit |
            AccessFlags.DepthStencilAttachmentWriteBit |
            AccessFlags.TransferReadBit |
            AccessFlags.TransferWriteBit;

        private readonly VulkanRenderer _gd;

        private readonly Device _device;

        private TextureCreateInfo _info;

        public TextureCreateInfo Info => _info;

        public bool Disposed { get; private set; }

        private readonly Image _image;
        private readonly Auto<DisposableImage> _imageAuto;
        private readonly Auto<MemoryAllocation> _allocationAuto;
        private readonly int _depthOrLayers;
        private Auto<MemoryAllocation> _foreignAllocationAuto;

        private Dictionary<Format, TextureStorage> _aliasedStorages;

        private AccessFlags _lastModificationAccess;
        private PipelineStageFlags _lastModificationStage;
        private AccessFlags _lastReadAccess;
        private PipelineStageFlags _lastReadStage;

        // TOTK-WRITE-GENERATION1
        // Diagnostic only. Incremented whenever this storage completes
        // a render pass as a color/depth attachment.
        private long _totkWriteGeneration;
        public long TotkWriteGeneration => System.Threading.Interlocked.Read(ref _totkWriteGeneration);

        // TOTK-C827-DEPTH-PRODUCER12
        // Guarda os ultimos draws que usaram ESTA storage como depth attachment.
        private readonly object _totkDepthDrawHistoryLock = new();
        private readonly Queue<string> _totkDepthDrawHistory = new();

        public void TotkRecordDepthDraw(
            string hash,
            ulong draw,
            bool passWritesDepthStencil,
            string viewInfo)
        {
            lock (_totkDepthDrawHistoryLock)
            {
                while (_totkDepthDrawHistory.Count >= 128)
                {
                    _totkDepthDrawHistory.Dequeue();
                }

                _totkDepthDrawHistory.Enqueue(
                    $"HASH={hash ?? "<none>"} " +
                    $"DRAW={draw} " +
                    $"DEPTHWRITE={passWritesDepthStencil} " +
                    $"VIEW={viewInfo}");
            }
        }

        public string[] TotkGetDepthDrawHistory()
        {
            lock (_totkDepthDrawHistoryLock)
            {
                return _totkDepthDrawHistory.ToArray();
            }
        }

        private int _viewsCount;
        private readonly ulong _size;

        private int _bindCount;
        private readonly TextureSliceInfo[] _slices;

        public VkFormat VkFormat { get; }

        public ImageUsageFlags UsageFlags { get; }

        // NEOV2 Etapa 4A: estado de telemetria de formatos (somente leitura, nao altera renderizacao).
        private readonly bool _telemetryTracked;
        private bool _telemetryIsRt;
        private bool _telemetryDisposed;
        private int _telemetryFlags;

        // NEOV2 Etapa 13: formatos de view declarados na criacao da imagem (Turnip). Usado para detectar
        // views fora da lista, que nao seriam validas com a lista reduzida para manter o UBWC.
        private VkFormat[] _turnipViewFormats;
        private static int _turnipViewOutsideListLogs;
        private static bool _turnipUbwcListLogged;
        private static readonly object _telemetryLock = new();

        public unsafe TextureStorage(
            VulkanRenderer gd,
            Device device,
            TextureCreateInfo info,
            Auto<MemoryAllocation> foreignAllocation = null)
        {
            _gd = gd;
            _device = device;
            _info = info;

            bool isMsImageStorageSupported = gd.Capabilities.SupportsShaderStorageImageMultisample || !info.Target.IsMultisample();

            VkFormat format = _gd.FormatCapabilities.ConvertToVkFormat(info.Format, isMsImageStorageSupported);
            uint levels = (uint)info.Levels;
            uint layers = (uint)info.GetLayers();
            uint depth = (uint)(info.Target == Target.Texture3D ? info.Depth : 1);

            VkFormat = format;
            _depthOrLayers = info.GetDepthOrLayers();

            ImageType type = info.Target.Convert();

            Extent3D extent = new((uint)info.Width, (uint)info.Height, depth);

            SampleCountFlags sampleCountFlags = ConvertToSampleCountFlags(gd.Capabilities.SupportedSampleCounts, (uint)info.Samples);

            ImageUsageFlags usage = GetImageUsage(info.Format, info.Target, gd.Capabilities, isMsImageStorageSupported);
            UsageFlags = usage;

            var flags = ImageCreateFlags.CreateMutableFormatBit | ImageCreateFlags.CreateExtendedUsageBit;

            // This flag causes mipmapped texture arrays to break on AMD GCN, so for that copy dependencies are forced for aliasing as cube.
            bool isCube = info.Target is Target.Cubemap or Target.CubemapArray;
            bool cubeCompatible = gd.IsAmdGcn ? isCube : (info.Width == info.Height && layers >= 6);

            if (type == ImageType.Type2D && cubeCompatible)
            {
                flags |= ImageCreateFlags.CreateCubeCompatibleBit;
            }

            if (type == ImageType.Type3D && !gd.Capabilities.PortabilitySubset.HasFlag(PortabilitySubsetFlags.No3DImageView))
            {
                flags |= ImageCreateFlags.Create2DArrayCompatibleBit;
            }

            ImageCreateInfo imageCreateInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = type,
                Format = format,
                Extent = extent,
                MipLevels = levels,
                ArrayLayers = layers,
                Samples = sampleCountFlags,
                Tiling = ImageTiling.Optimal,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
                Flags = flags,
            };

            // NEOV2 Etapa 4B: no Turnip, uma imagem MUTABLE_FORMAT sem VkImageFormatListCreateInfo
            // e forcada para layout linear e perde UBWC. Informamos a lista exata de formatos de view
            // que o backend pode criar para esta imagem. Views de outros formatos vindas da camada GPU
            // viram copia (SupportsMismatchingViewFormat = false no Turnip).
            VkFormat* viewFormats = stackalloc VkFormat[MaxTurnipViewFormats];
            ImageFormatListCreateInfo formatList = default;

            if (gd.IsTurnip && foreignAllocation == null && !VulkanRenderer.TotkMismatchViewsE20)
            {
                int viewFormatCount = BuildTurnipViewFormatList(gd, info, format, isMsImageStorageSupported, viewFormats);

                if (viewFormatCount > 0)
                {
                    _turnipViewFormats = new VkFormat[viewFormatCount];

                    for (int i = 0; i < viewFormatCount; i++)
                    {
                        _turnipViewFormats[i] = viewFormats[i];
                    }

                    formatList = new ImageFormatListCreateInfo
                    {
                        SType = StructureType.ImageFormatListCreateInfo,
                        ViewFormatCount = (uint)viewFormatCount,
                        PViewFormats = viewFormats,
                    };

                    imageCreateInfo.PNext = &formatList;
                }
            }
            long neoS0 = System.Diagnostics.Stopwatch.GetTimestamp();
            gd.Api.CreateImage(device, in imageCreateInfo, null, out _image).ThrowOnError();
            if (Ryujinx.Common.Diagnostics.NeoHitchStats.Enabled) Ryujinx.Common.Diagnostics.NeoHitchStats.AddStep(0, neoS0);

            if (foreignAllocation == null)
            {
                gd.Api.GetImageMemoryRequirements(device, _image, out MemoryRequirements requirements);
                long neoS1 = System.Diagnostics.Stopwatch.GetTimestamp();
                MemoryAllocation allocation = gd.MemoryAllocator.AllocateDeviceMemory(requirements, DefaultImageMemoryFlags);
                if (Ryujinx.Common.Diagnostics.NeoHitchStats.Enabled) Ryujinx.Common.Diagnostics.NeoHitchStats.AddStep(1, neoS1);

                if (allocation.Memory.Handle == 0UL)
                {
                    gd.Api.DestroyImage(device, _image, null);
                    throw new Exception("Image initialization failed.");
                }

                _size = requirements.Size;

                long neoS2 = System.Diagnostics.Stopwatch.GetTimestamp();
                gd.Api.BindImageMemory(device, _image, allocation.Memory, allocation.Offset).ThrowOnError();
                if (Ryujinx.Common.Diagnostics.NeoHitchStats.Enabled) Ryujinx.Common.Diagnostics.NeoHitchStats.AddStep(2, neoS2);

                _allocationAuto = new Auto<MemoryAllocation>(allocation);
                _imageAuto = new Auto<DisposableImage>(new DisposableImage(_gd.Api, device, _image), null, _allocationAuto);

                long neoS3 = System.Diagnostics.Stopwatch.GetTimestamp();
                InitialTransition(ImageLayout.Undefined, ImageLayout.General);
                if (Ryujinx.Common.Diagnostics.NeoHitchStats.Enabled) Ryujinx.Common.Diagnostics.NeoHitchStats.AddStep(3, neoS3);

                _telemetryTracked = GezinjinxPerformanceMode.DetailedTelemetry;
                TextureFormatTelemetry.RecordStorageCreated((long)_size);
            }
            else
            {
                _foreignAllocationAuto = foreignAllocation;
                foreignAllocation.IncrementReferenceCount();
                MemoryAllocation allocation = foreignAllocation.GetUnsafe();

                gd.Api.BindImageMemory(device, _image, allocation.Memory, allocation.Offset).ThrowOnError();

                _imageAuto = new Auto<DisposableImage>(new DisposableImage(_gd.Api, device, _image));

                InitialTransition(ImageLayout.Undefined, ImageLayout.General);

                TextureFormatTelemetry.RecordAliasImageCreated();
            }

            _slices = new TextureSliceInfo[levels * _depthOrLayers];
        }

        private const int MaxTurnipViewFormats = 8;

        /// <summary>
        /// NEOV2 Etapa 4B: monta a lista de formatos de view para imagens de cor nao comprimidas no Turnip.
        /// Inclui o formato da imagem e os formatos uint que o HelperShader usa em copias de reinterpretacao
        /// (GetFormat(bpp) e GetFormat(componentSize, count)). Retorna 0 quando a imagem deve ficar como antes.
        /// </summary>
        private static unsafe int BuildTurnipViewFormatList(
            VulkanRenderer gd,
            TextureCreateInfo info,
            VkFormat format,
            bool isMsImageStorageSupported,
            VkFormat* list)
        {
            if (info.IsCompressed || info.Format.IsDepthOrStencil())
            {
                return 0;
            }

            // Formato substituido pelo backend (ex.: decodificacao ou fallback de suporte): mantem o comportamento atual.
            if (format != FormatTable.GetFormat(info.Format))
            {
                return 0;
            }

            int count = 0;
            list[count++] = format;

            // sRGB/UNORM counterpart (same UBWC class): lets the GPU layer create a real view instead of a copy.
            Format srgbPair = info.Format switch
            {
                Format.R8G8B8A8Unorm => Format.R8G8B8A8Srgb,
                Format.R8G8B8A8Srgb => Format.R8G8B8A8Unorm,
                Format.B8G8R8A8Unorm => Format.B8G8R8A8Srgb,
                Format.B8G8R8A8Srgb => Format.B8G8R8A8Unorm,
                _ => info.Format,
            };

            if (srgbPair != info.Format)
            {
                AddViewFormat(gd, srgbPair, isMsImageStorageSupported, list, ref count);
            }

            switch (info.BytesPerPixel)
            {
                case 1:
                    AddViewFormat(gd, Format.R8Uint, isMsImageStorageSupported, list, ref count);
                    break;
                case 2:
                    AddViewFormat(gd, Format.R8G8Uint, isMsImageStorageSupported, list, ref count);
                    AddViewFormat(gd, Format.R16Uint, isMsImageStorageSupported, list, ref count);
                    break;
                case 4:
                    AddViewFormat(gd, Format.R8G8B8A8Uint, isMsImageStorageSupported, list, ref count);
                    AddViewFormat(gd, Format.R16G16Uint, isMsImageStorageSupported, list, ref count);
                    AddViewFormat(gd, Format.R32Uint, isMsImageStorageSupported, list, ref count);
                    break;
                case 8:
                    AddViewFormat(gd, Format.R16G16B16A16Uint, isMsImageStorageSupported, list, ref count);
                    AddViewFormat(gd, Format.R32G32Uint, isMsImageStorageSupported, list, ref count);
                    break;
                case 16:
                    AddViewFormat(gd, Format.R32G32B32A32Uint, isMsImageStorageSupported, list, ref count);
                    break;
                default:
                    return 0;
            }

            // NEOV2 Etapa 13: o Turnip so mantem UBWC (compressao) em imagens MUTABLE quando todos os
            // formatos da lista sao da mesma classe de compatibilidade UBWC. Formatos de classe diferente
            // desligam a compressao da imagem inteira. Mantemos apenas os formatos da mesma classe.
            bool unormIntCompatible = IsUbwcUnormIntCompatible(gd);
            int ownClass = GetUbwcCompatClass(format, unormIntCompatible);
            int kept = 1;

            for (int i = 1; i < count; i++)
            {
                if (ownClass != 0 && GetUbwcCompatClass(list[i], unormIntCompatible) == ownClass)
                {
                    list[kept++] = list[i];
                }
            }

            if (!_turnipUbwcListLogged)
            {
                _turnipUbwcListLogged = true;
                Logger.Info?.Print(LogClass.Gpu, $"[NEOV2-ETAPA13] Turnip UBWC-compatible view format lists: Enabled (unormIntCompatible={unormIntCompatible})");
            }

            return kept;
        }

        private static bool IsUbwcUnormIntCompatible(VulkanRenderer gd)
        {
            // Adreno 740/750 e 8xx: UNORM, SNORM, SRGB e INT do mesmo layout compartilham a classe UBWC.
            if (gd.IsAdreno8xx)
            {
                return true;
            }

            System.Text.RegularExpressions.Match match = VendorUtils.Adreno7xxRegex().Match(gd.GpuRenderer ?? string.Empty);

            return match.Success &&
                int.TryParse(match.Groups[2].Value.TrimEnd('L'), out int model) &&
                model >= 740;
        }

        /// <summary>
        /// NEOV2 Etapa 13: classe de compatibilidade UBWC do Turnip (fd6_ubwc_compat_mode).
        /// 0 = desconhecida (so e compativel consigo mesma).
        /// </summary>
        private static int GetUbwcCompatClass(VkFormat format, bool unormIntCompatible)
        {
            switch (format)
            {
                case VkFormat.R8G8Unorm:
                case VkFormat.R8G8Srgb:
                    return unormIntCompatible ? 2 : 1;
                case VkFormat.R8G8SNorm:
                    return unormIntCompatible ? 2 : 0;
                case VkFormat.R8G8Uint:
                case VkFormat.R8G8Sint:
                    return 2;

                case VkFormat.R8G8B8A8Unorm:
                case VkFormat.R8G8B8A8Srgb:
                    return unormIntCompatible ? 4 : 3;
                case VkFormat.R8G8B8A8SNorm:
                    return unormIntCompatible ? 4 : 0;
                case VkFormat.R8G8B8A8Uint:
                case VkFormat.R8G8B8A8Sint:
                    return 4;

                case VkFormat.B8G8R8A8Unorm:
                case VkFormat.B8G8R8A8Srgb:
                    return 5;

                case VkFormat.R16G16Unorm:
                    return unormIntCompatible ? 7 : 6;
                case VkFormat.R16G16SNorm:
                    return unormIntCompatible ? 7 : 0;
                case VkFormat.R16G16Uint:
                case VkFormat.R16G16Sint:
                    return 7;

                case VkFormat.R16G16B16A16Unorm:
                    return unormIntCompatible ? 9 : 8;
                case VkFormat.R16G16B16A16SNorm:
                    return unormIntCompatible ? 9 : 0;
                case VkFormat.R16G16B16A16Uint:
                case VkFormat.R16G16B16A16Sint:
                    return 9;

                case VkFormat.R32Uint:
                case VkFormat.R32Sint:
                    return 10;
                case VkFormat.R32G32Uint:
                case VkFormat.R32G32Sint:
                    return 11;
                case VkFormat.R32G32B32A32Uint:
                case VkFormat.R32G32B32A32Sint:
                    return 12;

                default:
                    return 0;
            }
        }

        private static unsafe void AddViewFormat(VulkanRenderer gd, Format galFormat, bool isMsImageStorageSupported, VkFormat* list, ref int count)
        {
            // Mesmo caminho de conversao usado pelo construtor de TextureView.
            VkFormat vkFormat = gd.FormatCapabilities.ConvertToVkFormat(galFormat, isMsImageStorageSupported);

            for (int i = 0; i < count; i++)
            {
                if (list[i] == vkFormat)
                {
                    return;
                }
            }

            if (count < MaxTurnipViewFormats)
            {
                list[count++] = vkFormat;
            }
        }

        public TextureStorage CreateAliasedColorForDepthStorageUnsafe(Format format)
        {
            Format colorFormat = format switch
            {
                Format.S8Uint => Format.R8Unorm,
                Format.D16Unorm => Format.R16Unorm,
                Format.D24UnormS8Uint or Format.S8UintD24Unorm or Format.X8UintD24Unorm => Format.R8G8B8A8Unorm,
                Format.D32Float => Format.R32Float,
                Format.D32FloatS8Uint => Format.R32G32Float,
                _ => throw new ArgumentException($"\"{format}\" is not a supported depth or stencil format."),
            };

            return CreateAliasedStorageUnsafe(colorFormat);
        }

        public TextureStorage CreateAliasedStorageUnsafe(Format format)
        {
            if (_aliasedStorages == null || !_aliasedStorages.TryGetValue(format, out TextureStorage storage))
            {
                _aliasedStorages ??= new Dictionary<Format, TextureStorage>();

                TextureCreateInfo info = NewCreateInfoWith(ref _info, format, _info.BytesPerPixel);

                storage = new TextureStorage(_gd, _device, info, _allocationAuto);

                _aliasedStorages.Add(format, storage);

                AddTelemetryFlags(TextureFormatTelemetry.FlagAliased);
            }

            return storage;
        }

        public static TextureCreateInfo NewCreateInfoWith(ref TextureCreateInfo info, Format format, int bytesPerPixel)
        {
            return NewCreateInfoWith(ref info, format, bytesPerPixel, info.Width, info.Height);
        }

        public static TextureCreateInfo NewCreateInfoWith(
            ref TextureCreateInfo info,
            Format format,
            int bytesPerPixel,
            int width,
            int height)
        {
            return new TextureCreateInfo(
                width,
                height,
                info.Depth,
                info.Levels,
                info.Samples,
                info.BlockWidth,
                info.BlockHeight,
                bytesPerPixel,
                format,
                info.DepthStencilMode,
                info.Target,
                info.SwizzleR,
                info.SwizzleG,
                info.SwizzleB,
                info.SwizzleA);
        }

        public Auto<DisposableImage> GetImage()
        {
            return _imageAuto;
        }

        public Image GetImageForViewCreation()
        {
            return _image;
        }

        public bool HasCommandBufferDependency(CommandBufferScoped cbs)
        {
            if (_foreignAllocationAuto != null)
            {
                return _foreignAllocationAuto.HasCommandBufferDependency(cbs);
            }
            else if (_allocationAuto != null)
            {
                return _allocationAuto.HasCommandBufferDependency(cbs);
            }

            return false;
        }

        private unsafe void InitialTransition(ImageLayout srcLayout, ImageLayout dstLayout)
        {
            CommandBufferScoped cbs;
            bool useTempCbs = !_gd.CommandBufferPool.OwnedByCurrentThread;

            if (useTempCbs)
            {
                cbs = _gd.BackgroundResources.Get().GetPool().Rent();
            }
            else
            {
                if (_gd.PipelineInternal != null)
                {
                    cbs = _gd.PipelineInternal.GetPreloadCommandBuffer();
                }
                else
                {
                    cbs = _gd.CommandBufferPool.Rent();
                    useTempCbs = true;
                }
            }

            ImageAspectFlags aspectFlags = _info.Format.ConvertAspectFlags();

            ImageSubresourceRange subresourceRange = new(aspectFlags, 0, (uint)_info.Levels, 0, (uint)_info.GetLayers());

            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = DefaultAccessMask,
                OldLayout = srcLayout,
                NewLayout = dstLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _imageAuto.Get(cbs).Value,
                SubresourceRange = subresourceRange,
            };

            _gd.Api.CmdPipelineBarrier(
                cbs.CommandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                1,
                in barrier);

            if (useTempCbs)
            {
                cbs.Dispose();
            }
        }

        public static ImageUsageFlags GetImageUsage(Format format, Target target, in HardwareCapabilities capabilities, bool isMsImageStorageSupported)
        {
            ImageUsageFlags usage = DefaultUsageFlags;

            if (format.IsDepthOrStencil())
            {
                usage |= ImageUsageFlags.DepthStencilAttachmentBit;
            }
            else if (format.IsRtColorCompatible())
            {
                usage |= ImageUsageFlags.ColorAttachmentBit;
            }

            if (format.IsImageCompatible() && (isMsImageStorageSupported || !target.IsMultisample()))
            {
                usage |= ImageUsageFlags.StorageBit;
            }

            if (capabilities.SupportsAttachmentFeedbackLoop &&
                (usage & (ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.ColorAttachmentBit)) != 0)
            {
                usage |= ImageUsageFlags.AttachmentFeedbackLoopBitExt;
            }

            return usage;
        }

        public static SampleCountFlags ConvertToSampleCountFlags(SampleCountFlags supportedSampleCounts, uint samples)
        {
            if (samples is 0 or > (uint)SampleCountFlags.Count64Bit)
            {
                return SampleCountFlags.Count1Bit;
            }

            // Round up to the nearest power of two.
            SampleCountFlags converted = (SampleCountFlags)(1u << (31 - BitOperations.LeadingZeroCount(samples)));

            // Pick nearest sample count that the host actually supports.
            while (converted != SampleCountFlags.Count1Bit && (converted & supportedSampleCounts) == 0)
            {
                converted = (SampleCountFlags)((uint)converted >> 1);
            }

            return converted;
        }

        public TextureView CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
        {
            return new TextureView(_gd, _device, info, this, firstLayer, firstLevel);
        }

        public void CopyFromOrToBuffer(
            CommandBuffer commandBuffer,
            VkBuffer buffer,
            Image image,
            int size,
            bool to,
            int x,
            int y,
            int dstLayer,
            int dstLevel,
            int dstLayers,
            int dstLevels,
            bool singleSlice,
            ImageAspectFlags aspectFlags,
            bool forFlush)
        {
            bool is3D = Info.Target == Target.Texture3D;
            int width = Info.Width;
            int height = Info.Height;
            int depth = is3D && !singleSlice ? Info.Depth : 1;
            int layer = is3D ? 0 : dstLayer;
            int layers = dstLayers;
            int levels = dstLevels;

            int offset = 0;

            for (int level = 0; level < levels; level++)
            {
                int mipSize = Info.GetMipSize(level);

                if (forFlush)
                {
                    mipSize = GetBufferDataLength(mipSize);
                }

                int endOffset = offset + mipSize;

                if ((uint)endOffset > (uint)size)
                {
                    break;
                }

                int rowLength = (Info.GetMipStride(level) / Info.BytesPerPixel) * Info.BlockWidth;

                ImageSubresourceLayers sl = new(
                    aspectFlags,
                    (uint)(dstLevel + level),
                    (uint)layer,
                    (uint)layers);

                Extent3D extent = new((uint)width, (uint)height, (uint)depth);

                int z = is3D ? dstLayer : 0;

                BufferImageCopy region = new(
                    (ulong)offset,
                    (uint)BitUtils.AlignUp(rowLength, Info.BlockWidth),
                    (uint)BitUtils.AlignUp(height, Info.BlockHeight),
                    sl,
                    new Offset3D(x, y, z),
                    extent);

                if (to)
                {
                    _gd.Api.CmdCopyImageToBuffer(commandBuffer, image, ImageLayout.General, buffer, 1, in region);
                }
                else
                {
                    _gd.Api.CmdCopyBufferToImage(commandBuffer, buffer, image, ImageLayout.General, 1, in region);
                }

                offset += mipSize;

                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);

                if (Info.Target == Target.Texture3D)
                {
                    depth = Math.Max(1, depth >> 1);
                }
            }
        }

        private int GetBufferDataLength(int length)
        {
            if (NeedsD24S8Conversion())
            {
                return length * 2;
            }

            return length;
        }

        private bool NeedsD24S8Conversion()
        {
            return FormatCapabilities.IsD24S8(Info.Format) && VkFormat == VkFormat.D32SfloatS8Uint;
        }

        internal void RecordViewFormatTelemetry(VkFormat viewFormat)
        {
            if (_turnipViewFormats != null && viewFormat != VkFormat && Array.IndexOf(_turnipViewFormats, viewFormat) < 0)
            {
                // NEOV2 Etapa 13: view com formato fora da lista declarada. Registra as primeiras ocorrencias.
                if (System.Threading.Interlocked.Increment(ref _turnipViewOutsideListLogs) <= 5)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[NEOV2-ETAPA13] View format outside image list: {VkFormat} -> {viewFormat}");
                }
            }

            if (!_telemetryTracked)
            {
                return;
            }

            int flag = TextureFormatTelemetry.Classify(VkFormat, viewFormat);

            if (flag != 0)
            {
                AddTelemetryFlags(flag);
            }
        }

        private void AddTelemetryFlags(int flag)
        {
            if (!_telemetryTracked)
            {
                return;
            }

            lock (_telemetryLock)
            {
                if (_telemetryDisposed)
                {
                    return;
                }

                int oldFlags = _telemetryFlags;
                int newFlags = oldFlags | flag;

                if (newFlags != oldFlags)
                {
                    _telemetryFlags = newFlags;
                    TextureFormatTelemetry.RecordFlagsChanged(_telemetryIsRt, (long)_size, oldFlags, newFlags);
                }
            }
        }

        private void MarkRenderTargetTelemetry()
        {
            lock (_telemetryLock)
            {
                if (_telemetryIsRt || _telemetryDisposed)
                {
                    return;
                }

                _telemetryIsRt = true;
                TextureFormatTelemetry.RecordBecameRenderTarget((long)_size, _telemetryFlags);
            }
        }

        public void AddStoreOpUsage(bool depthStencil)
        {
            if (_telemetryTracked && !_telemetryIsRt)
            {
                MarkRenderTargetTelemetry();
            }

            // TOTK-WRITE-GENERATION1
            System.Threading.Interlocked.Increment(ref _totkWriteGeneration);

            _lastModificationStage = GetTotkAttachmentWriteStage(depthStencil);

            _lastModificationAccess = depthStencil ?
                AccessFlags.DepthStencilAttachmentWriteBit :
                AccessFlags.ColorAttachmentWriteBit;
        }

        public void QueueLoadOpBarrier(CommandBufferScoped cbs, bool depthStencil)
        {
            PipelineStageFlags srcStageFlags = _lastReadStage | _lastModificationStage;
            PipelineStageFlags dstStageFlags = depthStencil ?
                PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit :
                PipelineStageFlags.ColorAttachmentOutputBit;

            AccessFlags srcAccessFlags = _lastModificationAccess | _lastReadAccess;
            AccessFlags dstAccessFlags = depthStencil ?
                AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit :
                AccessFlags.ColorAttachmentWriteBit | AccessFlags.ColorAttachmentReadBit;

            if (srcAccessFlags != AccessFlags.None)
            {
                ImageAspectFlags aspectFlags = Info.Format.ConvertAspectFlags();
                ImageMemoryBarrier barrier = TextureView.GetImageBarrier(
                    _imageAuto.Get(cbs).Value,
                    srcAccessFlags,
                    dstAccessFlags,
                    aspectFlags,
                    0,
                    0,
                    _info.GetLayers(),
                    _info.Levels);

                _gd.Barriers.QueueBarrier(barrier, this, srcStageFlags, dstStageFlags);

                _lastReadStage = PipelineStageFlags.None;
                _lastReadAccess = AccessFlags.None;
            }

            _lastModificationStage = GetTotkAttachmentWriteStage(depthStencil);

            _lastModificationAccess = depthStencil ?
                AccessFlags.DepthStencilAttachmentWriteBit :
                AccessFlags.ColorAttachmentWriteBit;
        }

        // TOTK-DEPTH-EARLYLATE17
        // Diagnostic A/B: depth/stencil attachment writes may execute in
        // EarlyFragmentTests or LateFragmentTests. Preserve the original
        // behavior unless explicitly enabled by the debug launcher.
        private static readonly bool _totkDepthEarlyLate17 =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_DEPTH_EARLY_LATE") == "1";

        private static PipelineStageFlags GetTotkAttachmentWriteStage(bool depthStencil)
        {
            if (!depthStencil)
            {
                return PipelineStageFlags.ColorAttachmentOutputBit;
            }

            return _totkDepthEarlyLate17
                ? PipelineStageFlags.EarlyFragmentTestsBit |
                  PipelineStageFlags.LateFragmentTestsBit
                : PipelineStageFlags.LateFragmentTestsBit;
        }

        // TOTK-C827-BARRIER-TRACE18
        private static readonly bool _totkC827BarrierTrace18 =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_C827_BARRIER_TRACE") == "1";

        private ulong _totkBarrierQueuedCount18;
        private AccessFlags _totkLastBarrierSrcAccess18;
        private AccessFlags _totkLastBarrierDstAccess18;
        private PipelineStageFlags _totkLastBarrierSrcStage18;
        private PipelineStageFlags _totkLastBarrierDstStage18;

        public string TotkGetBarrierDebugSignature18()
        {
            return
                $"MODA={_lastModificationAccess};" +
                $"MODS={_lastModificationStage};" +
                $"READA={_lastReadAccess};" +
                $"READS={_lastReadStage};" +
                $"LASTSA={_totkLastBarrierSrcAccess18};" +
                $"LASTSS={_totkLastBarrierSrcStage18};" +
                $"LASTDA={_totkLastBarrierDstAccess18};" +
                $"LASTDS={_totkLastBarrierDstStage18}";
        }

        public string TotkGetBarrierDebugState18()
        {
            return
                TotkGetBarrierDebugSignature18() +
                $";QUEUED={_totkBarrierQueuedCount18}";
        }

        public void QueueWriteToReadBarrier(CommandBufferScoped cbs, AccessFlags dstAccessFlags, PipelineStageFlags dstStageFlags)
        {
            _lastReadAccess |= dstAccessFlags;
            _lastReadStage |= dstStageFlags;

            if (_lastModificationAccess != AccessFlags.None)
            {
                if (_totkC827BarrierTrace18)
                {
                    _totkBarrierQueuedCount18++;
                    _totkLastBarrierSrcAccess18 = _lastModificationAccess;
                    _totkLastBarrierDstAccess18 = dstAccessFlags;
                    _totkLastBarrierSrcStage18 = _lastModificationStage;
                    _totkLastBarrierDstStage18 = dstStageFlags;
                }
                ImageAspectFlags aspectFlags = Info.Format.ConvertAspectFlags();
                ImageMemoryBarrier barrier = TextureView.GetImageBarrier(
                    _imageAuto.Get(cbs).Value,
                    _lastModificationAccess,
                    dstAccessFlags,
                    aspectFlags,
                    0,
                    0,
                    _info.GetLayers(),
                    _info.Levels);

                _gd.Barriers.QueueBarrier(barrier, this, _lastModificationStage, dstStageFlags);

                _lastModificationAccess = AccessFlags.None;
            }
        }

        public void AddBinding(TextureView view)
        {
            // Assumes a view only has a first level.

            int index = view.FirstLevel * _depthOrLayers + view.FirstLayer;
            int layers = view.Layers;

            for (int i = 0; i < layers; i++)
            {
                ref TextureSliceInfo info = ref _slices[index++];

                info.BindCount++;
            }

            _bindCount++;
        }

        public void ClearBindings()
        {
            if (_bindCount != 0)
            {
                Array.Clear(_slices, 0, _slices.Length);

                _bindCount = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsBound(TextureView view)
        {
            if (_bindCount != 0)
            {
                int index = view.FirstLevel * _depthOrLayers + view.FirstLayer;
                int layers = view.Layers;

                for (int i = 0; i < layers; i++)
                {
                    ref TextureSliceInfo info = ref _slices[index++];

                    if (info.BindCount != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public void IncrementViewsCount()
        {
            _viewsCount++;
        }

        public void DecrementViewsCount()
        {
            if (--_viewsCount == 0)
            {
                _gd.PipelineInternal?.FlushCommandsIfWeightExceeding(_imageAuto, _size);

                Dispose();
            }
        }

        public void Dispose()
        {
            Disposed = true;

            if (_telemetryTracked)
            {
                lock (_telemetryLock)
                {
                    if (!_telemetryDisposed)
                    {
                        _telemetryDisposed = true;
                        TextureFormatTelemetry.RecordStorageDisposed(_telemetryIsRt, (long)_size, _telemetryFlags);
                    }
                }
            }

            if (_aliasedStorages != null)
            {
                foreach (TextureStorage storage in _aliasedStorages.Values)
                {
                    storage.Dispose();
                }

                _aliasedStorages.Clear();
            }

            _imageAuto.Dispose();
            _allocationAuto?.Dispose();
            _foreignAllocationAuto?.DecrementReferenceCount();
            _foreignAllocationAuto = null;
        }
    }
}
