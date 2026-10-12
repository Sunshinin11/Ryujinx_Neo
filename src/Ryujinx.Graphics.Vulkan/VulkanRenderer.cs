using Ryujinx.Common.Diagnostics;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Ryujinx.Graphics.Vulkan.MoltenVK;
using Ryujinx.Graphics.Vulkan.Queries;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Format = Ryujinx.Graphics.GAL.Format;
using PrimitiveTopology = Ryujinx.Graphics.GAL.PrimitiveTopology;
using SamplerCreateInfo = Ryujinx.Graphics.GAL.SamplerCreateInfo;

namespace Ryujinx.Graphics.Vulkan
{
    public sealed class VulkanRenderer : IRenderer
    {
        private VulkanInstance _instance;
        private SurfaceKHR _surface;
        private VulkanPhysicalDevice _physicalDevice;
        private Device _device;
        private WindowBase _window;

        private bool _initialized;

        // JNI/Lifecycle Flag
        internal volatile bool PresentAllowed = true;

        public uint ProgramCount { get; set; } = 0;

        internal FormatCapabilities FormatCapabilities { get; private set; }
        internal HardwareCapabilities Capabilities;

        internal Vk Api { get; private set; }
        internal KhrSurface SurfaceApi { get; private set; }
        internal KhrSwapchain SwapchainApi { get; private set; }
        internal ExtConditionalRendering ConditionalRenderingApi { get; private set; }
        internal ExtExtendedDynamicState ExtendedDynamicStateApi { get; private set; }
        internal KhrPushDescriptor PushDescriptorApi { get; private set; }
        internal ExtTransformFeedback TransformFeedbackApi { get; private set; }
        internal KhrDrawIndirectCount DrawIndirectCountApi { get; private set; }
        internal ExtAttachmentFeedbackLoopDynamicState DynamicFeedbackLoopApi { get; private set; }

        internal uint QueueFamilyIndex { get; private set; }
        internal Queue Queue { get; private set; }
        internal Queue BackgroundQueue { get; private set; }
        internal Lock BackgroundQueueLock { get; private set; }
        internal Lock QueueLock { get; private set; }

        // SurfaceLock to serialize Create/Destroy/Queries
        internal Lock SurfaceLock { get; private set; }

        internal MemoryAllocator MemoryAllocator { get; private set; }
        internal HostMemoryAllocator HostMemoryAllocator { get; private set; }
        private readonly object _submissionGraphGate = new();
        internal VulkanSubmissionWorker QueueSubmissionWorker { get; private set; }
        internal VulkanSubmissionWorker BackgroundQueueSubmissionWorker { get; private set; }
        internal bool SupportsTimelineSemaphore { get; private set; }
        internal CommandBufferPool CommandBufferPool { get; private set; }
        internal PipelineLayoutCache PipelineLayoutCache { get; private set; }
        internal PipelineLibraryCache PipelineLibrary { get; private set; }
        internal BackgroundResources BackgroundResources { get; private set; }
        internal Action<Action> InterruptAction { get; private set; }
        internal SyncManager SyncManager { get; private set; }

        internal BufferManager BufferManager { get; private set; }

        internal HashSet<ShaderCollection> Shaders { get; }
        internal HashSet<ITexture> Textures { get; }
        internal HashSet<SamplerHolder> Samplers { get; }

        private VulkanDebugMessenger _debugMessenger;
        private Counters _counters;

        private PipelineFull _pipeline;

        internal HelperShader HelperShader { get; private set; }
        internal PipelineFull PipelineInternal => _pipeline;

        internal BarrierBatch Barriers { get; private set; }

        public IPipeline Pipeline => _pipeline;

        public IWindow Window => _window;

        public SurfaceTransformFlagsKHR CurrentTransform => _window.CurrentTransform;

        private readonly Func<Instance, Vk, SurfaceKHR> _getSurface;
        private readonly Func<string[]> _getRequiredExtensions;
        private readonly string _preferredGpuId;

        private int[] _pdReservedBindings;
        private readonly static int[] _pdReservedBindingsNvn = [3, 18, 21, 36, 30];
        private readonly static int[] _pdReservedBindingsOgl = [17, 18, 34, 35, 36];

        internal Vendor Vendor { get; private set; }
        internal bool IsAmdWindows { get; private set; }
        internal bool IsIntelWindows { get; private set; }
        internal bool IsAmdGcn { get; private set; }
        internal bool IsAmdRdna3 { get; private set; }
        internal bool IsAdreno6xx { get; private set; }
        internal bool IsAdreno7xx { get; private set; }
        internal bool IsAdreno8xx { get; private set; }
        internal bool IsFeedbackLoopDevice { get; private set; }
        internal bool IsNvidiaPreTuring { get; private set; }
        internal bool IsIntelArc { get; private set; }
        internal bool IsQualcommProprietary { get; private set; }
        internal bool IsTurnip { get; private set; }

        // NEOV2 CONDRENDER1: Eden skips waiting for conditional rendering values on these drivers.
        public bool PreferUnconditionalRenderingOverReadback => (IsTurnip || IsQualcommProprietary) && NeoGraphicsSettings.ConditionalRenderNoWaitEnabled;
        internal bool IsMoltenVk { get; private set; }
        internal bool IsTBDR { get; private set; }
        internal bool IsSharedMemory { get; private set; }

        // NEOV2-DYNAMIC-UBO1: Used to keep dynamic uniform layouts within the
        // limit advertised by the active Vulkan device.
        internal uint MaxDynamicUniformBuffers =>
            _physicalDevice.PhysicalDeviceProperties.Limits.MaxDescriptorSetUniformBuffersDynamic;

        public string GpuVendor { get; private set; }
        public string GpuDriver { get; private set; }
        public string GpuRenderer { get; private set; }
        public string GpuVersion { get; private set; }
        public string GpuDriverVersion { get; private set; }

        public bool PreferThreading => true;

        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

        public VulkanRenderer(Vk api, Func<Instance, Vk, SurfaceKHR> surfaceFunc, Func<string[]> requiredExtensionsFunc, string preferredGpuId)
        {
            _getSurface = surfaceFunc;
            _getRequiredExtensions = requiredExtensionsFunc;
            _preferredGpuId = preferredGpuId;
            Api = api;
            Shaders = [];
            Textures = [];
            Samplers = [];

            if (OperatingSystem.IsMacOS())
            {
                MVKInitialization.Initialize();

                // Any device running on MacOS is using MoltenVK, even Intel and AMD vendors.
                IsMoltenVk = true;
            }
        }

        private unsafe void LoadFeatures(uint maxQueueCount, uint queueFamilyIndex)
        {
            FormatCapabilities = new FormatCapabilities(Api, _physicalDevice.PhysicalDevice);

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtConditionalRendering conditionalRenderingApi))
            {
                ConditionalRenderingApi = conditionalRenderingApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExtendedDynamicState extendedDynamicStateApi))
            {
                ExtendedDynamicStateApi = extendedDynamicStateApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrPushDescriptor pushDescriptorApi))
            {
                PushDescriptorApi = pushDescriptorApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtTransformFeedback transformFeedbackApi))
            {
                TransformFeedbackApi = transformFeedbackApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrDrawIndirectCount drawIndirectCountApi))
            {
                DrawIndirectCountApi = drawIndirectCountApi;
            }

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtAttachmentFeedbackLoopDynamicState dynamicFeedbackLoopApi))
            {
                DynamicFeedbackLoopApi = dynamicFeedbackLoopApi;
            }

            if (maxQueueCount >= 2)
            {
                Api.GetDeviceQueue(_device, queueFamilyIndex, 1, out Queue backgroundQueue);
                BackgroundQueue = backgroundQueue;
                BackgroundQueueLock = new();
            }

            PhysicalDeviceProperties2 properties2 = new()
            {
                SType = StructureType.PhysicalDeviceProperties2,
            };

            PhysicalDeviceIDProperties propertiesId = new()
            {
                SType = StructureType.PhysicalDeviceIDProperties,
                PNext = properties2.PNext,
            };

            properties2.PNext = &propertiesId;

            PhysicalDeviceSubgroupProperties propertiesSubgroup = new()
            {
                SType = StructureType.PhysicalDeviceSubgroupProperties,
                PNext = properties2.PNext,
            };

            properties2.PNext = &propertiesSubgroup;

            PhysicalDeviceBlendOperationAdvancedPropertiesEXT propertiesBlendOperationAdvanced = new()
            {
                SType = StructureType.PhysicalDeviceBlendOperationAdvancedPropertiesExt,
            };

            bool supportsBlendOperationAdvanced = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_blend_operation_advanced");

            if (supportsBlendOperationAdvanced)
            {
                propertiesBlendOperationAdvanced.PNext = properties2.PNext;
                properties2.PNext = &propertiesBlendOperationAdvanced;
            }

            bool supportsTransformFeedback = _physicalDevice.IsDeviceExtensionPresent(ExtTransformFeedback.ExtensionName);

            PhysicalDeviceTransformFeedbackPropertiesEXT propertiesTransformFeedback = new()
            {
                SType = StructureType.PhysicalDeviceTransformFeedbackPropertiesExt,
            };

            if (supportsTransformFeedback)
            {
                propertiesTransformFeedback.PNext = properties2.PNext;
                properties2.PNext = &propertiesTransformFeedback;
            }

            PhysicalDevicePortabilitySubsetPropertiesKHR propertiesPortabilitySubset = new()
            {
                SType = StructureType.PhysicalDevicePortabilitySubsetPropertiesKhr,
            };

            bool supportsPushDescriptors = _physicalDevice.IsDeviceExtensionPresent(KhrPushDescriptor.ExtensionName);

            PhysicalDevicePushDescriptorPropertiesKHR propertiesPushDescriptor = new()
            {
                SType = StructureType.PhysicalDevicePushDescriptorPropertiesKhr
            };

            if (supportsPushDescriptors)
            {
                propertiesPushDescriptor.PNext = properties2.PNext;
                properties2.PNext = &propertiesPushDescriptor;
            }

            // NEOV2 TEXBUF-ALIGN1: drivers que expõem VK_EXT_texel_buffer_alignment com alinhamento
            // de um único texel (ex.: Turnip) aceitam o offset exato da view de texture buffer.
            // Nesses drivers a view não pode ser deslocada para frente, senão o shader lê o dado errado.
            bool supportsTexelBufferAlignment = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_texel_buffer_alignment");

            PhysicalDeviceTexelBufferAlignmentPropertiesEXT propertiesTexelBufferAlignment = new()
            {
                SType = StructureType.PhysicalDeviceTexelBufferAlignmentPropertiesExt,
            };

            if (supportsTexelBufferAlignment)
            {
                propertiesTexelBufferAlignment.PNext = properties2.PNext;
                properties2.PNext = &propertiesTexelBufferAlignment;
            }

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
            };

            PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT featuresPrimitiveTopologyListRestart = new()
            {
                SType = StructureType.PhysicalDevicePrimitiveTopologyListRestartFeaturesExt,
            };

            PhysicalDeviceRobustness2FeaturesEXT featuresRobustness2 = new()
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
            };

            PhysicalDeviceShaderFloat16Int8FeaturesKHR featuresShaderInt8 = new()
            {
                SType = StructureType.PhysicalDeviceShaderFloat16Int8Features,
            };

            PhysicalDeviceCustomBorderColorFeaturesEXT featuresCustomBorderColor = new()
            {
                SType = StructureType.PhysicalDeviceCustomBorderColorFeaturesExt,
            };

            PhysicalDeviceDepthClipControlFeaturesEXT featuresDepthClipControl = new()
            {
                SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt,
            };

            PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT featuresAttachmentFeedbackLoop = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesExt,
            };

            PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT featuresDynamicAttachmentFeedbackLoop = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesExt,
            };

            PhysicalDevicePortabilitySubsetFeaturesKHR featuresPortabilitySubset = new()
            {
                SType = StructureType.PhysicalDevicePortabilitySubsetFeaturesKhr,
            };

            if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_primitive_topology_list_restart"))
            {
                features2.PNext = &featuresPrimitiveTopologyListRestart;
            }

            if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_robustness2"))
            {
                featuresRobustness2.PNext = features2.PNext;
                features2.PNext = &featuresRobustness2;
            }

            if (_physicalDevice.IsDeviceExtensionPresent("VK_KHR_shader_float16_int8"))
            {
                featuresShaderInt8.PNext = features2.PNext;
                features2.PNext = &featuresShaderInt8;
            }

            if (_physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color"))
            {
                featuresCustomBorderColor.PNext = features2.PNext;
                features2.PNext = &featuresCustomBorderColor;
            }

            bool supportsDepthClipControl = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_depth_clip_control");

            if (supportsDepthClipControl)
            {
                featuresDepthClipControl.PNext = features2.PNext;
                features2.PNext = &featuresDepthClipControl;
            }

            bool supportsAttachmentFeedbackLoop = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_layout");

            if (supportsAttachmentFeedbackLoop)
            {
                featuresAttachmentFeedbackLoop.PNext = features2.PNext;
                features2.PNext = &featuresAttachmentFeedbackLoop;
            }

            bool supportsDynamicAttachmentFeedbackLoop = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_dynamic_state");

            if (supportsDynamicAttachmentFeedbackLoop)
            {
                featuresDynamicAttachmentFeedbackLoop.PNext = features2.PNext;
                features2.PNext = &featuresDynamicAttachmentFeedbackLoop;
            }

            bool usePortability = _physicalDevice.IsDeviceExtensionPresent("VK_KHR_portability_subset");

            if (usePortability)
            {
                propertiesPortabilitySubset.PNext = properties2.PNext;
                properties2.PNext = &propertiesPortabilitySubset;

                featuresPortabilitySubset.PNext = features2.PNext;
                features2.PNext = &featuresPortabilitySubset;
            }

            Api.GetPhysicalDeviceProperties2(_physicalDevice.PhysicalDevice, &properties2);
            Api.GetPhysicalDeviceFeatures2(_physicalDevice.PhysicalDevice, &features2);

            PortabilitySubsetFlags portabilityFlags = PortabilitySubsetFlags.None;
            uint vertexBufferAlignment = 1;

            if (usePortability)
            {
                vertexBufferAlignment = propertiesPortabilitySubset.MinVertexInputBindingStrideAlignment;

                portabilityFlags |= featuresPortabilitySubset.TriangleFans ? 0 : PortabilitySubsetFlags.NoTriangleFans;
                portabilityFlags |= featuresPortabilitySubset.PointPolygons ? 0 : PortabilitySubsetFlags.NoPointMode;
                portabilityFlags |= featuresPortabilitySubset.ImageView2DOn3DImage ? 0 : PortabilitySubsetFlags.No3DImageView;
                portabilityFlags |= featuresPortabilitySubset.SamplerMipLodBias ? 0 : PortabilitySubsetFlags.NoLodBias;
            }

            bool supportsCustomBorderColor = _physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color") &&
                                             featuresCustomBorderColor.CustomBorderColors &&
                                             featuresCustomBorderColor.CustomBorderColorWithoutFormat;

            ref PhysicalDeviceProperties properties = ref properties2.Properties;

            bool hasDriverProperties = _physicalDevice.TryGetPhysicalDeviceDriverPropertiesKHR(Api, out PhysicalDeviceDriverPropertiesKHR driverProperties);

            Vendor = VendorUtils.FromId(properties.VendorID);

            IsAmdWindows = Vendor == Vendor.Amd && OperatingSystem.IsWindows();
            IsIntelWindows = Vendor == Vendor.Intel && OperatingSystem.IsWindows();
            IsTBDR =
                Vendor is Vendor.Apple or Vendor.Qualcomm or Vendor.ARM or Vendor.Broadcom or Vendor.ImgTec;

            GpuVendor = VendorUtils.GetNameFromId(properties.VendorID);
            GpuDriver = hasDriverProperties && !OperatingSystem.IsMacOS() ?
                VendorUtils.GetFriendlyDriverName(driverProperties.DriverID) : GpuVendor; // Fallback to vendor name if driver is unavailable or on MacOS where vendor is preferred.
            GpuDriverVersion = TryGetIntelWindowsDriverVersionFromUuid(ref propertiesId, out string intelDriverVersion) ? intelDriverVersion :
                hasDriverProperties ?
                GetDriverInfo(ref driverProperties) ?? ParseDriverVersion(ref properties) :
                ParseDriverVersion(ref properties);

            fixed (byte* deviceName = properties.DeviceName)
            {
                GpuRenderer = Marshal.PtrToStringAnsi((nint)deviceName);
            }

            GpuVersion = $"Vulkan v{ParseStandardVulkanVersion(properties.ApiVersion)}";

            IsAmdGcn = !IsMoltenVk && Vendor == Vendor.Amd && VendorUtils.AmdGcnRegex().IsMatch(GpuRenderer);

            IsAmdRdna3 = Vendor == Vendor.Amd && (VendorUtils.AmdRdna3Regex().IsMatch(GpuRenderer)
                                                  // ROG Ally (X) Device IDs
                                                  || properties.DeviceID is 0x15BF or 0x15C8 or 0x150E);

            IsAdreno6xx = Vendor == Vendor.Qualcomm && VendorUtils.Adreno6xxRegex().IsMatch(GpuRenderer);
            IsAdreno7xx = Vendor == Vendor.Qualcomm && VendorUtils.Adreno7xxRegex().IsMatch(GpuRenderer);
            IsAdreno8xx = Vendor == Vendor.Qualcomm && VendorUtils.Adreno8xxRegex().IsMatch(GpuRenderer);

            IsFeedbackLoopDevice = IsAmdRdna3 || IsAdreno6xx || IsAdreno7xx;

            // NEOV2 Etapa 4B: forks do Turnip (ex.: PurpleVK) trocam o nome do dispositivo
            // ("PurpleVK-public Adreno (TM) 740"), entao tambem identificamos pelo DriverID do Mesa.
            IsTurnip = GpuRenderer.StartsWith("Turnip") ||
                       (hasDriverProperties && driverProperties.DriverID == DriverId.MesaTurnip);

            if (Vendor == Vendor.Nvidia)
            {
                Match match = VendorUtils.NvidiaConsumerClassRegex().Match(GpuRenderer);

                if (match != null && int.TryParse(match.Groups[2].Value, out int gpuNumber))
                {
                    IsNvidiaPreTuring = gpuNumber < 2000;
                }
                else if (GpuRenderer.Contains("TITAN") && !GpuRenderer.Contains("RTX"))
                {
                    IsNvidiaPreTuring = true;
                }
            }
            else if (Vendor == Vendor.Intel)
            {
                IsIntelArc = GpuRenderer.StartsWith("Intel(R) Arc(TM)");
            }

            IsQualcommProprietary = hasDriverProperties && driverProperties.DriverID == DriverId.QualcommProprietary;

            ulong minResourceAlignment = Math.Max(
                Math.Max(
                    properties.Limits.MinStorageBufferOffsetAlignment,
                    properties.Limits.MinUniformBufferOffsetAlignment),
                properties.Limits.MinTexelBufferOffsetAlignment
            );

            bool texelBufferSingleTexelAlignment = supportsTexelBufferAlignment &&
                propertiesTexelBufferAlignment.UniformTexelBufferOffsetSingleTexelAlignment &&
                propertiesTexelBufferAlignment.StorageTexelBufferOffsetSingleTexelAlignment &&
                NeoGraphicsSettings.TexelBufferExactOffsetEnabled;

            ulong minTexelBufferViewOffsetAlignment = texelBufferSingleTexelAlignment
                ? 1UL
                : properties.Limits.MinTexelBufferOffsetAlignment;

            SampleCountFlags supportedSampleCounts =
                properties.Limits.FramebufferColorSampleCounts &
                properties.Limits.FramebufferDepthSampleCounts &
                properties.Limits.FramebufferStencilSampleCounts;

            Capabilities = new HardwareCapabilities(
                _physicalDevice.IsDeviceExtensionPresent("VK_EXT_index_type_uint8") && !IsAdreno8xx,
                supportsCustomBorderColor && !IsAdreno8xx,
                supportsBlendOperationAdvanced,
                propertiesBlendOperationAdvanced.AdvancedBlendCorrelatedOverlap,
                propertiesBlendOperationAdvanced.AdvancedBlendNonPremultipliedSrcColor,
                propertiesBlendOperationAdvanced.AdvancedBlendNonPremultipliedDstColor,
                _physicalDevice.IsDeviceExtensionPresent(KhrDrawIndirectCount.ExtensionName),
                _physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_shader_interlock") && !IsAdreno8xx,
                _physicalDevice.IsDeviceExtensionPresent("VK_NV_geometry_shader_passthrough") && !IsAdreno8xx,
                features2.Features.ShaderFloat64,
                featuresShaderInt8.ShaderInt8 && !IsAdreno8xx,
                _physicalDevice.IsDeviceExtensionPresent("VK_EXT_shader_stencil_export") && !IsAdreno8xx,
                features2.Features.ShaderStorageImageMultisample,
                _physicalDevice.IsDeviceExtensionPresent(ExtConditionalRendering.ExtensionName),
                _physicalDevice.IsDeviceExtensionPresent(ExtExtendedDynamicState.ExtensionName) && !IsAdreno8xx,
                features2.Features.MultiViewport && !(IsMoltenVk && Vendor == Vendor.Amd), // Workaround for AMD on MoltenVK issue
                (featuresRobustness2.NullDescriptor || IsMoltenVk) && !IsAdreno8xx,
                supportsPushDescriptors && !IsMoltenVk,
                propertiesPushDescriptor.MaxPushDescriptors,
                featuresPrimitiveTopologyListRestart.PrimitiveTopologyListRestart && !IsAdreno8xx,
                featuresPrimitiveTopologyListRestart.PrimitiveTopologyPatchListRestart && !IsAdreno8xx,
                supportsTransformFeedback && !IsAdreno8xx,
                propertiesTransformFeedback.TransformFeedbackQueries,
                features2.Features.OcclusionQueryPrecise,
                _physicalDevice.PhysicalDeviceFeatures.PipelineStatisticsQuery,
                _physicalDevice.PhysicalDeviceFeatures.GeometryShader && !IsAdreno8xx,
                _physicalDevice.PhysicalDeviceFeatures.TessellationShader && !IsAdreno8xx,
                _physicalDevice.IsDeviceExtensionPresent("VK_NV_viewport_array2"),
                _physicalDevice.IsDeviceExtensionPresent(ExtExternalMemoryHost.ExtensionName) && !IsAdreno8xx,
                supportsDepthClipControl && featuresDepthClipControl.DepthClipControl,
                supportsAttachmentFeedbackLoop && featuresAttachmentFeedbackLoop.AttachmentFeedbackLoopLayout && IsFeedbackLoopDevice,
                supportsDynamicAttachmentFeedbackLoop && featuresDynamicAttachmentFeedbackLoop.AttachmentFeedbackLoopDynamicState && IsFeedbackLoopDevice,
                propertiesSubgroup.SubgroupSize,
                supportedSampleCounts,
                portabilityFlags,
                vertexBufferAlignment,
                properties.Limits.SubTexelPrecisionBits,
                minResourceAlignment,
                minTexelBufferViewOffsetAlignment);

            IsSharedMemory = MemoryAllocator.IsDeviceMemoryShared(_physicalDevice);

            MemoryAllocator = new MemoryAllocator(Api, _physicalDevice, _device);

            Api.TryGetDeviceExtension(_instance.Instance, _device, out ExtExternalMemoryHost hostMemoryApi);
            HostMemoryAllocator = new HostMemoryAllocator(MemoryAllocator, Api, hostMemoryApi, _device);

            // NEOV2 TURNIP-FENCE1: no Turnip (kgsl) a timeline e emulada pelo Mesa. Cada consulta ou espera
            // faz uma chamada ao kernel segurando uma trava unica da timeline, que serializa backend, submit
            // e GPU thread. O Eden desliga timeline no Turnip (Device::HasTimelineSemaphore) e usa VkFence.
            bool useTimelineSemaphore = SupportsTimelineSemaphore && !(IsTurnip && NeoGraphicsSettings.TurnipFenceEnabled);

            QueueSubmissionWorker = new VulkanSubmissionWorker(Api, _device, Queue, QueueLock, _submissionGraphGate, useTimelineSemaphore, "Kenji Vulkan Main Submit");
            BackgroundQueueSubmissionWorker = BackgroundQueue.Handle != 0
                ? new VulkanSubmissionWorker(Api, _device, BackgroundQueue, BackgroundQueueLock, _submissionGraphGate, useTimelineSemaphore, "Kenji Vulkan Background Submit")
                : QueueSubmissionWorker;

            Logger.Info?.Print(LogClass.Gpu, $"[NEOV2-ETAPA2] Vulkan timeline semaphore completion: {(useTimelineSemaphore ? "Enabled" : (SupportsTimelineSemaphore ? "Fence fallback (Turnip, TURNIP-FENCE1)" : "Fence fallback"))}");
            Logger.Info?.Print(LogClass.Gpu, $"[NEOV2-ETAPA4B] Turnip image view format lists: {(IsTurnip ? "Enabled" : "Disabled")}");
            Logger.Info?.Print(LogClass.Gpu, $"[NEOV2-TEXBUF-ALIGN1] Texel buffer view offset: {(texelBufferSingleTexelAlignment ? "exact (single texel alignment)" : $"aligned up to {minTexelBufferViewOffsetAlignment} bytes")}");
                        // NEOV2 AUTOFLUSH1-FIX1: o teto por frame depende do modo efetivo do seletor, entao a linha
            // nao pode mais imprimir 4 fixo.
            string drawFlushCap = NeoGraphicsSettings.DrawFlushMode switch
            {
                DrawFlushMode.Reduced => "1",
                DrawFlushMode.Disabled => "0",
                _ => IsTurnip ? "4" : "unbounded",
            };

            Logger.Info?.Print(LogClass.Gpu, $"[NEOV2-FLUSH-R3] Turnip adaptive fast-flush guard: {(IsTurnip ? "Enabled" : "Not applicable")} drawFlushMode={NeoGraphicsSettings.DrawFlushMode} maxDrawFlushesPerFrame={drawFlushCap} exitThresholdMs=0.75");
            GezinjinxGuestStallTelemetry.Enable();

            CommandBufferPool = new CommandBufferPool(Api, _device, QueueSubmissionWorker, queueFamilyIndex, IsQualcommProprietary, IsTurnip);

            PipelineLayoutCache = new PipelineLayoutCache();
            PipelineLibrary = PipelineLibraryCache.TryCreate(this, _device, _physicalDevice);

            BackgroundResources = new BackgroundResources(this, _device);

            BufferManager = new BufferManager(this, _device);

            SyncManager = new SyncManager(this, _device);
            _pipeline = new PipelineFull(this, _device);
            _pipeline.Initialize();

            HelperShader = new HelperShader(this, _device);

            Barriers = new BarrierBatch(this);

            _counters = new Counters(this, _device, _pipeline);
        }

        private void SetupContext(GraphicsDebugLevel logLevel)
        {
            _instance = VulkanInitialization.CreateInstance(Api, logLevel, _getRequiredExtensions());
            _debugMessenger = new VulkanDebugMessenger(Api, _instance.Instance, logLevel);

            if (Api.TryGetInstanceExtension(_instance.Instance, out KhrSurface surfaceApi))
            {
                SurfaceApi = surfaceApi;
            }

            _surface = _getSurface(_instance.Instance, Api);
            _physicalDevice = VulkanInitialization.FindSuitablePhysicalDevice(Api, _instance, _surface, _preferredGpuId);

            uint queueFamilyIndex = VulkanInitialization.FindSuitableQueueFamily(Api, _physicalDevice, _surface, out uint maxQueueCount);

            _device = VulkanInitialization.CreateDevice(Api, _physicalDevice, queueFamilyIndex, maxQueueCount, out bool timelineSemaphoreEnabled);
            SupportsTimelineSemaphore = timelineSemaphoreEnabled;

            if (Api.TryGetDeviceExtension(_instance.Instance, _device, out KhrSwapchain swapchainApi))
            {
                SwapchainApi = swapchainApi;
            }

            Api.GetDeviceQueue(_device, queueFamilyIndex, 0, out Queue queue);
            Queue = queue;
            QueueLock = new();

            // Init Locks
            SurfaceLock = new();
            if (maxQueueCount >= 2)
            {
                Api.GetDeviceQueue(_device, queueFamilyIndex, 1, out Queue backgroundQueue);
                BackgroundQueue = backgroundQueue;
                BackgroundQueueLock = new();
            }

            LoadFeatures(maxQueueCount, queueFamilyIndex);

            QueueFamilyIndex = queueFamilyIndex;

            _window = new Window(this, _surface, _physicalDevice.PhysicalDevice, _device);

            _initialized = true;
        }

        internal int[] GetPushDescriptorReservedBindings(bool isOgl)
        {
            // The first call of this method determines what push descriptor layout is used for all shaders on this renderer.
            // This is chosen to minimize shaders that can't fit their uniforms on the device's max number of push descriptors.
            if (_pdReservedBindings == null)
            {
                if (Capabilities.MaxPushDescriptors <= Constants.MaxUniformBuffersPerStage * 2)
                {
                    _pdReservedBindings = isOgl ? _pdReservedBindingsOgl : _pdReservedBindingsNvn;
                }
                else
                {
                    _pdReservedBindings = [];
                }
            }

            return _pdReservedBindings;
        }

        public BufferHandle CreateBuffer(int size, BufferAccess access)
        {
            return BufferManager.CreateWithHandle(this, size, access.HasFlag(BufferAccess.SparseCompatible), access.Convert(), access.HasFlag(BufferAccess.Stream));
        }

        public BufferHandle CreateBuffer(nint pointer, int size)
        {
            return BufferManager.CreateHostImported(this, pointer, size);
        }

        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            return BufferManager.CreateSparse(this, storageBuffers);
        }

        public IImageArray CreateImageArray(int size, bool isBuffer)
        {
            return new ImageArray(this, size, isBuffer);
        }

        public IProgram CreateProgram(ShaderSource[] sources, ShaderInfo info)
        {
            ProgramCount++;

            bool isCompute = sources.Length == 1 && sources[0].Stage == ShaderStage.Compute;

            if (info.State.HasValue || isCompute)
            {
                return new ShaderCollection(this, _device, sources, info.ResourceLayout, info.State ?? default, info.FromCache);
            }

            return new ShaderCollection(this, _device, sources, info.ResourceLayout);
        }

        internal ShaderCollection CreateProgramWithMinimalLayout(ShaderSource[] sources, ResourceLayout resourceLayout, SpecDescription[] specDescription = null)
        {
            return new ShaderCollection(this, _device, sources, resourceLayout, specDescription, isMinimal: true);
        }

        public ISampler CreateSampler(SamplerCreateInfo info)
        {
            return new SamplerHolder(this, _device, info);
        }

        public ITexture CreateTexture(TextureCreateInfo info)
        {
            if (info.Target == Target.TextureBuffer)
            {
                return new TextureBuffer(this, info);
            }

            return CreateTextureView(info);
        }

        public ITextureArray CreateTextureArray(int size, bool isBuffer)
        {
            return new TextureArray(this, size, isBuffer);
        }

        internal TextureView CreateTextureView(TextureCreateInfo info)
        {
            // This should be disposed when all views are destroyed.
            TextureStorage storage = CreateTextureStorage(info);
            return storage.CreateView(info, 0, 0);
        }

        internal TextureStorage CreateTextureStorage(TextureCreateInfo info)
        {
            return new TextureStorage(this, _device, info);
        }

        public void DeleteBuffer(BufferHandle buffer)
        {
            BufferManager.Delete(buffer);
        }

        internal void FlushAllCommands([CallerFilePath] string callerFile = "", [CallerMemberName] string callerMember = "")
        {
            // NEOV2 Etapa 16: registra a origem de cada flush forcado (arquivo.metodo) para o contador.
            AutoFlushCounter.RecordExternalFlush(callerFile, callerMember);

            _pipeline?.FlushCommandsImpl();
        }

        internal void RegisterFlush()
        {
            SyncManager.RegisterFlush();

            // Periodically free unused regions of the staging buffer to avoid doing it all at once.
            BufferManager.StagingBuffer.FreeCompleted();
        }

        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            return BufferManager.GetData(buffer, offset, size);
        }

        public unsafe Capabilities GetCapabilities()
        {
            FormatFeatureFlags compressedFormatFeatureFlags =
                FormatFeatureFlags.SampledImageBit |
                FormatFeatureFlags.SampledImageFilterLinearBit |
                FormatFeatureFlags.BlitSrcBit |
                FormatFeatureFlags.TransferSrcBit |
                FormatFeatureFlags.TransferDstBit;

            bool supportsBc123CompressionFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Bc1RgbaSrgb,
                Format.Bc1RgbaUnorm,
                Format.Bc2Srgb,
                Format.Bc2Unorm,
                Format.Bc3Srgb,
                Format.Bc3Unorm);

            bool supportsBc45CompressionFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Bc4Snorm,
                Format.Bc4Unorm,
                Format.Bc5Snorm,
                Format.Bc5Unorm);

            bool supportsBc67CompressionFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Bc6HSfloat,
                Format.Bc6HUfloat,
                Format.Bc7Srgb,
                Format.Bc7Unorm);

            bool supportsEtc2CompressionFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Etc2RgbaSrgb,
                Format.Etc2RgbaUnorm,
                Format.Etc2RgbPtaSrgb,
                Format.Etc2RgbPtaUnorm,
                Format.Etc2RgbSrgb,
                Format.Etc2RgbUnorm);

            bool supports5BitComponentFormat = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.R5G6B5Unorm,
                Format.R5G5B5A1Unorm,
                Format.R5G5B5X1Unorm,
                Format.B5G6R5Unorm,
                Format.B5G5R5A1Unorm,
                Format.A1B5G5R5Unorm);

            bool supportsR4G4B4A4Format = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.R4G4B4A4Unorm);

            bool supportsAstcFormats = FormatCapabilities.OptimalFormatsSupport(compressedFormatFeatureFlags,
                Format.Astc4x4Unorm,
                Format.Astc5x4Unorm,
                Format.Astc5x5Unorm,
                Format.Astc6x5Unorm,
                Format.Astc6x6Unorm,
                Format.Astc8x5Unorm,
                Format.Astc8x6Unorm,
                Format.Astc8x8Unorm,
                Format.Astc10x5Unorm,
                Format.Astc10x6Unorm,
                Format.Astc10x8Unorm,
                Format.Astc10x10Unorm,
                Format.Astc12x10Unorm,
                Format.Astc12x12Unorm,
                Format.Astc4x4Srgb,
                Format.Astc5x4Srgb,
                Format.Astc5x5Srgb,
                Format.Astc6x5Srgb,
                Format.Astc6x6Srgb,
                Format.Astc8x5Srgb,
                Format.Astc8x6Srgb,
                Format.Astc8x8Srgb,
                Format.Astc10x5Srgb,
                Format.Astc10x6Srgb,
                Format.Astc10x8Srgb,
                Format.Astc10x10Srgb,
                Format.Astc12x10Srgb,
                Format.Astc12x12Srgb);

            PhysicalDeviceVulkan12Features featuresVk12 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
            };

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &featuresVk12,
            };

            Api.GetPhysicalDeviceFeatures2(_physicalDevice.PhysicalDevice, &features2);

            PhysicalDeviceLimits limits = _physicalDevice.PhysicalDeviceProperties.Limits;
            QueueFamilyProperties mainQueueProperties = _physicalDevice.QueueFamilyProperties[QueueFamilyIndex];

            SystemMemoryType memoryType;

            if (IsSharedMemory)
            {
                memoryType = SystemMemoryType.UnifiedMemory;
            }
            else
            {
                memoryType = Vendor == Vendor.Nvidia ?
                    SystemMemoryType.DedicatedMemorySlowStorage :
                    SystemMemoryType.DedicatedMemory;
            }

            return new Capabilities(
                api: TargetApi.Vulkan,
                GpuVendor,
                memoryType: memoryType,
                hasFrontFacingBug: IsIntelWindows,
                hasVectorIndexingBug: IsQualcommProprietary,
                needsFragmentOutputSpecialization: IsMoltenVk,
                reduceShaderPrecision: IsMoltenVk,
                supportsAstcCompression: features2.Features.TextureCompressionAstcLdr && supportsAstcFormats,
                supportsBc123Compression: supportsBc123CompressionFormat,
                supportsBc45Compression: supportsBc45CompressionFormat,
                supportsBc67Compression: supportsBc67CompressionFormat,
                supportsEtc2Compression: supportsEtc2CompressionFormat,
                supports3DTextureCompression: true,
                supportsBgraFormat: true,
                supportsR4G4Format: false,
                supportsR4G4B4A4Format: supportsR4G4B4A4Format,
                supportsScaledVertexFormats: FormatCapabilities.SupportsScaledVertexFormats(),
                supportsSnormBufferTextureFormat: true,
                supports5BitComponentFormat: supports5BitComponentFormat,
                supportsSparseBuffer: features2.Features.SparseBinding && mainQueueProperties.QueueFlags.HasFlag(QueueFlags.SparseBindingBit),
                supportsBlendEquationAdvanced: Capabilities.SupportsBlendEquationAdvanced,
                supportsFragmentShaderInterlock: Capabilities.SupportsFragmentShaderInterlock && !IsAdreno8xx,
                supportsFragmentShaderOrderingIntel: false,
                supportsGeometryShader: Capabilities.SupportsGeometryShader && !IsAdreno8xx,
                supportsGeometryShaderPassthrough: Capabilities.SupportsGeometryShaderPassthrough && !IsAdreno8xx,
                supportsTransformFeedback: Capabilities.SupportsTransformFeedback && !IsAdreno8xx,
                supportsImageLoadFormatted: features2.Features.ShaderStorageImageReadWithoutFormat,
                supportsLayerVertexTessellation: featuresVk12.ShaderOutputLayer && !IsAdreno8xx,
                // NEOV2 Etapa 4B: no Turnip as imagens recebem lista fechada de formatos de view,
                // entao views com formato diferente viram copia na camada GPU.
                supportsMismatchingViewFormat: !IsTurnip,
                supportsCubemapView: !IsAmdGcn,
                supportsNonConstantTextureOffset: false,
                supportsQuads: false,
                supportsSeparateSampler: true,
                supportsShaderBallot: false,
                supportsShaderBarrierDivergence: Vendor != Vendor.Intel,
                supportsShaderFloat64: Capabilities.SupportsShaderFloat64,
                supportsShaderNonUniformIndexing:
                    featuresVk12.ShaderSampledImageArrayNonUniformIndexing &&
                    featuresVk12.ShaderStorageImageArrayNonUniformIndexing,
                supportsTextureGatherOffsets: features2.Features.ShaderImageGatherExtended && !IsMoltenVk,
                supportsTextureShadowLod: false,
                supportsVertexStoreAndAtomics: features2.Features.VertexPipelineStoresAndAtomics,
                supportsViewportIndexVertexTessellation: featuresVk12.ShaderOutputViewportIndex && !IsAdreno8xx,
                supportsViewportMask: Capabilities.SupportsViewportArray2,
                supportsViewportSwizzle: false,
                supportsIndirectParameters: true,
                supportsDepthClipControl: Capabilities.SupportsDepthClipControl,
                uniformBufferSetIndex: PipelineBase.UniformSetIndex,
                storageBufferSetIndex: PipelineBase.StorageSetIndex,
                textureSetIndex: PipelineBase.TextureSetIndex,
                imageSetIndex: PipelineBase.ImageSetIndex,
                extraSetBaseIndex: PipelineBase.DescriptorSetLayouts,
                maximumExtraSets: Math.Max(0, (int)limits.MaxBoundDescriptorSets - PipelineBase.DescriptorSetLayouts),
                maximumUniformBuffersPerStage: Constants.MaxUniformBuffersPerStage,
                maximumStorageBuffersPerStage: Constants.MaxStorageBuffersPerStage,
                maximumTexturesPerStage: Constants.MaxTexturesPerStage,
                maximumImagesPerStage: Constants.MaxImagesPerStage,
                maximumComputeSharedMemorySize: (int)limits.MaxComputeSharedMemorySize,
                maximumSupportedAnisotropy: (int)limits.MaxSamplerAnisotropy,
                shaderSubgroupSize: (int)Capabilities.SubgroupSize,
                storageBufferOffsetAlignment: (int)limits.MinStorageBufferOffsetAlignment,
                textureBufferOffsetAlignment: (int)limits.MinTexelBufferOffsetAlignment,
                // Adreno (Turnip) picks a different 2x2 textureGather footprint than NVIDIA when the
                // coordinate lands on a texel center, like AMD/Intel. In TOTK this shifts the quarter-res
                // depth gathers of the sun shadow mask and produces dark 64x64 tiles on sloped terrain.
                // See docs/TOTK-turnip-gather-bias.md. RYUJINX_TOTK_TURNIP_GATHER_BIAS=0 disables it (A/B).
                gatherBiasPrecision:
                    IsIntelWindows ||
                    IsAmdWindows ||
                    (IsTurnip && Environment.GetEnvironmentVariable("RYUJINX_TOTK_TURNIP_GATHER_BIAS") != "0")
                        ? (int)Capabilities.SubTexelPrecisionBits
                        : 0,
                maximumGpuMemory: GetTotalGPUMemory());
        }

        private ulong GetTotalGPUMemory()
        {
            ulong totalMemory = 0;

            Api.GetPhysicalDeviceMemoryProperties(_physicalDevice.PhysicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);

            for (int i = 0; i < memoryProperties.MemoryHeapCount; i++)
            {
                var heap = memoryProperties.MemoryHeaps[i];
                if ((heap.Flags & MemoryHeapFlags.DeviceLocalBit) == MemoryHeapFlags.DeviceLocalBit)
                {
                    totalMemory += heap.Size;
                }
            }

            return totalMemory;
        }

        public HardwareInfo GetHardwareInfo()
        {
            return new HardwareInfo(GpuVendor, GpuRenderer, GpuDriver, GpuDriverVersion);
        }

        /// <summary>
        /// Gets the available Vulkan devices using the default Vulkan API
        /// object returned by <see cref="Vk.GetApi()"/>
        /// </summary>
        /// <returns></returns>
        public static DeviceInfo[] GetPhysicalDevices()
        {
            try
            {
                return VulkanInitialization.GetSuitablePhysicalDevices(Vk.GetApi());
            }
            catch (Exception ex)
            {
                Logger.Error?.PrintMsg(LogClass.Gpu, $"Error querying Vulkan devices: {ex.Message}");

                return [];
            }
        }

        public static DeviceInfo[] GetPhysicalDevices(Vk api)
        {
            try
            {
                return VulkanInitialization.GetSuitablePhysicalDevices(api);
            }
            catch (Exception)
            {
                // If we got an exception here, Vulkan is most likely not supported.
                return [];
            }
        }

        private static string ParseStandardVulkanVersion(uint version)
        {
            return $"{version >> 22}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";
        }

        private static string ParseDriverVersion(ref PhysicalDeviceProperties properties)
        {
            uint driverVersionRaw = properties.DriverVersion;

            // NVIDIA differ from the standard here and uses a different format.
            if (properties.VendorID == 0x10DE)
            {
                return $"{(driverVersionRaw >> 22) & 0x3FF}.{(driverVersionRaw >> 14) & 0xFF}.{(driverVersionRaw >> 6) & 0xFF}.{driverVersionRaw & 0x3F}";
            }

            // Intel's Windows Vulkan driver exposes the 101.xxxx build portion in a custom layout.
            if (properties.VendorID == 0x8086 && OperatingSystem.IsWindows())
            {
                return $"{driverVersionRaw >> 14}.{driverVersionRaw & 0x3FFF}";
            }

            return ParseStandardVulkanVersion(driverVersionRaw);
        }

        private static unsafe string GetDriverInfo(ref PhysicalDeviceDriverPropertiesKHR driverProperties)
        {
            fixed (byte* driverInfo = driverProperties.DriverInfo)
            {
                string driverInfoString = Marshal.PtrToStringAnsi((nint)driverInfo);

                return string.IsNullOrWhiteSpace(driverInfoString) ? null : driverInfoString;
            }
        }

        private static unsafe bool TryGetIntelWindowsDriverVersionFromUuid(ref PhysicalDeviceIDProperties propertiesId, out string driverVersion)
        {
            driverVersion = null;

            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            fixed (byte* driverUuid = propertiesId.DriverUuid)
            {
                string driverUuidString = Marshal.PtrToStringAnsi((nint)driverUuid, (int)Vk.UuidSize);
                int terminatorIndex = driverUuidString.IndexOf('\0');

                if (terminatorIndex >= 0)
                {
                    driverUuidString = driverUuidString[..terminatorIndex];
                }

                if (VendorUtils.IntelWindowsDriverVersionRegex().IsMatch(driverUuidString))
                {
                    driverVersion = driverUuidString;

                    return true;
                }
            }

            return false;
        }

        internal PrimitiveTopology TopologyRemap(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Quads => PrimitiveTopology.Triangles,
                PrimitiveTopology.QuadStrip => PrimitiveTopology.TriangleStrip,
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => Capabilities.PortabilitySubset.HasFlag(PortabilitySubsetFlags.NoTriangleFans)
                    ? PrimitiveTopology.Triangles
                    : topology,
                _ => topology,
            };
        }

        internal bool TopologyUnsupported(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Quads => true,
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => Capabilities.PortabilitySubset.HasFlag(PortabilitySubsetFlags.NoTriangleFans),
                _ => false,
            };
        }

        private void PrintGpuInformation()
        {
            Logger.Notice.Print(LogClass.Gpu, $"{GpuVendor} {GpuRenderer} ({GpuVersion}, Driver: {GpuDriver} {GpuDriverVersion})");
            Logger.Notice.Print(LogClass.Gpu, $"GPU Memory: {GetTotalGPUMemory() / (1024 * 1024)} MiB");
        }

        public void Initialize(GraphicsDebugLevel logLevel)
        {
            SetupContext(logLevel);

            PrintGpuInformation();
        }

        internal bool NeedsVertexBufferAlignment(int attrScalarAlignment, out int alignment)
        {
            if (Capabilities.VertexBufferAlignment > 1)
            {
                alignment = (int)Capabilities.VertexBufferAlignment;

                return true;
            }
            else if (Vendor != Vendor.Nvidia)
            {
                // Vulkan requires that vertex attributes are globally aligned by their component size,
                // so buffer strides that don't divide by the largest scalar element are invalid.
                // Guest applications do this, NVIDIA GPUs are OK with it, others are not.

                alignment = attrScalarAlignment;

                return true;
            }

            alignment = 1;

            return false;
        }

        public void PreFrame()
        {
            SyncManager.Cleanup();
        }

        public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            return _counters.QueueReport(type, resultHandler, divisor, hostReserved);
        }

        public void ResetCounter(CounterType type)
        {
            _counters.QueueReset(type);
        }

        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            BufferManager.SetData(buffer, offset, data, _pipeline.CurrentCommandBuffer, _pipeline.EndRenderPassDelegate);
        }

        public void UpdateCounters()
        {
            _counters.Update();
        }

        public void ResetCounterPool()
        {
            _counters.ResetCounterPool();
        }

        public void ResetFutureCounters(CommandBuffer cmd, int count)
        {
            _counters?.ResetFutureCounters(cmd, count);
        }

        public void BackgroundContextAction(Action action, bool alwaysBackground = false)
        {
            action();
        }

        public void CreateSync(ulong id, bool strict)
        {
            SyncManager.Create(id, strict);
        }

        public IProgram LoadProgramBinary(byte[] programBinary, bool isFragment, ShaderInfo info)
        {
            throw new NotImplementedException();
        }

        public void WaitSync(ulong id)
        {
            SyncManager.Wait(id);
        }

        public ulong GetCurrentSync()
        {
            return SyncManager.GetCurrent();
        }

        public void SetInterruptAction(Action<Action> interruptAction)
        {
            InterruptAction = interruptAction;
        }

        public void Screenshot()
        {
            _window.ScreenCaptureRequested = true;
        }

        public void OnScreenCaptured(ScreenCaptureImageInfo bitmap)
        {
            ScreenCaptured?.Invoke(this, bitmap);
        }

        public bool SupportsRenderPassBarrier(PipelineStageFlags flags)
        {
            return !(IsMoltenVk || IsQualcommProprietary);
        }

        // ===== Surface/Present Lifecycle helpers =====

        public unsafe bool RecreateSurface()
        {
            if (!PresentAllowed)
            {
                return false;
            }

            lock (SurfaceLock)
            {
                try
                {
                    if (_surface.Handle != 0)
                    {
                        SurfaceApi.DestroySurface(_instance.Instance, _surface, null);
                        _surface = new SurfaceKHR(0);
                    }

                    _surface = _getSurface(_instance.Instance, Api);
                    if (_surface.Handle == 0)
                    {
                        return false;
                    }

                    ( _window as Window )?.SetSurface(_surface);
                    ( _window as Window )?.SetSurfaceQueryAllowed(true);
                    return true;
                }
                catch
                {
                    // retry später
                    return false;
                }
            }
        }

        public unsafe void ReleaseSurface()
        {
            lock (SurfaceLock)
            {
                if (!_initialized)
                {
                    return;
                }

                try
                {
                    ( _window as Window )?.SetSurfaceQueryAllowed(false);

                    if (_surface.Handle != 0 && SurfaceApi != null && _instance != null)
                    {
                        SurfaceApi.DestroySurface(_instance.Instance, _surface, null);
                        _surface = new SurfaceKHR(0);
                    }
                }
                catch
                {
                    // still
                }

                try
                {
                    ( _window as Window )?.OnSurfaceLost();
                }
                catch
                {
                    // ignore shutdown path exceptions
                }
            }
        }

        public void SetPresentEnabled(bool enabled)
        {
            PresentAllowed = enabled;

            if (!_initialized)
            {
                return;
            }

            if (!enabled)
            {
                try
                {
                    ( _window as Window )?.SetSurfaceQueryAllowed(false);
                    ReleaseSurface();
                }
                catch
                {
                    // ignore shutdown path exceptions
                }
            }
            else
            {
                try
                {
                    _ = RecreateSurface();
                }
                catch
                {
                    // ignore resume path exceptions
                }
            }
        }

        public void SetPresentAllowed(bool allowed)
        {
            PresentAllowed = allowed;
            Logger.Trace?.Print(LogClass.Gpu, $"PresentAllowed={allowed}");

            if (allowed)
            {
                try
                {
                    ( _window as Window )?.SetSurfaceQueryAllowed(true);
                    _window?.SetSize(0, 0);
                    _ = RecreateSurface();
                }
                catch { }
            }
            else
            {
                ( _window as Window )?.SetSurfaceQueryAllowed(false);
            }
        }

        public unsafe void Dispose()
        {
            if (!_initialized)
            {
                return;
            }

            PipelineLibrary?.StopWorkers();
            CommandBufferPool.Dispose();
            BackgroundResources.Dispose();
            _counters.Dispose();
            _window.Dispose();
            QueueSubmissionWorker?.Dispose();
            if (!ReferenceEquals(BackgroundQueueSubmissionWorker, QueueSubmissionWorker))
            {
                BackgroundQueueSubmissionWorker?.Dispose();
            }
            HelperShader.Dispose();
            _pipeline.Dispose();
            BufferManager.Dispose();
            PipelineLayoutCache.Dispose();
            Barriers.Dispose();

            MemoryAllocator.Dispose();

            foreach (ShaderCollection shader in Shaders) shader.Dispose();
            foreach (ITexture texture in Textures) texture.Release();
            foreach (SamplerHolder sampler in Samplers) sampler.Dispose();

            PipelineLibrary?.Dispose();

            if (_surface.Handle != 0)
            {
                SurfaceApi.DestroySurface(_instance.Instance, _surface, null);
            }

            Api.DestroyDevice(_device, null);

            _debugMessenger.Dispose();

            // Last step destroy the instance
            _instance.Dispose();
        }

        public bool PrepareHostMapping(nint address, ulong size)
        {
            return Capabilities.SupportsHostImportedMemory &&
                HostMemoryAllocator.TryImport(BufferManager.HostImportedBufferMemoryRequirements, BufferManager.DefaultBufferMemoryFlags, address, size);
        }
    }
}
