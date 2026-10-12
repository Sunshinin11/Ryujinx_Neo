using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Vulkan
{
    public unsafe static class VulkanInitialization
    {
        private const uint InvalidIndex = uint.MaxValue;
        private static readonly uint _minimalVulkanVersion = Vk.Version11.Value;
        private static readonly uint _minimalInstanceVulkanVersion = Vk.Version12.Value;
        private static readonly uint _maximumVulkanVersion = Vk.Version12.Value;
        private const string AppName = "Ryujinx.Graphics.Vulkan";
        private const int QueuesCount = 2;

        private static readonly string[] _desirableExtensions =
        [
            ExtConditionalRendering.ExtensionName,
            ExtExtendedDynamicState.ExtensionName,
            ExtTransformFeedback.ExtensionName,
            KhrDrawIndirectCount.ExtensionName,
            KhrPushDescriptor.ExtensionName,
            ExtExternalMemoryHost.ExtensionName,
            ExtPageableDeviceLocalMemory.ExtensionName,
            "VK_EXT_blend_operation_advanced",
            "VK_EXT_custom_border_color",
            "VK_EXT_descriptor_indexing", // Enabling this works around an issue with disposed buffer bindings on RADV.
            "VK_EXT_fragment_shader_interlock",
            "VK_EXT_index_type_uint8",
            "VK_EXT_primitive_topology_list_restart",
            "VK_EXT_robustness2",
            "VK_EXT_shader_stencil_export",
            "VK_KHR_shader_float16_int8",
            "VK_EXT_shader_subgroup_ballot",
            "VK_NV_geometry_shader_passthrough",
            "VK_NV_viewport_array2",
            "VK_EXT_depth_clip_control",
            "VK_KHR_portability_subset", // As per spec, we should enable this if present.
            "VK_EXT_4444_formats",
            "VK_KHR_8bit_storage",
            "VK_KHR_maintenance2",
            "VK_EXT_attachment_feedback_loop_layout",
            "VK_EXT_attachment_feedback_loop_dynamic_state",
            "VK_EXT_memory_priority",
            "VK_KHR_pipeline_library",
            "VK_EXT_graphics_pipeline_library"
        ];

        private static readonly string[] _requiredExtensions =
        [
            KhrSwapchain.ExtensionName
        ];

        internal static VulkanInstance CreateInstance(Vk api, GraphicsDebugLevel logLevel, string[] requiredExtensions)
        {
            List<string> enabledLayers = new();

            IReadOnlySet<string> instanceExtensions = VulkanInstance.GetInstanceExtensions(api);
            IReadOnlySet<string> instanceLayers = VulkanInstance.GetInstanceLayers(api);

            void AddAvailableLayer(string layerName)
            {
                if (instanceLayers.Contains(layerName))
                {
                    enabledLayers.Add(layerName);
                }
                else
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"Missing layer {layerName}");
                }
            }

            if (logLevel != GraphicsDebugLevel.None)
            {
                AddAvailableLayer("VK_LAYER_KHRONOS_validation");
            }

            string[] enabledExtensions = requiredExtensions;

            if (instanceExtensions.Contains("VK_EXT_debug_utils"))
            {
                enabledExtensions = enabledExtensions.Append(ExtDebugUtils.ExtensionName).ToArray();
            }

            IntPtr appName = Marshal.StringToHGlobalAnsi(AppName);

            ApplicationInfo applicationInfo = new()
            {
                PApplicationName = (byte*)appName,
                ApplicationVersion = 1,
                PEngineName = (byte*)appName,
                EngineVersion = 1,
                ApiVersion = _maximumVulkanVersion,
            };

            nint* ppEnabledExtensions = stackalloc nint[enabledExtensions.Length];
            nint* ppEnabledLayers = stackalloc nint[enabledLayers.Count];

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                ppEnabledExtensions[i] = Marshal.StringToHGlobalAnsi(enabledExtensions[i]);
            }

            for (int i = 0; i < enabledLayers.Count; i++)
            {
                ppEnabledLayers[i] = Marshal.StringToHGlobalAnsi(enabledLayers[i]);
            }

            InstanceCreateInfo instanceCreateInfo = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo,
                PpEnabledExtensionNames = (byte**)ppEnabledExtensions,
                PpEnabledLayerNames = (byte**)ppEnabledLayers,
                EnabledExtensionCount = (uint)enabledExtensions.Length,
                EnabledLayerCount = (uint)enabledLayers.Count,
            };

            Result result = VulkanInstance.Create(api, ref instanceCreateInfo, out VulkanInstance instance);

            Marshal.FreeHGlobal(appName);

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                Marshal.FreeHGlobal(ppEnabledExtensions[i]);
            }

            for (int i = 0; i < enabledLayers.Count; i++)
            {
                Marshal.FreeHGlobal(ppEnabledLayers[i]);
            }

            result.ThrowOnError();

            return instance;
        }

        internal static VulkanPhysicalDevice FindSuitablePhysicalDevice(Vk api, VulkanInstance instance, SurfaceKHR surface, string preferredGpuId)
        {
            instance.EnumeratePhysicalDevices(out VulkanPhysicalDevice[] physicalDevices).ThrowOnError();

            // First we try to pick the user preferred GPU.
            for (int i = 0; i < physicalDevices.Length; i++)
            {
                if (IsPreferredAndSuitableDevice(api, physicalDevices[i], surface, preferredGpuId))
                {
                    return physicalDevices[i];
                }
            }

            // If we fail to do that, just use the first compatible GPU.
            for (int i = 0; i < physicalDevices.Length; i++)
            {
                if (IsSuitableDevice(api, physicalDevices[i], surface))
                {
                    return physicalDevices[i];
                }
            }

            throw new VulkanException("Initialization failed, none of the available GPUs meets the minimum requirements.");
        }

        internal static DeviceInfo[] GetSuitablePhysicalDevices(Vk api)
        {
            IntPtr appName = Marshal.StringToHGlobalAnsi(AppName);

            ApplicationInfo applicationInfo = new()
            {
                PApplicationName = (byte*)appName,
                ApplicationVersion = 1,
                PEngineName = (byte*)appName,
                EngineVersion = 1,
                ApiVersion = _maximumVulkanVersion,
            };

            InstanceCreateInfo instanceCreateInfo = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo,
                PpEnabledExtensionNames = null,
                PpEnabledLayerNames = null,
                EnabledExtensionCount = 0,
                EnabledLayerCount = 0,
            };

            Result result = VulkanInstance.Create(api, ref instanceCreateInfo, out VulkanInstance rawInstance);

            Marshal.FreeHGlobal(appName);

            result.ThrowOnError();

            using VulkanInstance instance = rawInstance;

            // We currently assume that the instance is compatible with Vulkan 1.2
            // TODO: Remove this once we relax our initialization codepaths.
            if (instance.InstanceVersion < _minimalInstanceVulkanVersion)
            {
                return [];
            }

            instance.EnumeratePhysicalDevices(out VulkanPhysicalDevice[] physicalDevices).ThrowOnError();

            List<DeviceInfo> deviceInfos = [];

            foreach (VulkanPhysicalDevice physicalDevice in physicalDevices)
            {
                if (physicalDevice.PhysicalDeviceProperties.ApiVersion < _minimalVulkanVersion)
                {
                    continue;
                }

                deviceInfos.Add(physicalDevice.ToDeviceInfo());
            }

            return deviceInfos.ToArray();
        }

        private static bool IsPreferredAndSuitableDevice(Vk api, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface, string preferredGpuId)
        {
            if (physicalDevice.Id != preferredGpuId)
            {
                return false;
            }

            return IsSuitableDevice(api, physicalDevice, surface);
        }

        private static bool IsSuitableDevice(Vk api, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface)
        {
            int extensionMatches = 0;

            foreach (string requiredExtension in _requiredExtensions)
            {
                if (physicalDevice.IsDeviceExtensionPresent(requiredExtension))
                {
                    extensionMatches++;
                }
            }

            return extensionMatches == _requiredExtensions.Length && FindSuitableQueueFamily(api, physicalDevice, surface, out _) != InvalidIndex;
        }

        internal static uint FindSuitableQueueFamily(Vk api, VulkanPhysicalDevice physicalDevice, SurfaceKHR surface, out uint queueCount)
        {
            const QueueFlags RequiredFlags = QueueFlags.GraphicsBit | QueueFlags.ComputeBit;

            KhrSurface khrSurface = new(api.Context);

            for (uint index = 0; index < physicalDevice.QueueFamilyProperties.Length; index++)
            {
                ref QueueFamilyProperties property = ref physicalDevice.QueueFamilyProperties[index];

                khrSurface.GetPhysicalDeviceSurfaceSupport(physicalDevice.PhysicalDevice, index, surface, out Bool32 surfaceSupported).ThrowOnError();

                if (property.QueueFlags.HasFlag(RequiredFlags) && surfaceSupported)
                {
                    queueCount = property.QueueCount;

                    return index;
                }
            }

            queueCount = 0;

            return InvalidIndex;
        }

        internal static Device CreateDevice(Vk api, VulkanPhysicalDevice physicalDevice, uint queueFamilyIndex, uint queueCount, out bool timelineSemaphoreEnabled)
        {
            if (queueCount > QueuesCount)
            {
                queueCount = QueuesCount;
            }

            float* queuePriorities = stackalloc float[(int)queueCount];

            for (int i = 0; i < queueCount; i++)
            {
                queuePriorities[i] = 1f;
            }

            DeviceQueueCreateInfo queueCreateInfo = new()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = queueFamilyIndex,
                QueueCount = queueCount,
                PQueuePriorities = queuePriorities,
            };

            bool useRobustBufferAccess = VendorUtils.FromId(physicalDevice.PhysicalDeviceProperties.VendorID) == Vendor.Nvidia;

            PhysicalDeviceVertexInputDynamicStateFeaturesEXT featuresVertexInputDynamicState = new()
            {
                SType = StructureType.PhysicalDeviceVertexInputDynamicStateFeaturesExt,
            };

            PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT featuresPrimitiveTopologyListRestart = new()
            {
                SType = StructureType.PhysicalDevicePrimitiveTopologyListRestartFeaturesExt,
            };

            PhysicalDeviceRobustness2FeaturesEXT featuresRobustness2 = new()
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
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

            PhysicalDeviceIndexTypeUint8FeaturesEXT featuresIndexU8 = new()
            {
                SType = StructureType.PhysicalDeviceIndexTypeUint8FeaturesExt,
            };

            PhysicalDeviceFragmentShaderInterlockFeaturesEXT featuresFragmentShaderInterlock = new()
            {
                SType = StructureType.PhysicalDeviceFragmentShaderInterlockFeaturesExt,
            };

            PhysicalDeviceShaderFloat16Int8FeaturesKHR featuresShaderInt8 = new()
            {
                SType = StructureType.PhysicalDeviceShaderFloat16Int8Features,
            };

            PhysicalDeviceTransformFeedbackFeaturesEXT featuresTransformFeedback = new()
            {
                SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
            };

            PhysicalDeviceConditionalRenderingFeaturesEXT featuresConditionalRendering = new()
            {
                SType = StructureType.PhysicalDeviceConditionalRenderingFeaturesExt,
            };

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
            };

            PhysicalDeviceVulkan11Features supportedFeaturesVk11 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan11Features,
                PNext = features2.PNext,
            };

            features2.PNext = &supportedFeaturesVk11;

            PhysicalDeviceCustomBorderColorFeaturesEXT supportedFeaturesCustomBorderColor = new()
            {
                SType = StructureType.PhysicalDeviceCustomBorderColorFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color"))
            {
                features2.PNext = &supportedFeaturesCustomBorderColor;
            }

            PhysicalDevicePrimitiveTopologyListRestartFeaturesEXT supportedFeaturesPrimitiveTopologyListRestart = new()
            {
                SType = StructureType.PhysicalDevicePrimitiveTopologyListRestartFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_primitive_topology_list_restart"))
            {
                features2.PNext = &supportedFeaturesPrimitiveTopologyListRestart;
            }

            PhysicalDeviceTransformFeedbackFeaturesEXT supportedFeaturesTransformFeedback = new()
            {
                SType = StructureType.PhysicalDeviceTransformFeedbackFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent(ExtTransformFeedback.ExtensionName))
            {
                features2.PNext = &supportedFeaturesTransformFeedback;
            }

            PhysicalDeviceConditionalRenderingFeaturesEXT supportedFeaturesConditionalRendering = new()
            {
                SType = StructureType.PhysicalDeviceConditionalRenderingFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent(ExtConditionalRendering.ExtensionName))
            {
                features2.PNext = &supportedFeaturesConditionalRendering;
            }

            PhysicalDeviceRobustness2FeaturesEXT supportedFeaturesRobustness2 = new()
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_robustness2"))
            {
                supportedFeaturesRobustness2.PNext = features2.PNext;

                features2.PNext = &supportedFeaturesRobustness2;
            }

            PhysicalDeviceDepthClipControlFeaturesEXT supportedFeaturesDepthClipControl = new()
            {
                SType = StructureType.PhysicalDeviceDepthClipControlFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_depth_clip_control"))
            {
                features2.PNext = &supportedFeaturesDepthClipControl;
            }

            PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesEXT supportedFeaturesAttachmentFeedbackLoopLayout = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopLayoutFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_layout"))
            {
                features2.PNext = &supportedFeaturesAttachmentFeedbackLoopLayout;
            }

            PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesEXT supportedFeaturesDynamicAttachmentFeedbackLoopLayout = new()
            {
                SType = StructureType.PhysicalDeviceAttachmentFeedbackLoopDynamicStateFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_dynamic_state"))
            {
                features2.PNext = &supportedFeaturesDynamicAttachmentFeedbackLoopLayout;
            }

            PhysicalDeviceVertexInputDynamicStateFeaturesEXT supportedFeaturesVertexInputDynamicState = new()
            {
                SType = StructureType.PhysicalDeviceVertexInputDynamicStateFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_vertex_input_dynamic_state"))
            {
                features2.PNext = &supportedFeaturesVertexInputDynamicState;
            }

            PhysicalDeviceShaderFloat16Int8FeaturesKHR supportedFeaturesShaderInt8 = new()
            {
                SType = StructureType.PhysicalDeviceShaderFloat16Int8Features,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_KHR_shader_float16_int8"))
            {
                features2.PNext = &supportedFeaturesShaderInt8;
            }

            PhysicalDevicePageableDeviceLocalMemoryFeaturesEXT supportedPageableDeviceLocalMemoryFeatures = new()
            {
                SType = StructureType.PhysicalDevicePageableDeviceLocalMemoryFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent(ExtPageableDeviceLocalMemory.ExtensionName))
            {
                features2.PNext = &supportedPageableDeviceLocalMemoryFeatures;
            }

            PhysicalDeviceMemoryPriorityFeaturesEXT supportedDeviceMemoryPriorityFeatures = new()
            {
                SType = StructureType.PhysicalDeviceMemoryPriorityFeaturesExt,
                PNext = features2.PNext,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_memory_priority"))
            {
                features2.PNext = &supportedDeviceMemoryPriorityFeatures;
            }

            PhysicalDeviceVulkan12Features supportedPhysicalDeviceVulkan12Features = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
                PNext = features2.PNext,
            };

            features2.PNext = &supportedPhysicalDeviceVulkan12Features;

            api.GetPhysicalDeviceFeatures2(physicalDevice.PhysicalDevice, &features2);

            // NEOV2 Etapa 2: conclusao de command buffers por timeline semaphore (portado do Gezinjinx).
            timelineSemaphoreEnabled =
                physicalDevice.PhysicalDeviceProperties.ApiVersion >= Vk.Version12.Value &&
                supportedPhysicalDeviceVulkan12Features.TimelineSemaphore;

            PhysicalDeviceFeatures supportedFeatures = features2.Features;

            PhysicalDeviceFeatures features = new()
            {
                DepthBiasClamp = supportedFeatures.DepthBiasClamp,
                DepthClamp = supportedFeatures.DepthClamp,
                DualSrcBlend = supportedFeatures.DualSrcBlend,
                FragmentStoresAndAtomics = supportedFeatures.FragmentStoresAndAtomics,
                GeometryShader = supportedFeatures.GeometryShader,
                ImageCubeArray = supportedFeatures.ImageCubeArray,
                IndependentBlend = supportedFeatures.IndependentBlend,
                LogicOp = supportedFeatures.LogicOp,
                OcclusionQueryPrecise = supportedFeatures.OcclusionQueryPrecise,
                MultiViewport = supportedFeatures.MultiViewport,
                PipelineStatisticsQuery = supportedFeatures.PipelineStatisticsQuery,
                SamplerAnisotropy = supportedFeatures.SamplerAnisotropy,
                ShaderClipDistance = supportedFeatures.ShaderClipDistance,
                ShaderFloat64 = supportedFeatures.ShaderFloat64,
                ShaderImageGatherExtended = supportedFeatures.ShaderImageGatherExtended,
                ShaderStorageImageMultisample = supportedFeatures.ShaderStorageImageMultisample,
                ShaderStorageImageReadWithoutFormat = supportedFeatures.ShaderStorageImageReadWithoutFormat,
                ShaderStorageImageWriteWithoutFormat = supportedFeatures.ShaderStorageImageWriteWithoutFormat,
                TessellationShader = supportedFeatures.TessellationShader,
                VertexPipelineStoresAndAtomics = supportedFeatures.VertexPipelineStoresAndAtomics,
                RobustBufferAccess = useRobustBufferAccess,
                SampleRateShading = supportedFeatures.SampleRateShading,
                ShaderStorageImageExtendedFormats = supportedFeatures.ShaderStorageImageExtendedFormats,
            };

            void* pExtendedFeatures = null;

            if (physicalDevice.IsDeviceExtensionPresent("VK_KHR_shader_float16_int8"))
            {
                featuresShaderInt8.PNext = pExtendedFeatures;
                featuresShaderInt8.ShaderInt8 = supportedFeaturesShaderInt8.ShaderInt8;

                pExtendedFeatures = &featuresShaderInt8;
            }

            if (physicalDevice.IsDeviceExtensionPresent(ExtTransformFeedback.ExtensionName))
            {
                featuresTransformFeedback.PNext = pExtendedFeatures;
                featuresTransformFeedback.TransformFeedback = supportedFeaturesTransformFeedback.TransformFeedback;

                pExtendedFeatures = &featuresTransformFeedback;
            }

            if (physicalDevice.IsDeviceExtensionPresent(ExtConditionalRendering.ExtensionName) &&
                supportedFeaturesConditionalRendering.ConditionalRendering)
            {
                featuresConditionalRendering.PNext = pExtendedFeatures;
                featuresConditionalRendering.ConditionalRendering = true;

                pExtendedFeatures = &featuresConditionalRendering;
            }

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_primitive_topology_list_restart"))
            {
                featuresPrimitiveTopologyListRestart.PNext = pExtendedFeatures;
                featuresPrimitiveTopologyListRestart.PrimitiveTopologyListRestart = supportedFeaturesPrimitiveTopologyListRestart.PrimitiveTopologyListRestart;
                featuresPrimitiveTopologyListRestart.PrimitiveTopologyPatchListRestart = supportedFeaturesPrimitiveTopologyListRestart.PrimitiveTopologyPatchListRestart;

                pExtendedFeatures = &featuresPrimitiveTopologyListRestart;
            }

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_robustness2"))
            {
                featuresRobustness2.PNext = pExtendedFeatures;
                featuresRobustness2.NullDescriptor = supportedFeaturesRobustness2.NullDescriptor;

                pExtendedFeatures = &featuresRobustness2;
            }

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_vertex_input_dynamic_state"))
            {
                featuresVertexInputDynamicState.PNext = pExtendedFeatures;
                featuresVertexInputDynamicState.VertexInputDynamicState = supportedFeaturesVertexInputDynamicState.VertexInputDynamicState;

                pExtendedFeatures = &featuresVertexInputDynamicState;
            }

            PhysicalDeviceExtendedDynamicStateFeaturesEXT featuresExtendedDynamicState = new()
            {
                SType = StructureType.PhysicalDeviceExtendedDynamicStateFeaturesExt,
                PNext = pExtendedFeatures,
                ExtendedDynamicState = physicalDevice.IsDeviceExtensionPresent(ExtExtendedDynamicState.ExtensionName),
            };

            pExtendedFeatures = &featuresExtendedDynamicState;

            PhysicalDeviceVulkan11Features featuresVk11 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan11Features,
                PNext = pExtendedFeatures,
                ShaderDrawParameters = supportedFeaturesVk11.ShaderDrawParameters,
                StorageBuffer16BitAccess = supportedFeaturesVk11.StorageBuffer16BitAccess,
                UniformAndStorageBuffer16BitAccess = supportedFeaturesVk11.UniformAndStorageBuffer16BitAccess,
                StoragePushConstant16 = supportedFeaturesVk11.StoragePushConstant16,
                StorageInputOutput16 = supportedFeaturesVk11.StorageInputOutput16,
            };

            pExtendedFeatures = &featuresVk11;

            PhysicalDeviceVulkan12Features featuresVk12 = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
                PNext = pExtendedFeatures,
                DescriptorIndexing = supportedPhysicalDeviceVulkan12Features.DescriptorIndexing,
                DrawIndirectCount = supportedPhysicalDeviceVulkan12Features.DrawIndirectCount,
                UniformBufferStandardLayout = supportedPhysicalDeviceVulkan12Features.UniformBufferStandardLayout,
                UniformAndStorageBuffer8BitAccess = supportedPhysicalDeviceVulkan12Features.UniformAndStorageBuffer8BitAccess,
                StorageBuffer8BitAccess = supportedPhysicalDeviceVulkan12Features.StorageBuffer8BitAccess,
                StoragePushConstant8 = supportedPhysicalDeviceVulkan12Features.StoragePushConstant8,
                TimelineSemaphore = timelineSemaphoreEnabled,
                ShaderSampledImageArrayNonUniformIndexing = supportedPhysicalDeviceVulkan12Features.ShaderSampledImageArrayNonUniformIndexing,
                ShaderStorageImageArrayNonUniformIndexing = supportedPhysicalDeviceVulkan12Features.ShaderStorageImageArrayNonUniformIndexing,
            };

            pExtendedFeatures = &featuresVk12;

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_index_type_uint8"))
            {
                featuresIndexU8.PNext = pExtendedFeatures;
                featuresIndexU8.IndexTypeUint8 = true;

                pExtendedFeatures = &featuresIndexU8;
            }

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_fragment_shader_interlock"))
            {
                featuresFragmentShaderInterlock.PNext = pExtendedFeatures;
                featuresFragmentShaderInterlock.FragmentShaderPixelInterlock = true;

                pExtendedFeatures = &featuresFragmentShaderInterlock;
            }

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_custom_border_color") &&
                supportedFeaturesCustomBorderColor.CustomBorderColors &&
                supportedFeaturesCustomBorderColor.CustomBorderColorWithoutFormat)
            {
                featuresCustomBorderColor.PNext = pExtendedFeatures;
                featuresCustomBorderColor.CustomBorderColors = true;
                featuresCustomBorderColor.CustomBorderColorWithoutFormat = true;

                pExtendedFeatures = &featuresCustomBorderColor;
            }

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_depth_clip_control") &&
                supportedFeaturesDepthClipControl.DepthClipControl)
            {
                featuresDepthClipControl.PNext = pExtendedFeatures;
                featuresDepthClipControl.DepthClipControl = true;

                pExtendedFeatures = &featuresDepthClipControl;
            }

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_layout") &&
                supportedFeaturesAttachmentFeedbackLoopLayout.AttachmentFeedbackLoopLayout)
            {
                featuresAttachmentFeedbackLoop.PNext = pExtendedFeatures;
                featuresAttachmentFeedbackLoop.AttachmentFeedbackLoopLayout = true;

                pExtendedFeatures = &featuresAttachmentFeedbackLoop;
            }

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_attachment_feedback_loop_dynamic_state") &&
                supportedFeaturesDynamicAttachmentFeedbackLoopLayout.AttachmentFeedbackLoopDynamicState)
            {
                featuresDynamicAttachmentFeedbackLoop.PNext = pExtendedFeatures;
                featuresDynamicAttachmentFeedbackLoop.AttachmentFeedbackLoopDynamicState = true;

                pExtendedFeatures = &featuresDynamicAttachmentFeedbackLoop;
            }

            if (physicalDevice.IsDeviceExtensionPresent(ExtPageableDeviceLocalMemory.ExtensionName) &&
                supportedPageableDeviceLocalMemoryFeatures.PageableDeviceLocalMemory)
            {
                PhysicalDevicePageableDeviceLocalMemoryFeaturesEXT featuresPageableDeviceLocalMemory = new()
                {
                    SType = StructureType.PhysicalDevicePageableDeviceLocalMemoryFeaturesExt,
                    PNext = pExtendedFeatures,
                    PageableDeviceLocalMemory = true,
                };

                pExtendedFeatures = &featuresPageableDeviceLocalMemory;
            }

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_memory_priority") &&
                supportedDeviceMemoryPriorityFeatures.MemoryPriority)
            {
                PhysicalDeviceMemoryPriorityFeaturesEXT featuresDeviceMemoryPriority = new()
                {
                    SType = StructureType.PhysicalDeviceMemoryPriorityFeaturesExt,
                    PNext = pExtendedFeatures,
                    MemoryPriority = true,
                };

                pExtendedFeatures = &featuresDeviceMemoryPriority;
            }

            // NEO-GPL: enable VK_EXT_graphics_pipeline_library when the device supports it.
            PhysicalDeviceGraphicsPipelineLibraryFeaturesEXT featuresGraphicsPipelineLibrary = new()
            {
                SType = StructureType.PhysicalDeviceGraphicsPipelineLibraryFeaturesExt,
            };

            if (physicalDevice.IsDeviceExtensionPresent("VK_EXT_graphics_pipeline_library") &&
                physicalDevice.IsDeviceExtensionPresent("VK_KHR_pipeline_library"))
            {
                PhysicalDeviceGraphicsPipelineLibraryFeaturesEXT supportedFeaturesGraphicsPipelineLibrary = new()
                {
                    SType = StructureType.PhysicalDeviceGraphicsPipelineLibraryFeaturesExt,
                };

                PhysicalDeviceFeatures2 graphicsPipelineLibraryFeatures2 = new()
                {
                    SType = StructureType.PhysicalDeviceFeatures2,
                    PNext = &supportedFeaturesGraphicsPipelineLibrary,
                };

                api.GetPhysicalDeviceFeatures2(physicalDevice.PhysicalDevice, &graphicsPipelineLibraryFeatures2);

                if (supportedFeaturesGraphicsPipelineLibrary.GraphicsPipelineLibrary)
                {
                    featuresGraphicsPipelineLibrary.PNext = pExtendedFeatures;
                    featuresGraphicsPipelineLibrary.GraphicsPipelineLibrary = true;

                    pExtendedFeatures = &featuresGraphicsPipelineLibrary;
                }
            }

            string[] enabledExtensions = _requiredExtensions.Union(_desirableExtensions.Intersect(physicalDevice.DeviceExtensions)).ToArray();

            nint* ppEnabledExtensions = stackalloc nint[enabledExtensions.Length];

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                ppEnabledExtensions[i] = Marshal.StringToHGlobalAnsi(enabledExtensions[i]);
            }

            DeviceCreateInfo deviceCreateInfo = new()
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = pExtendedFeatures,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueCreateInfo,
                PpEnabledExtensionNames = (byte**)ppEnabledExtensions,
                EnabledExtensionCount = (uint)enabledExtensions.Length,
                PEnabledFeatures = &features,
            };

            api.CreateDevice(physicalDevice.PhysicalDevice, in deviceCreateInfo, null, out Device device).ThrowOnError();

            for (int i = 0; i < enabledExtensions.Length; i++)
            {
                Marshal.FreeHGlobal(ppEnabledExtensions[i]);
            }

            return device;
        }
    }
}
