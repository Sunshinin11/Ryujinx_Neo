using Ryujinx.Graphics.Device;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Engine.InlineToMemory;
using Ryujinx.Graphics.Gpu.Engine.Threed;
using Ryujinx.Graphics.Gpu.Engine.Types;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Shader;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.Gpu.Engine.Compute
{
    /// <summary>
    /// Represents a compute engine class.
    /// </summary>
    class ComputeClass : IDeviceState
    {
        private readonly GpuContext _context;
        private readonly GpuChannel _channel;
        private readonly ThreedClass _3dEngine;
        private readonly DeviceState<ComputeClassState> _state;

        private readonly InlineToMemoryClass _i2mClass;

        /// <summary>
        /// Creates a new instance of the compute engine class.
        /// </summary>
        /// <param name="context">GPU context</param>
        /// <param name="channel">GPU channel</param>
        /// <param name="threedEngine">3D engine</param>
        public ComputeClass(GpuContext context, GpuChannel channel, ThreedClass threedEngine)
        {
            _context = context;
            _channel = channel;
            _3dEngine = threedEngine;
            _state = new DeviceState<ComputeClassState>(new Dictionary<string, RwCallback>
            {
                { nameof(ComputeClassState.LaunchDma), new RwCallback(LaunchDma, null) },
                { nameof(ComputeClassState.LoadInlineData), new RwCallback(LoadInlineData, null) },
                { nameof(ComputeClassState.SendSignalingPcasB), new RwCallback(SendSignalingPcasB, null) },
            });

            _i2mClass = new InlineToMemoryClass(context, channel, initializeState: false);
        }

        /// <summary>
        /// Reads data from the class registers.
        /// </summary>
        /// <param name="offset">Register byte offset</param>
        /// <returns>Data at the specified offset</returns>
        public int Read(int offset) => _state.Read(offset);

        /// <summary>
        /// Writes data to the class registers.
        /// </summary>
        /// <param name="offset">Register byte offset</param>
        /// <param name="data">Data to be written</param>
        public void Write(int offset, int data) => _state.Write(offset, data);

        /// <summary>
        /// Launches the Inline-to-Memory DMA copy operation.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        private void LaunchDma(int argument)
        {
            _i2mClass.LaunchDma(ref Unsafe.As<ComputeClassState, InlineToMemoryClassState>(ref _state.State), argument);
        }

        /// <summary>
        /// Pushes a block of data to the Inline-to-Memory engine.
        /// </summary>
        /// <param name="data">Data to push</param>
        public void LoadInlineData(ReadOnlySpan<int> data)
        {
            _i2mClass.LoadInlineData(data);
        }

        /// <summary>
        /// Pushes a word of data to the Inline-to-Memory engine.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        private void LoadInlineData(int argument)
        {
            _i2mClass.LoadInlineData(argument);
        }

        /// <summary>
        /// Performs the compute dispatch operation.
        /// </summary>
        /// <param name="argument">Method call argument</param>
        // DISPATCH-TRACE-E6: env-gated compute dispatch trace (RYUJINX_TOTK_DISPATCH_TRACE=1).
        private static readonly bool _dispatchTraceE6 =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_DISPATCH_TRACE") == "1";

        private static readonly string _dispatchDumpDirE6 =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_DISPATCH_DUMP_DIR");

        private static readonly Dictionary<string, int> _dispatchSeenE6 = new();

        // TILEDUMP-E7: when the trigger file exists, read back (tracked, GPU-synced) the storage buffers
        // used by the TOTK tile depth passes right after they are dispatched, then remove the trigger.
        private readonly List<(int Slot, ulong Va, uint Size)> _tileDumpSbE7 = new();
        private static readonly string _tileDumpDirE7 =
            Environment.GetEnvironmentVariable("RYUJINX_TOTK_TILEDUMP_DIR");
        private static long _tileDumpLastCheckE7;
        private static bool _tileDumpArmedE7;
        private static int _tileDumpSeqE7;

        private static string GuestHashE7(CachedShaderProgram cs)
        {
            byte[] code = cs?.Shaders?[0]?.Code;
            return code != null
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(code)).Substring(0, 16).ToLowerInvariant()
                : "nocode";
        }

        private void TileDumpE7(CachedShaderProgram cs, ComputeQmd qmd)
        {
            if (string.IsNullOrEmpty(_tileDumpDirE7))
            {
                return;
            }

            string trigger = System.IO.Path.Combine(_tileDumpDirE7, "dump-now");

            long now = Environment.TickCount64;
            if (!_tileDumpArmedE7)
            {
                if (now - _tileDumpLastCheckE7 < 500)
                {
                    return;
                }

                _tileDumpLastCheckE7 = now;
                _tileDumpArmedE7 = System.IO.File.Exists(trigger);

                if (!_tileDumpArmedE7)
                {
                    return;
                }
            }

            string hash = GuestHashE7(cs);
            bool isDepthTiles = hash == "a725974a07350e36";
            bool isMaxTiles = hash == "a4f14e4792099b76";

            if (!isDepthTiles && !isMaxTiles)
            {
                return;
            }

            try
            {
                int seq = ++_tileDumpSeqE7;
                var meta = new System.Text.StringBuilder();
                meta.AppendLine($"seq={seq} hash={hash} grid={qmd.CtaRasterWidth}x{qmd.CtaRasterHeight}x{qmd.CtaRasterDepth} block={qmd.CtaThreadDimension0}x{qmd.CtaThreadDimension1}x{qmd.CtaThreadDimension2} time={DateTime.Now:O}");

                for (int i = 0; i < Constants.TotalCpUniformBuffers; i++)
                {
                    if (!qmd.ConstantBufferValid(i))
                    {
                        continue;
                    }

                    ulong cbVa = (uint)qmd.ConstantBufferAddrLower(i) | (ulong)qmd.ConstantBufferAddrUpper(i) << 32;
                    int cbSize = Math.Min(qmd.ConstantBufferSize(i), 0x10000);
                    byte[] cbData = _channel.MemoryManager.GetSpan(cbVa, cbSize, tracked: true).ToArray();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(_tileDumpDirE7, $"{seq:D2}_{hash}_cb{i}.bin"), cbData);
                    meta.AppendLine($"cb{i} va=0x{cbVa:X} size={cbSize}");
                }

                foreach ((int slot, ulong va, uint size) in _tileDumpSbE7)
                {
                    int dumpSize = (int)Math.Min(size, 4u * 1024 * 1024);
                    byte[] data = _channel.MemoryManager.GetSpan(va, dumpSize, tracked: true).ToArray();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(_tileDumpDirE7, $"{seq:D2}_{hash}_sb{slot}.bin"), data);
                    meta.AppendLine($"sb{slot} va=0x{va:X} size={size} dumped={dumpSize}");
                }

                System.IO.File.WriteAllText(System.IO.Path.Combine(_tileDumpDirE7, $"{seq:D2}_{hash}_meta.txt"), meta.ToString());

                Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu, $"[TOTK-TILEDUMP-E7] {meta.ToString().Replace(Environment.NewLine, " | ")}");

                if (isMaxTiles)
                {
                    // a4f14e runs after a725 in the frame: one dump pair per trigger.
                    _tileDumpArmedE7 = false;
                    System.IO.File.Delete(trigger);
                }
            }
            catch (Exception ex)
            {
                _tileDumpArmedE7 = false;
                Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.Gpu, $"[TOTK-TILEDUMP-E7] failed: {ex.Message}");
            }
        }
        private static readonly HashSet<string> _dispatchDumpedE6 = new();

        private static void TraceDispatchE6(CachedShaderProgram cs, ulong shaderGpuVa, ComputeQmd qmd, int sharedMemorySize, int localMemorySize)
        {
            if (!_dispatchTraceE6 || cs?.Shaders == null || cs.Shaders.Length == 0 || cs.Shaders[0] == null)
            {
                return;
            }

            byte[] code = cs.Shaders[0].Code;
            string hash = code != null
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(code)).Substring(0, 16).ToLowerInvariant()
                : "nocode";

            string key = $"{hash} grid={qmd.CtaRasterWidth}x{qmd.CtaRasterHeight}x{qmd.CtaRasterDepth} block={qmd.CtaThreadDimension0}x{qmd.CtaThreadDimension1}x{qmd.CtaThreadDimension2}";

            lock (_dispatchSeenE6)
            {
                _dispatchSeenE6.TryGetValue(key, out int count);
                _dispatchSeenE6[key] = count + 1;

                if (count != 0)
                {
                    return;
                }

                ShaderProgramInfo info = cs.Shaders[0].Info;

                Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                    $"[TOTK-DISPATCH-E6] CS={key} va=0x{shaderGpuVa:X} smem={sharedMemorySize} lmem={localMemorySize} " +
                    $"cb={info.CBuffers.Count} sb={info.SBuffers.Count} tex={info.Textures.Count} img={info.Images.Count} codeLen={code?.Length ?? 0}");

                if (!string.IsNullOrEmpty(_dispatchDumpDirE6) && code != null && _dispatchDumpedE6.Add(hash))
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(_dispatchDumpDirE6);
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dispatchDumpDirE6, $"cs_{hash}.bin"), code);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private void SendSignalingPcasB(int argument)
        {
            MemoryManager memoryManager = _channel.MemoryManager;

            // Since we're going to change the state, make sure any pending instanced draws are done.
            _3dEngine.PerformDeferredDraws();

            // Make sure all pending uniform buffer data is written to memory.
            _3dEngine.FlushUboDirty();

            uint qmdAddress = _state.State.SendPcasA;

            ComputeQmd qmd = _channel.MemoryManager.Read<ComputeQmd>((ulong)qmdAddress << 8);

            ulong shaderGpuVa = ((ulong)_state.State.SetProgramRegionAAddressUpper << 32) | _state.State.SetProgramRegionB;

            shaderGpuVa += (uint)qmd.ProgramOffset;

            int localMemorySize = qmd.ShaderLocalMemoryLowSize + qmd.ShaderLocalMemoryHighSize;

            int sharedMemorySize = Math.Min(qmd.SharedMemorySize, _context.Capabilities.MaximumComputeSharedMemorySize);

            for (int index = 0; index < Constants.TotalCpUniformBuffers; index++)
            {
                if (!qmd.ConstantBufferValid(index))
                {
                    continue;
                }

                ulong gpuVa = (uint)qmd.ConstantBufferAddrLower(index) | (ulong)qmd.ConstantBufferAddrUpper(index) << 32;
                ulong size = (ulong)qmd.ConstantBufferSize(index);

                _channel.BufferManager.SetComputeUniformBuffer(index, gpuVa, size);
            }

            ulong samplerPoolGpuVa = ((ulong)_state.State.SetTexSamplerPoolAOffsetUpper << 32) | _state.State.SetTexSamplerPoolB;
            ulong texturePoolGpuVa = ((ulong)_state.State.SetTexHeaderPoolAOffsetUpper << 32) | _state.State.SetTexHeaderPoolB;

            int samplerPoolMaximumId = _state.State.SetTexSamplerPoolCMaximumIndex;

            GpuChannelPoolState poolState = new(
                texturePoolGpuVa,
                _state.State.SetTexHeaderPoolCMaximumIndex,
                _state.State.SetBindlessTextureConstantBufferSlotSelect);

            GpuChannelComputeState computeState = new(
                qmd.CtaThreadDimension0,
                qmd.CtaThreadDimension1,
                qmd.CtaThreadDimension2,
                localMemorySize,
                sharedMemorySize,
                _channel.BufferManager.HasUnalignedStorageBuffers);

            CachedShaderProgram cs = memoryManager.Physical.ShaderCache.GetComputeShader(_channel, samplerPoolMaximumId, poolState, computeState, shaderGpuVa);

            GraphicsCompatibility.RegisterMua3Program(cs.HostProgram, new Mua3ShaderIdentity(0, shaderGpuVa, 0, 0, 0, 0));

            _context.Renderer.Pipeline.SetProgram(cs.HostProgram);

            _channel.TextureManager.SetComputeSamplerPool(samplerPoolGpuVa, _state.State.SetTexSamplerPoolCMaximumIndex, qmd.SamplerIndex);
            _channel.TextureManager.SetComputeTexturePool(texturePoolGpuVa, _state.State.SetTexHeaderPoolCMaximumIndex);
            _channel.TextureManager.SetComputeTextureBufferIndex(_state.State.SetBindlessTextureConstantBufferSlotSelect);

            ShaderProgramInfo info = cs.Shaders[0].Info;

            _tileDumpSbE7.Clear();

            for (int index = 0; index < info.SBuffers.Count; index++)
            {
                BufferDescriptor sb = info.SBuffers[index];

                ulong sbDescAddress = _channel.BufferManager.GetComputeUniformBufferAddress(sb.SbCbSlot);
                sbDescAddress += (ulong)sb.SbCbOffset * 4;

                SbDescriptor sbDescriptor = _channel.MemoryManager.Physical.Read<SbDescriptor>(sbDescAddress);

                uint size = NeoStorageBufferSize.FromDriverDescriptor(_channel.MemoryManager, sbDescriptor, -1, sb.SbCbSlot);

                _channel.BufferManager.SetComputeStorageBuffer(sb.Slot, sbDescriptor.PackAddress(), size, sb.Flags);

                _tileDumpSbE7.Add((sb.Slot, sbDescriptor.PackAddress(), size));
            }

            if (_channel.BufferManager.HasUnalignedStorageBuffers != computeState.HasUnalignedStorageBuffer)
            {
                // Refetch the shader, as assumptions about storage buffer alignment have changed.
                computeState = new GpuChannelComputeState(
                    qmd.CtaThreadDimension0,
                    qmd.CtaThreadDimension1,
                    qmd.CtaThreadDimension2,
                    localMemorySize,
                    sharedMemorySize,
                    _channel.BufferManager.HasUnalignedStorageBuffers);

                cs = memoryManager.Physical.ShaderCache.GetComputeShader(_channel, samplerPoolMaximumId, poolState, computeState, shaderGpuVa);

                GraphicsCompatibility.RegisterMua3Program(cs.HostProgram, new Mua3ShaderIdentity(0, shaderGpuVa, 0, 0, 0, 0));

                _context.Renderer.Pipeline.SetProgram(cs.HostProgram);
            }

            _channel.BufferManager.SetComputeBufferBindings(cs.Bindings);

            _channel.TextureManager.SetComputeBindings(cs.Bindings);

            // Should never return false for mismatching spec state, since the shader was fetched above.
            _channel.TextureManager.CommitComputeBindings(cs.SpecializationState);

            _channel.BufferManager.CommitComputeBindings();

            TraceDispatchE6(cs, shaderGpuVa, qmd, sharedMemorySize, localMemorySize);

            string csHashE12 = TotkDiagE12.Enabled ? TotkDiagE12.Hash(cs.Shaders[0]?.Code) : null;
            bool skipE12 = TotkDiagE12.ShouldSkip(csHashE12);
            TotkDiagE12.Timeline($"DISPATCH cs={csHashE12} grid={qmd.CtaRasterWidth}x{qmd.CtaRasterHeight}x{qmd.CtaRasterDepth} block={qmd.CtaThreadDimension0}x{qmd.CtaThreadDimension1}x{qmd.CtaThreadDimension2} skip={skipE12}");

            if (!skipE12)
            {
                _context.Renderer.Pipeline.DispatchCompute(qmd.CtaRasterWidth, qmd.CtaRasterHeight, qmd.CtaRasterDepth);
            }

            TileDumpE7(cs, qmd);

            _3dEngine.ForceShaderUpdate();
        }
    }
}
