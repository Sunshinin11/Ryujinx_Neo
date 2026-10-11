using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Vulkan
{
    class Shader : IDisposable
    {
        private static readonly nint _ptrMainEntryPointName = Marshal.StringToHGlobalAnsi("main");

        private readonly Vk _api;
        private readonly Device _device;
        private readonly ShaderStageFlags _stage;

        private bool _disposed;
        private ShaderModule _module;
        private string _codeHash;

        public ShaderStageFlags StageFlags => _stage;
        public string CodeHash
        {
            get
            {
                CompileTask.Wait();
                return _codeHash;
            }
        }

        public ProgramLinkStatus CompileStatus { private set; get; }

        public readonly Task CompileTask;

        public unsafe Shader(Vk api, Device device, ShaderSource shaderSource)
        {
            _api = api;
            _device = device;

            CompileStatus = ProgramLinkStatus.Incomplete;

            _stage = shaderSource.Stage.Convert();

            CompileTask = Task.Run(() =>
            {
                byte[] spirv = shaderSource.BinaryCode;

                if (spirv == null)
                {
                    spirv = GlslToSpirv(shaderSource.Code, shaderSource.Stage);

                    if (spirv == null)
                    {
                        CompileStatus = ProgramLinkStatus.Failure;

                        return;
                    }
                }

                _codeHash = Convert.ToHexString(SHA256.HashData(spirv)).ToLowerInvariant();

                // TOTK-C827-SHADERDUMP7
                if (shaderSource.Stage == ShaderStage.Fragment &&
                    string.Equals(
                        _codeHash,
                        "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string dumpDir =
                            Environment.GetEnvironmentVariable("RYUJINX_TOTK_SHADER_DUMP_DIR");

                        if (!string.IsNullOrWhiteSpace(dumpDir))
                        {
                            Directory.CreateDirectory(dumpDir);

                            string spvPath = Path.Combine(
                                dumpDir,
                                "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae.spv");

                            File.WriteAllBytes(spvPath, spirv);

                            if (!string.IsNullOrEmpty(shaderSource.Code))
                            {
                                string textPath = Path.Combine(
                                    dumpDir,
                                    "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae.txt");

                                File.WriteAllText(textPath, shaderSource.Code);
                            }

                            Logger.Info?.Print(
                                LogClass.Gpu,
                                $"[TOTK-C827-SHADERDUMP7] DUMPED bytes={spirv.Length} dir={dumpDir} text={!string.IsNullOrEmpty(shaderSource.Code)}");
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Error?.Print(
                            LogClass.Gpu,
                            $"[TOTK-C827-SHADERDUMP7] FAILED {e.GetType().Name}: {e.Message}");
                    }
                }

                // TOTK-C827-GATHER-BYPASS9
                if (shaderSource.Stage == ShaderStage.Fragment &&
                    string.Equals(
                        _codeHash,
                        "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                        StringComparison.OrdinalIgnoreCase) &&
                    Environment.GetEnvironmentVariable("RYUJINX_TOTK_C827_GATHER_BYPASS") == "1")
                {
                    spirv = PatchTotkC827GatherBypass(spirv, out bool gatherPatched);

                    Logger.Info?.Print(
                        LogClass.Gpu,
                        gatherPatched
                            ? "[TOTK-C827-GATHER-BYPASS9] ACTIVE OpImageGather -> OpImageSampleImplicitLod, R replicated"
                            : "[TOTK-C827-GATHER-BYPASS9] PATTERN NOT FOUND");
                }

                // TOTK-C827-FRAGDEPTH-Z10
                if (shaderSource.Stage == ShaderStage.Fragment &&
                    string.Equals(
                        _codeHash,
                        "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                        StringComparison.OrdinalIgnoreCase) &&
                    Environment.GetEnvironmentVariable("RYUJINX_TOTK_C827_FRAGDEPTH_Z") == "1")
                {
                    spirv = PatchTotkC827FragDepthFromFragCoordZ(
                        spirv,
                        out bool fragDepthPatched);

                    Logger.Info?.Print(
                        LogClass.Gpu,
                        fragDepthPatched
                            ? "[TOTK-C827-FRAGDEPTH-Z10] ACTIVE FragDepth <- FragCoord.z"
                            : "[TOTK-C827-FRAGDEPTH-Z10] PATTERN NOT FOUND");
                }

                // TOTK-C827-LOCATION0-ZERO11
                if (shaderSource.Stage == ShaderStage.Fragment &&
                    string.Equals(
                        _codeHash,
                        "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                        StringComparison.OrdinalIgnoreCase) &&
                    Environment.GetEnvironmentVariable("RYUJINX_TOTK_C827_LOCATION0_ZERO") == "1")
                {
                    spirv = PatchTotkC827Location0Zero(
                        spirv,
                        out int locationStoresPatched);

                    Logger.Info?.Print(
                        LogClass.Gpu,
                        locationStoresPatched == 4
                            ? "[TOTK-C827-LOCATION0-ZERO11] ACTIVE Location0 <- 0.0 stores=4"
                            : $"[TOTK-C827-LOCATION0-ZERO11] PATCH INCOMPLETE stores={locationStoresPatched}");
                }

                // TOTK-C827-FARDEPTH-EPS16
                if (shaderSource.Stage == ShaderStage.Fragment &&
                    string.Equals(
                        _codeHash,
                        "c82797bafb11668589c15bbb3a52d39db43eb99210c803a9e7969f5032ea8eae",
                        StringComparison.OrdinalIgnoreCase))
                {
                    string farDepthMode =
                        Environment.GetEnvironmentVariable("RYUJINX_TOTK_C827_FARDEPTH_EPS");

                    if (string.Equals(
                        farDepthMode,
                        "tap3",
                        StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                        farDepthMode,
                        "both",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        bool patchBoth =
                            string.Equals(
                                farDepthMode,
                                "both",
                                StringComparison.OrdinalIgnoreCase);

                        spirv = PatchTotkC827FarDepthEpsilon(
                            spirv,
                            patchBoth,
                            out int comparisonsPatched);

                        Logger.Info?.Print(
                            LogClass.Gpu,
                            $"[TOTK-C827-FARDEPTH-EPS16] " +
                            $"mode={farDepthMode} " +
                            $"comparisons={comparisonsPatched} " +
                            $"thresholdBits=0x3F7FFFF8");
                    }
                }

                fixed (byte* pCode = spirv)
                {
                    ShaderModuleCreateInfo shaderModuleCreateInfo = new()
                    {
                        SType = StructureType.ShaderModuleCreateInfo,
                        CodeSize = (uint)spirv.Length,
                        PCode = (uint*)pCode,
                    };

                    api.CreateShaderModule(device, in shaderModuleCreateInfo, null, out _module).ThrowOnError();
                }

                CompileStatus = ProgramLinkStatus.Success;
            });
        }

        // TOTK-C827-GATHER-BYPASS9
        private static byte[] PatchTotkC827GatherBypass(byte[] spirv, out bool patched)
        {
            patched = false;

            if (spirv == null || spirv.Length < 20 || (spirv.Length & 3) != 0)
            {
                return spirv;
            }

            uint[] words = new uint[spirv.Length / 4];
            System.Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);

            int gatherIndex = -1;
            int shuffleIndex = -1;

            for (int i = 5; i < words.Length;)
            {
                uint first = words[i];
                int wordCount = (int)(first >> 16);
                uint opcode = first & 0xffff;

                if (wordCount <= 0 || i + wordCount > words.Length)
                {
                    return spirv;
                }

                // Exato OpImageGather do c827:
                // %179 = OpImageGather %5 %174 %178 %167 None
                if (opcode == 96 &&
                    wordCount == 7 &&
                    words[i + 1] == 5 &&
                    words[i + 2] == 179 &&
                    words[i + 3] == 174 &&
                    words[i + 4] == 178 &&
                    words[i + 5] == 167 &&
                    words[i + 6] == 0)
                {
                    gatherIndex = i;
                }

                // Exato shuffle logo após:
                // %180 = OpVectorShuffle %5 %179 %179 0 1 2 3
                if (opcode == 79 &&
                    wordCount == 9 &&
                    words[i + 1] == 5 &&
                    words[i + 2] == 180 &&
                    words[i + 3] == 179 &&
                    words[i + 4] == 179 &&
                    words[i + 5] == 0 &&
                    words[i + 6] == 1 &&
                    words[i + 7] == 2 &&
                    words[i + 8] == 3)
                {
                    shuffleIndex = i;
                }

                i += wordCount;
            }

            if (gatherIndex < 0 || shuffleIndex < 0 || shuffleIndex <= gatherIndex)
            {
                return spirv;
            }

            List<uint> output = new(words.Length - 1);

            for (int i = 0; i < 5; i++)
            {
                output.Add(words[i]);
            }

            for (int i = 5; i < words.Length;)
            {
                uint first = words[i];
                int wordCount = (int)(first >> 16);

                if (i == gatherIndex)
                {
                    // OpImageSampleImplicitLod:
                    // ResultType, ResultId, SampledImage, Coordinate, ImageOperands(None)
                    //
                    // Remove o operand "Component" usado apenas por OpImageGather.
                    output.Add((6u << 16) | 87u);
                    output.Add(words[i + 1]); // %5
                    output.Add(words[i + 2]); // %179
                    output.Add(words[i + 3]); // %174
                    output.Add(words[i + 4]); // %178
                    output.Add(0);            // ImageOperands None

                    i += wordCount;
                    continue;
                }

                if (i == shuffleIndex)
                {
                    // Replica R em XYZW para manter quatro resultados.
                    output.Add(words[i]);
                    output.Add(words[i + 1]); // result type
                    output.Add(words[i + 2]); // result id
                    output.Add(words[i + 3]); // vector 1
                    output.Add(words[i + 4]); // vector 2
                    output.Add(0);
                    output.Add(0);
                    output.Add(0);
                    output.Add(0);

                    i += wordCount;
                    continue;
                }

                for (int j = 0; j < wordCount; j++)
                {
                    output.Add(words[i + j]);
                }

                i += wordCount;
            }

            byte[] result = new byte[output.Count * 4];
            uint[] patchedWords = output.ToArray();
            System.Buffer.BlockCopy(patchedWords, 0, result, 0, result.Length);

            patched = true;
            return result;
        }

        // TOTK-C827-FRAGDEPTH-Z10
        private static byte[] PatchTotkC827FragDepthFromFragCoordZ(
            byte[] spirv,
            out bool patched)
        {
            patched = false;

            if (spirv == null ||
                spirv.Length < 20 ||
                (spirv.Length & 3) != 0)
            {
                return spirv;
            }

            uint[] words = new uint[spirv.Length / 4];
            System.Buffer.BlockCopy(
                spirv,
                0,
                words,
                0,
                spirv.Length);

            // Este dump exato tem bound=354, portanto
            // %354 e %355 estao livres.
            if (words[3] > 354)
            {
                return spirv;
            }

            int storeIndex = -1;

            for (int i = 5; i < words.Length;)
            {
                uint first = words[i];
                int wordCount = (int)(first >> 16);
                uint opcode = first & 0xffff;

                if (wordCount <= 0 ||
                    i + wordCount > words.Length)
                {
                    return spirv;
                }

                // Exato:
                // OpStore %37 %341
                if (opcode == 62 &&
                    wordCount == 3 &&
                    words[i + 1] == 37 &&
                    words[i + 2] == 341)
                {
                    if (storeIndex >= 0)
                    {
                        // Mais de um match = nao arriscar.
                        return spirv;
                    }

                    storeIndex = i;
                }

                i += wordCount;
            }

            if (storeIndex < 0)
            {
                return spirv;
            }

            List<uint> output = new(words.Length + 9);

            // Header.
            for (int i = 0; i < 5; i++)
            {
                output.Add(words[i]);
            }

            // Novo bound: IDs validos agora vao ate %355.
            output[3] = 356;

            for (int i = 5; i < words.Length;)
            {
                uint first = words[i];
                int wordCount = (int)(first >> 16);

                if (i == storeIndex)
                {
                    // %354 = OpAccessChain %168 %34 %243
                    //
                    // %168 = pointer Input float
                    // %34  = FragCoord
                    // %243 = constante int 2
                    output.Add((5u << 16) | 65u);
                    output.Add(168);
                    output.Add(354);
                    output.Add(34);
                    output.Add(243);

                    // %355 = OpLoad %4 %354
                    //
                    // %4 = float32
                    output.Add((4u << 16) | 61u);
                    output.Add(4);
                    output.Add(355);
                    output.Add(354);

                    // OpStore %37 %355
                    //
                    // %37 = FragDepth
                    output.Add((3u << 16) | 62u);
                    output.Add(37);
                    output.Add(355);

                    i += wordCount;
                    continue;
                }

                for (int j = 0; j < wordCount; j++)
                {
                    output.Add(words[i + j]);
                }

                i += wordCount;
            }

            uint[] patchedWords = output.ToArray();
            byte[] result = new byte[patchedWords.Length * 4];

            System.Buffer.BlockCopy(
                patchedWords,
                0,
                result,
                0,
                result.Length);

            patched = true;
            return result;
        }

        // TOTK-C827-LOCATION0-ZERO11
        private static byte[] PatchTotkC827Location0Zero(
            byte[] spirv,
            out int storesPatched)
        {
            storesPatched = 0;

            if (spirv == null ||
                spirv.Length < 20 ||
                (spirv.Length & 3) != 0)
            {
                return spirv;
            }

            uint[] words = new uint[spirv.Length / 4];

            System.Buffer.BlockCopy(
                spirv,
                0,
                words,
                0,
                spirv.Length);

            // Neste shader:
            //
            // %303 = constant FP32 0.0
            //
            // Os quatro AccessChains de Location 0 sao:
            // %342, %345, %348, %351
            //
            // Stores originais:
            // OpStore %342 %344
            // OpStore %345 %347
            // OpStore %348 %350
            // OpStore %351 %353
            //
            // Troca somente o valor armazenado por %303.

            uint[] ptrs = { 342, 345, 348, 351 };
            uint[] vals = { 344, 347, 350, 353 };

            for (int i = 5; i < words.Length;)
            {
                uint first = words[i];
                int wordCount = (int)(first >> 16);
                uint opcode = first & 0xffff;

                if (wordCount <= 0 ||
                    i + wordCount > words.Length)
                {
                    return spirv;
                }

                if (opcode == 62 && wordCount == 3)
                {
                    for (int n = 0; n < 4; n++)
                    {
                        if (words[i + 1] == ptrs[n] &&
                            words[i + 2] == vals[n])
                        {
                            words[i + 2] = 303;
                            storesPatched++;
                            break;
                        }
                    }
                }

                i += wordCount;
            }

            if (storesPatched != 4)
            {
                storesPatched = 0;
                return spirv;
            }

            byte[] result = new byte[spirv.Length];

            System.Buffer.BlockCopy(
                words,
                0,
                result,
                0,
                result.Length);

            return result;
        }

        // TOTK-C827-FARDEPTH-EPS16
        private static byte[] PatchTotkC827FarDepthEpsilon(
            byte[] spirv,
            bool patchBoth,
            out int comparisonsPatched)
        {
            comparisonsPatched = 0;

            if (spirv == null ||
                spirv.Length < 20 ||
                (spirv.Length & 3) != 0)
            {
                return spirv;
            }

            uint[] words = new uint[spirv.Length / 4];

            System.Buffer.BlockCopy(
                spirv,
                0,
                words,
                0,
                spirv.Length);

            // Dump original: bound=354, IDs validos 0..353.
            // Usa %354 para a nova constante FP32.
            if (words[3] != 354)
            {
                return spirv;
            }

            int firstFunction = -1;
            int compareMaxIndex = -1;
            int compareTap3Index = -1;

            for (int i = 5; i < words.Length;)
            {
                uint first = words[i];
                int wordCount = (int)(first >> 16);
                uint opcode = first & 0xffff;

                if (wordCount <= 0 ||
                    i + wordCount > words.Length)
                {
                    return spirv;
                }

                if (opcode == 54 && firstFunction < 0)
                {
                    firstFunction = i;
                }

                // %271 = OpFOrdEqual %120 %270 %268
                if (opcode == 180 &&
                    wordCount == 5 &&
                    words[i + 1] == 120 &&
                    words[i + 2] == 271 &&
                    words[i + 3] == 270 &&
                    words[i + 4] == 268)
                {
                    compareMaxIndex = i;
                }

                // %296 = OpFOrdEqual %120 %295 %268
                // %295 = tap3 carregado de %60.
                if (opcode == 180 &&
                    wordCount == 5 &&
                    words[i + 1] == 120 &&
                    words[i + 2] == 296 &&
                    words[i + 3] == 295 &&
                    words[i + 4] == 268)
                {
                    compareTap3Index = i;
                }

                i += wordCount;
            }

            if (firstFunction < 0 ||
                compareTap3Index < 0 ||
                (patchBoth && compareMaxIndex < 0))
            {
                return spirv;
            }

            List<uint> output = new(words.Length + 4);

            for (int i = 0; i < 5; i++)
            {
                output.Add(words[i]);
            }

            // Novo ID maximo = %354, portanto bound=355.
            output[3] = 355;

            for (int i = 5; i < words.Length;)
            {
                uint first = words[i];
                int wordCount = (int)(first >> 16);

                if (i == firstFunction)
                {
                    // %354 = OpConstant %4 0x3F7FFFF8
                    //
                    // 8 ULP abaixo de FP32 1.0:
                    // aproximadamente 0.999999523.
                    output.Add((4u << 16) | 43u);
                    output.Add(4);
                    output.Add(354);
                    output.Add(0x3F7FFFF8u);
                }

                bool patchThis =
                    i == compareTap3Index ||
                    (patchBoth && i == compareMaxIndex);

                if (patchThis)
                {
                    // Mantem ResultType, ResultId e Operand1.
                    //
                    // OpFOrdEqual(value, 1.0)
                    // vira:
                    // OpFOrdGreaterThanEqual(value, nearOne)
                    //
                    // Opcode 190 = OpFOrdGreaterThanEqual.
                    output.Add((5u << 16) | 190u);
                    output.Add(words[i + 1]);
                    output.Add(words[i + 2]);
                    output.Add(words[i + 3]);
                    output.Add(354);

                    comparisonsPatched++;
                    i += wordCount;
                    continue;
                }

                for (int j = 0; j < wordCount; j++)
                {
                    output.Add(words[i + j]);
                }

                i += wordCount;
            }

            int expected = patchBoth ? 2 : 1;

            if (comparisonsPatched != expected)
            {
                comparisonsPatched = 0;
                return spirv;
            }

            uint[] patchedWords = output.ToArray();
            byte[] result = new byte[patchedWords.Length * 4];

            System.Buffer.BlockCopy(
                patchedWords,
                0,
                result,
                0,
                result.Length);

            return result;
        }

        private unsafe static byte[] GlslToSpirv(string glsl, ShaderStage stage)
        {
            Shaderc api = Shaderc.GetApi();
            Compiler* compiler = api.CompilerInitialize();
            CompileOptions* options = api.CompileOptionsInitialize();

            api.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
            api.CompileOptionsSetTargetSpirv(options, SpirvVersion.Shaderc15);
            api.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, Vk.Version12);

            CompilationResult* scr = api.CompileIntoSpv(compiler, glsl, (nuint)glsl.Length, GetShaderCShaderStage(stage), "Ryu", "main", options);

            CompilationStatus status = api.ResultGetCompilationStatus(scr);

            if (status != CompilationStatus.Success)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Shader compilation error: {status} {api.ResultGetErrorMessageS(scr)}");

                return null;
            }

            Span<byte> spirvBytes = new(api.ResultGetBytes(scr), (int)api.ResultGetLength(scr));

            byte[] code = new byte[(spirvBytes.Length + 3) & ~3];

            spirvBytes.CopyTo(code.AsSpan()[..spirvBytes.Length]);

            api.CompilerRelease(compiler);
            api.CompileOptionsRelease(options);

            return code;
        }

        private static ShaderKind GetShaderCShaderStage(ShaderStage stage)
        {
            switch (stage)
            {
                case ShaderStage.Vertex:
                    return ShaderKind.GlslVertexShader;
                case ShaderStage.Geometry:
                    return ShaderKind.GlslGeometryShader;
                case ShaderStage.TessellationControl:
                    return ShaderKind.GlslTessControlShader;
                case ShaderStage.TessellationEvaluation:
                    return ShaderKind.GlslTessEvaluationShader;
                case ShaderStage.Fragment:
                    return ShaderKind.GlslFragmentShader;
                case ShaderStage.Compute:
                    return ShaderKind.GlslComputeShader;
            }

            Logger.Debug?.Print(LogClass.Gpu, $"Invalid {nameof(ShaderStage)} enum value: {stage}.");

            return ShaderKind.GlslVertexShader;
        }

        public unsafe PipelineShaderStageCreateInfo GetInfo()
        {
            return new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = _stage,
                Module = _module,
                PName = (byte*)_ptrMainEntryPointName,
            };
        }

        public void WaitForCompile()
        {
            CompileTask.Wait();
        }

        public unsafe void Dispose()
        {
            if (!_disposed)
            {
                _api.DestroyShaderModule(_device, _module, null);
                _disposed = true;
            }
        }
    }
}
