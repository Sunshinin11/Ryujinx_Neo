using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Diagnostics;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ryujinx.UI.Common.Configuration
{
    public sealed class GameSettingsProfile
    {
        public bool Enabled { get; set; }

        // CPU / emulação
        public bool? UseNce { get; set; }
        public bool? NceProtectionRestore { get; set; }
        public bool? EnablePtc { get; set; }
        public bool? MemorySlackEnabled { get; set; }

        // Sistema / gráficos já seguros para aplicação por jogo.
        public VSyncMode? VSyncMode { get; set; }
        public bool? EnableDockedMode { get; set; }

        // Mantidos no formato desde já para a próxima etapa da UI.
        public bool? EnableShaderCache { get; set; }
        public bool? EnableShaderLoopSafety { get; set; }
        public bool? EnableTextureRecompression { get; set; }
        public bool? EnableMacroHLE { get; set; }
        public int? ResScale { get; set; }
        public float? ResScaleCustom { get; set; }
        public float? MaxAnisotropy { get; set; }

        // NEOV2 — opções adicionais por jogo.
        public bool? RelaxedDmaCopy { get; set; }
        public bool? EnableVtgViaCompute { get; set; }
        public bool? RejectUnmappedTextureDescriptors { get; set; }
        public bool? AsyncLarge3DTextureUpload { get; set; }

        // Agrupa as otimizações do commit 2a1c968.
        public bool? GpuOptimizations { get; set; }

        // Usa timeline semaphore para permitir batching de submits neste jogo.
        public bool? TurnipTimelineBatching { get; set; }

        // Correções específicas do MARVEL Cosmic Invasion / caminho 2D.
        public bool? ExperimentalGameFixesEnabled { get; set; }

        // Força NIFM sem acesso à internet.
        public bool? NifmOfflineEnabled { get; set; }

        // Overrides do backend.
        public DrawFlushMode? DrawFlushMode { get; set; }
        public BufferReadbackMode? BufferReadbackMode { get; set; }
        public StrictSyncMode? StrictSyncMode { get; set; }
    }

    public static class GameSettingsProfileStore
    {
        private const string FileName = "neo-profile.json";

        public static string GetPath(ulong titleId)
        {
            return Path.Combine(
                AppDataManager.GamesDirPath,
                titleId.ToString("x16"),
                FileName);
        }

        public static GameSettingsProfile Load(ulong titleId)
        {
            if (titleId == 0)
            {
                return new GameSettingsProfile();
            }

            string path = GetPath(titleId);

            if (!File.Exists(path))
            {
                return new GameSettingsProfile();
            }

            try
            {
                GameSettingsProfile profile =
                    JsonSerializer.Deserialize(
                        File.ReadAllText(path),
                        GameSettingsProfileJsonContext.Default.GameSettingsProfile);

                return profile ?? new GameSettingsProfile();
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(
                    LogClass.Application,
                    $"[NEOV2-PROFILE] Failed to load profile for {titleId:X16}: {exception.Message}");

                return new GameSettingsProfile();
            }
        }

        public static void Save(ulong titleId, GameSettingsProfile profile)
        {
            if (titleId == 0 || profile == null)
            {
                return;
            }

            string path = GetPath(titleId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(profile, GameSettingsProfileJsonContext.Default.GameSettingsProfile));
        }

        public static void Reset(ulong titleId)
        {
            if (titleId == 0)
            {
                return;
            }

            string path = GetPath(titleId);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(GameSettingsProfile))]
    internal partial class GameSettingsProfileJsonContext : JsonSerializerContext
    {
    }

}
