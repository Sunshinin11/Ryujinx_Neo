using Ryujinx.Common.Logging;
using Silk.NET.Vulkan;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.Vulkan
{
    // NEO-GPL: VK_EXT_graphics_pipeline_library support.
    // A graphics pipeline is split into four libraries: vertex input interface, pre-rasterization shaders,
    // fragment shader and fragment output interface. The shader libraries are cached per program and the
    // fixed function ones globally, so a new state combination usually only needs a fast link instead of a
    // full shader compile. Fast linked pipelines are then recompiled with link time optimization on a
    // background thread and swapped in when ready.
    sealed class PipelineLibraryKey : IEquatable<PipelineLibraryKey>
    {
        private readonly byte[] _data;
        private readonly int _hash;

        public PipelineLibraryKey(byte[] data)
        {
            _data = data;

            HashCode hashCode = new();
            hashCode.AddBytes(data);
            _hash = hashCode.ToHashCode();
        }

        public bool Equals(PipelineLibraryKey other)
        {
            return other != null && _hash == other._hash && _data.AsSpan().SequenceEqual(other._data);
        }

        public override bool Equals(object obj)
        {
            return obj is PipelineLibraryKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return _hash;
        }
    }

    struct PipelineLibraryKeyBuilder
    {
        private byte[] _buffer;
        private int _length;

        public PipelineLibraryKeyBuilder(int capacity)
        {
            _buffer = new byte[capacity];
            _length = 0;
        }

        public void Add<T>(T value) where T : unmanaged
        {
            AddSpan(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
        }

        public void AddSpan<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values);

            if (_length + bytes.Length > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + bytes.Length));
            }

            bytes.CopyTo(_buffer.AsSpan(_length));
            _length += bytes.Length;
        }

        public readonly PipelineLibraryKey Build()
        {
            return new PipelineLibraryKey(_buffer.AsSpan(0, _length).ToArray());
        }
    }

    struct PipelineLibrarySet
    {
        public Pipeline VertexInput;
        public Pipeline PreRasterization;
        public Pipeline FragmentShader;
        public Pipeline FragmentOutput;
    }

    sealed class PipelineLibraryCache
    {
        private sealed class OptimizeRequest
        {
            public ShaderCollection Program;
            public PipelineUid Key;
            public PipelineLibrarySet Libraries;
            public PipelineLayout Layout;
        }

        private const int StatsInterval = 100;

        private readonly VulkanRenderer _gd;
        private readonly Device _device;

        private readonly Lock _lock = new();
        private readonly Dictionary<PipelineLibraryKey, Pipeline> _vertexInputLibraries = new();
        private readonly Dictionary<PipelineLibraryKey, Pipeline> _fragmentOutputLibraries = new();

        private readonly BlockingCollection<OptimizeRequest> _optimizeQueue;
        private readonly CancellationTokenSource _cancellation;
        private readonly Thread[] _workers;

        public bool OptimizeEnabled { get; }

        private readonly Lock _statsLock = new();
        private long _links;
        private long _linkTicks;
        private long _maxLinkTicks;
        private long _compileLinks;
        private long _compileLinkTicks;
        private long _maxCompileLinkTicks;
        private long _libraries;
        private long _optimized;
        private long _optimizeFailed;
        private long _fallbacks;

        private PipelineLibraryCache(VulkanRenderer gd, Device device, bool optimize)
        {
            _gd = gd;
            _device = device;
            OptimizeEnabled = optimize;

            if (optimize)
            {
                _optimizeQueue = new BlockingCollection<OptimizeRequest>();
                _cancellation = new CancellationTokenSource();

                int workerCount = Math.Clamp(Environment.ProcessorCount / 4, 1, 2);

                _workers = new Thread[workerCount];

                for (int i = 0; i < workerCount; i++)
                {
                    _workers[i] = new Thread(WorkerLoop)
                    {
                        Name = $"GPU.PipelineOptimizer.{i}",
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                    };

                    _workers[i].Start();
                }
            }
        }

        public static unsafe PipelineLibraryCache TryCreate(VulkanRenderer gd, Device device, VulkanPhysicalDevice physicalDevice)
        {
            string env = Environment.GetEnvironmentVariable("RYUJINX_NEO_GPL");

            if (env == "0" || gd.IsMoltenVk || gd.IsAdreno8xx)
            {
                return null;
            }

            if (!physicalDevice.IsDeviceExtensionPresent("VK_EXT_graphics_pipeline_library") ||
                !physicalDevice.IsDeviceExtensionPresent("VK_KHR_pipeline_library"))
            {
                return null;
            }

            PhysicalDeviceGraphicsPipelineLibraryFeaturesEXT features = new()
            {
                SType = StructureType.PhysicalDeviceGraphicsPipelineLibraryFeaturesExt,
            };

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &features,
            };

            gd.Api.GetPhysicalDeviceFeatures2(physicalDevice.PhysicalDevice, &features2);

            PhysicalDeviceGraphicsPipelineLibraryPropertiesEXT properties = new()
            {
                SType = StructureType.PhysicalDeviceGraphicsPipelineLibraryPropertiesExt,
            };

            PhysicalDeviceProperties2 properties2 = new()
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &properties,
            };

            gd.Api.GetPhysicalDeviceProperties2(physicalDevice.PhysicalDevice, &properties2);

            // Without fast linking, linking is as slow as a full compile and there's nothing to gain.
            if (!features.GraphicsPipelineLibrary || !properties.GraphicsPipelineLibraryFastLinking)
            {
                Logger.Info?.Print(LogClass.Gpu, $"[NEO-GPL] Not used (feature={(bool)features.GraphicsPipelineLibrary}, fastLinking={(bool)properties.GraphicsPipelineLibraryFastLinking})");

                return null;
            }

            bool optimize = Environment.GetEnvironmentVariable("RYUJINX_NEO_GPL_OPTIMIZE") != "0";

            Logger.Info?.Print(LogClass.Gpu, $"[NEO-GPL] Graphics pipeline library enabled (background optimization={optimize})");

            return new PipelineLibraryCache(gd, device, optimize);
        }

        public bool TryGetVertexInput(PipelineLibraryKey key, out Pipeline library)
        {
            lock (_lock)
            {
                return _vertexInputLibraries.TryGetValue(key, out library);
            }
        }

        public Pipeline AddVertexInput(PipelineLibraryKey key, Pipeline library)
        {
            return Add(_vertexInputLibraries, key, library);
        }

        public bool TryGetFragmentOutput(PipelineLibraryKey key, out Pipeline library)
        {
            lock (_lock)
            {
                return _fragmentOutputLibraries.TryGetValue(key, out library);
            }
        }

        public Pipeline AddFragmentOutput(PipelineLibraryKey key, Pipeline library)
        {
            return Add(_fragmentOutputLibraries, key, library);
        }

        private Pipeline Add(Dictionary<PipelineLibraryKey, Pipeline> dictionary, PipelineLibraryKey key, Pipeline library)
        {
            lock (_lock)
            {
                if (dictionary.TryGetValue(key, out Pipeline existing))
                {
                    // Another thread created the same library first.
                    DestroyPipeline(library);

                    return existing;
                }

                dictionary.Add(key, library);

                return library;
            }
        }

        public unsafe Pipeline CreateLibrary(GraphicsPipelineLibraryFlagsEXT libraryFlags, GraphicsPipelineCreateInfo info, PipelineCache cache)
        {
            GraphicsPipelineLibraryCreateInfoEXT libraryInfo = new()
            {
                SType = StructureType.GraphicsPipelineLibraryCreateInfoExt,
                PNext = info.PNext,
                Flags = libraryFlags,
            };

            info.PNext = &libraryInfo;
            info.Flags |= PipelineCreateFlags.CreateLibraryBitKhr;

            if (OptimizeEnabled)
            {
                info.Flags |= PipelineCreateFlags.CreateRetainLinkTimeOptimizationInfoBitExt;
            }

            Pipeline library = default;

            Result result = _gd.Api.CreateGraphicsPipelines(_device, cache, 1, &info, null, &library);

            if (result.IsError())
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[NEO-GPL] Failed to create {libraryFlags} library: {result}");

                return default;
            }

            Interlocked.Increment(ref _libraries);

            return library;
        }

        public unsafe Pipeline Link(PipelineLibrarySet libraries, PipelineLayout layout, RenderPass renderPass, PipelineCache cache, bool optimize)
        {
            Pipeline* pLibraries = stackalloc Pipeline[4];

            pLibraries[0] = libraries.VertexInput;
            pLibraries[1] = libraries.PreRasterization;
            pLibraries[2] = libraries.FragmentShader;
            pLibraries[3] = libraries.FragmentOutput;

            PipelineLibraryCreateInfoKHR libraryInfo = new()
            {
                SType = StructureType.PipelineLibraryCreateInfoKhr,
                LibraryCount = 4,
                PLibraries = pLibraries,
            };

            GraphicsPipelineCreateInfo info = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &libraryInfo,
                Flags = optimize ? PipelineCreateFlags.CreateLinkTimeOptimizationBitExt : 0,
                Layout = layout,
                RenderPass = renderPass,
                BasePipelineIndex = -1,
            };

            Pipeline pipeline = default;

            Result result = _gd.Api.CreateGraphicsPipelines(_device, cache, 1, &info, null, &pipeline);

            if (result.IsError())
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[NEO-GPL] Failed to link pipeline (optimize={optimize}): {result}");

                return default;
            }

            return pipeline;
        }

        public void RecordFallback()
        {
            Interlocked.Increment(ref _fallbacks);
        }

        public void RecordLink(long ticks, bool compiledShaders)
        {
            lock (_statsLock)
            {
                RecordLinkLocked(ticks, compiledShaders);
            }
        }

        private void RecordLinkLocked(long ticks, bool compiledShaders)
        {
            if (compiledShaders)
            {
                _compileLinks++;
                _compileLinkTicks += ticks;
                _maxCompileLinkTicks = Math.Max(_maxCompileLinkTicks, ticks);
            }
            else
            {
                _links++;
                _linkTicks += ticks;
                _maxLinkTicks = Math.Max(_maxLinkTicks, ticks);
            }

            long total = _links + _compileLinks;

            if (total <= 3 || total % StatsInterval == 0)
            {
                PrintStats();
            }
        }

        private void PrintStats()
        {
            static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

            Logger.Info?.Print(
                LogClass.Gpu,
                $"[NEO-GPL] fastLinks={_links} avgMs={(_links == 0 ? 0 : ToMs(_linkTicks) / _links):F2} maxMs={ToMs(_maxLinkTicks):F1} " +
                $"withShaderCompile={_compileLinks} avgMs={(_compileLinks == 0 ? 0 : ToMs(_compileLinkTicks) / _compileLinks):F1} maxMs={ToMs(_maxCompileLinkTicks):F1} " +
                $"libraries={Volatile.Read(ref _libraries)} fallbacks={Volatile.Read(ref _fallbacks)} optimized={Volatile.Read(ref _optimized)} " +
                $"optimizeFailed={Volatile.Read(ref _optimizeFailed)} pending={_optimizeQueue?.Count ?? 0}");
        }

        public void EnqueueOptimize(ShaderCollection program, ref PipelineUid key, PipelineLibrarySet libraries, PipelineLayout layout)
        {
            if (!OptimizeEnabled || _optimizeQueue.IsAddingCompleted)
            {
                return;
            }

            try
            {
                _optimizeQueue.Add(new OptimizeRequest
                {
                    Program = program,
                    Key = key,
                    Libraries = libraries,
                    Layout = layout,
                });
            }
            catch (InvalidOperationException)
            {
                // Shutting down.
            }
        }

        private void WorkerLoop()
        {
            try
            {
                foreach (OptimizeRequest request in _optimizeQueue.GetConsumingEnumerable(_cancellation.Token))
                {
                    ShaderCollection program = request.Program;

                    // The pre-rasterization and fragment libraries belong to the program,
                    // hold the lifetime lock so they can't be destroyed while linking.
                    lock (program.LibraryLifetimeLock)
                    {
                        if (program.LibrariesDisposed)
                        {
                            continue;
                        }

                        Pipeline pipeline = Link(request.Libraries, request.Layout, default, default, optimize: true);

                        if (pipeline.Handle == 0)
                        {
                            Interlocked.Increment(ref _optimizeFailed);

                            continue;
                        }

                        program.AddOptimizedPipeline(ref request.Key, pipeline);
                    }

                    Interlocked.Increment(ref _optimized);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void StopWorkers()
        {
            if (!OptimizeEnabled || _optimizeQueue.IsAddingCompleted)
            {
                return;
            }

            PrintStats();

            _optimizeQueue.CompleteAdding();
            _cancellation.Cancel();

            foreach (Thread worker in _workers)
            {
                worker.Join();
            }
        }

        public void Dispose()
        {
            StopWorkers();

            lock (_lock)
            {
                foreach (Pipeline library in _vertexInputLibraries.Values)
                {
                    DestroyPipeline(library);
                }

                foreach (Pipeline library in _fragmentOutputLibraries.Values)
                {
                    DestroyPipeline(library);
                }

                _vertexInputLibraries.Clear();
                _fragmentOutputLibraries.Clear();
            }
        }

        public void DestroyPipeline(Pipeline pipeline)
        {
            if (pipeline.Handle != 0)
            {
                _gd.Api.DestroyPipeline(_device, pipeline, Span<AllocationCallbacks>.Empty);
            }
        }
    }
}
