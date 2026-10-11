using Avalonia.Controls;
using Avalonia.Interactivity;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Diagnostics;
using Ryujinx.UI.Common.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.Ava.UI.Windows
{
    public partial class GameSettingsWindow : StyleableWindow
    {
        private const ulong TotkTitleId = 0x0100F2C0115B6000UL;

        private readonly ulong _titleId;
        private readonly string _titleName;

        private readonly List<ResolutionScaleOption> _resolutionOptions =
        [
            new("0.5x", -1, 0.5f),
            new("0.75x", -1, 0.75f),
            new("1x", 1, 1.0f),
            new("2x", 2, 2.0f),
            new("3x", 3, 3.0f),
            new("4x", 4, 4.0f),
        ];

        private readonly List<AnisotropyOption> _anisotropyOptions =
        [
            new("Auto", -1.0f),
            new("4x", 4.0f),
            new("8x", 8.0f),
            new("16x", 16.0f),
        ];

        private CheckBox EnabledCheckBoxControl => this.FindControl<CheckBox>("EnabledCheckBox");
        private Panel SettingsPanelControl => this.FindControl<Panel>("SettingsPanel");

        private CheckBox UseNceCheckBoxControl => this.FindControl<CheckBox>("UseNceCheckBox");
        private CheckBox NceProtectionRestoreCheckBoxControl => this.FindControl<CheckBox>("NceProtectionRestoreCheckBox");
        private CheckBox EnablePtcCheckBoxControl => this.FindControl<CheckBox>("EnablePtcCheckBox");
        private CheckBox MemorySlackCheckBoxControl => this.FindControl<CheckBox>("MemorySlackCheckBox");
        private CheckBox DockedCheckBoxControl => this.FindControl<CheckBox>("DockedCheckBox");

        private ComboBox VSyncComboBoxControl => this.FindControl<ComboBox>("VSyncComboBox");
        private ComboBox ResolutionScaleComboBoxControl => this.FindControl<ComboBox>("ResolutionScaleComboBox");
        private ComboBox AnisotropyComboBoxControl => this.FindControl<ComboBox>("AnisotropyComboBox");

        private CheckBox ShaderCacheCheckBoxControl => this.FindControl<CheckBox>("ShaderCacheCheckBox");
        private CheckBox ShaderLoopSafetyCheckBoxControl => this.FindControl<CheckBox>("ShaderLoopSafetyCheckBox");
        private CheckBox TextureRecompressionCheckBoxControl => this.FindControl<CheckBox>("TextureRecompressionCheckBox");
        private CheckBox MacroHleCheckBoxControl => this.FindControl<CheckBox>("MacroHleCheckBox");
        private CheckBox RelaxedDmaCheckBoxControl => this.FindControl<CheckBox>("RelaxedDmaCheckBox");
        private CheckBox VtgComputeCheckBoxControl => this.FindControl<CheckBox>("VtgComputeCheckBox");
        private CheckBox RejectUnmappedCheckBoxControl => this.FindControl<CheckBox>("RejectUnmappedCheckBox");
        private CheckBox AsyncLarge3DCheckBoxControl => this.FindControl<CheckBox>("AsyncLarge3DCheckBox");
        private CheckBox GpuOptimizationsCheckBoxControl => this.FindControl<CheckBox>("GpuOptimizationsCheckBox");
        private CheckBox Experimental2DCheckBoxControl => this.FindControl<CheckBox>("Experimental2DCheckBox");
        private CheckBox NifmOfflineCheckBoxControl => this.FindControl<CheckBox>("NifmOfflineCheckBox");
        private ComboBox DrawFlushComboBoxControl => this.FindControl<ComboBox>("DrawFlushComboBox");
        private ComboBox BufferReadbackComboBoxControl => this.FindControl<ComboBox>("BufferReadbackComboBox");
        private ComboBox StrictSyncComboBoxControl => this.FindControl<ComboBox>("StrictSyncComboBox");

        public GameSettingsWindow()
        {
            InitializeComponent();

            _titleId = 0;
            _titleName = "Configurações do jogo";

            InitializeControls();
        }

        public GameSettingsWindow(ulong titleId, string titleName)
        {
            _titleId = titleId;
            _titleName = string.IsNullOrWhiteSpace(titleName)
                ? "Configurações do jogo"
                : titleName;

            InitializeComponent();
            InitializeControls();
        }

        private void InitializeControls()
        {
            Title = $"Ryujinx {Program.Version} - Configurações do jogo";

            TextBlock gameName = this.FindControl<TextBlock>("GameNameText");
            TextBlock titleId = this.FindControl<TextBlock>("TitleIdText");

            gameName.Text = _titleName;
            titleId.Text = _titleId == 0
                ? "Title ID indisponível"
                : _titleId.ToString("X16");

            VSyncComboBoxControl.ItemsSource = Enum.GetValues<VSyncMode>();
            ResolutionScaleComboBoxControl.ItemsSource = _resolutionOptions;
            AnisotropyComboBoxControl.ItemsSource = _anisotropyOptions;
            DrawFlushComboBoxControl.ItemsSource = Enum.GetValues<DrawFlushMode>();
            BufferReadbackComboBoxControl.ItemsSource = Enum.GetValues<BufferReadbackMode>();
            StrictSyncComboBoxControl.ItemsSource = Enum.GetValues<StrictSyncMode>();

            LoadProfile();
        }

        private void LoadProfile()
        {
            ConfigurationState config = ConfigurationState.Instance;
            GameSettingsProfile profile = GameSettingsProfileStore.Load(_titleId);

            bool enabled = _titleId != 0 && profile.Enabled;

            EnabledCheckBoxControl.IsChecked = enabled;
            EnabledCheckBoxControl.IsEnabled = _titleId != 0;

            UseNceCheckBoxControl.IsChecked =
                enabled && profile.UseNce.HasValue
                    ? profile.UseNce.Value
                    : config.System.UseHypervisor.Value;

            // NCE-PROT1 is per-game only. Missing/old profile means OFF.
            NceProtectionRestoreCheckBoxControl.IsChecked =
                enabled && profile.NceProtectionRestore == true;

            EnablePtcCheckBoxControl.IsChecked =
                enabled && profile.EnablePtc.HasValue
                    ? profile.EnablePtc.Value
                    : config.System.EnablePtc.Value;

            MemorySlackCheckBoxControl.IsChecked =
                enabled && profile.MemorySlackEnabled.HasValue
                    ? profile.MemorySlackEnabled.Value
                    : _titleId == TotkTitleId;

            DockedCheckBoxControl.IsChecked =
                enabled && profile.EnableDockedMode.HasValue
                    ? profile.EnableDockedMode.Value
                    : config.System.EnableDockedMode.Value;

            VSyncMode effectiveVSync =
                enabled && profile.VSyncMode.HasValue
                    ? profile.VSyncMode.Value
                    : config.Graphics.VSyncMode.Value;

            VSyncComboBoxControl.SelectedItem = effectiveVSync;

            int resScale =
                enabled && profile.ResScale.HasValue
                    ? profile.ResScale.Value
                    : config.Graphics.ResScale.Value;

            float resScaleCustom =
                enabled && profile.ResScaleCustom.HasValue
                    ? profile.ResScaleCustom.Value
                    : config.Graphics.ResScaleCustom.Value;

            ResolutionScaleOption selectedResolution =
                _resolutionOptions.FirstOrDefault(option =>
                    option.Setting == resScale &&
                    (resScale != -1 || Math.Abs(option.Custom - resScaleCustom) < 0.01f))
                ?? _resolutionOptions.First(option => option.Setting == 1);

            ResolutionScaleComboBoxControl.SelectedItem = selectedResolution;

            float anisotropy =
                enabled && profile.MaxAnisotropy.HasValue
                    ? profile.MaxAnisotropy.Value
                    : config.Graphics.MaxAnisotropy.Value;

            AnisotropyComboBoxControl.SelectedItem =
                _anisotropyOptions.FirstOrDefault(option =>
                    Math.Abs(option.Value - anisotropy) < 0.01f)
                ?? _anisotropyOptions[0];

            ShaderCacheCheckBoxControl.IsChecked =
                enabled && profile.EnableShaderCache.HasValue
                    ? profile.EnableShaderCache.Value
                    : config.Graphics.EnableShaderCache.Value;

            ShaderLoopSafetyCheckBoxControl.IsChecked =
                enabled && profile.EnableShaderLoopSafety.HasValue
                    ? profile.EnableShaderLoopSafety.Value
                    : false;

            TextureRecompressionCheckBoxControl.IsChecked =
                enabled && profile.EnableTextureRecompression.HasValue
                    ? profile.EnableTextureRecompression.Value
                    : config.Graphics.EnableTextureRecompression.Value;

            MacroHleCheckBoxControl.IsChecked =
                enabled && profile.EnableMacroHLE.HasValue
                    ? profile.EnableMacroHLE.Value
                    : config.Graphics.EnableMacroHLE.Value;

            RelaxedDmaCheckBoxControl.IsChecked =
                enabled && profile.RelaxedDmaCopy.HasValue
                    ? profile.RelaxedDmaCopy.Value
                    : false;

            VtgComputeCheckBoxControl.IsChecked =
                enabled && profile.EnableVtgViaCompute.HasValue
                    ? profile.EnableVtgViaCompute.Value
                    : true;

            RejectUnmappedCheckBoxControl.IsChecked =
                enabled && profile.RejectUnmappedTextureDescriptors.HasValue
                    ? profile.RejectUnmappedTextureDescriptors.Value
                    : false;

            AsyncLarge3DCheckBoxControl.IsChecked =
                enabled && profile.AsyncLarge3DTextureUpload.HasValue
                    ? profile.AsyncLarge3DTextureUpload.Value
                    : true;

            GpuOptimizationsCheckBoxControl.IsChecked =
                enabled && profile.GpuOptimizations.HasValue
                    ? profile.GpuOptimizations.Value
                    : true;

            Experimental2DCheckBoxControl.IsChecked =
                enabled && profile.ExperimentalGameFixesEnabled.HasValue
                    ? profile.ExperimentalGameFixesEnabled.Value
                    : false;

            NifmOfflineCheckBoxControl.IsChecked =
                enabled && profile.NifmOfflineEnabled.HasValue
                    ? profile.NifmOfflineEnabled.Value
                    : false;

            DrawFlushComboBoxControl.SelectedItem =
                enabled && profile.DrawFlushMode.HasValue
                    ? profile.DrawFlushMode.Value
                    : NeoGraphicsSettings.DrawFlushGlobalMode;

            BufferReadbackComboBoxControl.SelectedItem =
                enabled && profile.BufferReadbackMode.HasValue
                    ? profile.BufferReadbackMode.Value
                    : NeoGraphicsSettings.BufferReadbackGlobalMode;

            StrictSyncComboBoxControl.SelectedItem =
                enabled && profile.StrictSyncMode.HasValue
                    ? profile.StrictSyncMode.Value
                    : NeoGraphicsSettings.StrictSyncGlobalMode;

            UpdateEnabledState();
        }

        private void UpdateEnabledState()
        {
            SettingsPanelControl.IsEnabled =
                _titleId != 0 &&
                EnabledCheckBoxControl.IsChecked == true;
        }

        private void Enabled_Click(object sender, RoutedEventArgs args)
        {
            UpdateEnabledState();
        }

        private void Save_Click(object sender, RoutedEventArgs args)
        {
            if (_titleId == 0)
            {
                Close();
                return;
            }

            bool enabled = EnabledCheckBoxControl.IsChecked == true;

            GameSettingsProfile profile = GameSettingsProfileStore.Load(_titleId);
            profile.Enabled = enabled;

            if (enabled)
            {
                profile.UseNce = UseNceCheckBoxControl.IsChecked == true;
                profile.NceProtectionRestore = NceProtectionRestoreCheckBoxControl.IsChecked == true;
                profile.EnablePtc = EnablePtcCheckBoxControl.IsChecked == true;
                profile.MemorySlackEnabled = MemorySlackCheckBoxControl.IsChecked == true;
                profile.EnableDockedMode = DockedCheckBoxControl.IsChecked == true;

                profile.VSyncMode =
                    VSyncComboBoxControl.SelectedItem is VSyncMode vSync
                        ? vSync
                        : ConfigurationState.Instance.Graphics.VSyncMode.Value;

                if (ResolutionScaleComboBoxControl.SelectedItem is ResolutionScaleOption resolution)
                {
                    profile.ResScale = resolution.Setting;
                    profile.ResScaleCustom = resolution.Custom;
                }

                if (AnisotropyComboBoxControl.SelectedItem is AnisotropyOption anisotropy)
                {
                    profile.MaxAnisotropy = anisotropy.Value;
                }

                profile.EnableShaderCache = ShaderCacheCheckBoxControl.IsChecked == true;
                profile.EnableShaderLoopSafety = ShaderLoopSafetyCheckBoxControl.IsChecked == true;
                profile.EnableTextureRecompression = TextureRecompressionCheckBoxControl.IsChecked == true;
                profile.EnableMacroHLE = MacroHleCheckBoxControl.IsChecked == true;

                profile.RelaxedDmaCopy =
                    RelaxedDmaCheckBoxControl.IsChecked == true;

                profile.EnableVtgViaCompute =
                    VtgComputeCheckBoxControl.IsChecked == true;

                profile.RejectUnmappedTextureDescriptors =
                    RejectUnmappedCheckBoxControl.IsChecked == true;

                profile.AsyncLarge3DTextureUpload =
                    AsyncLarge3DCheckBoxControl.IsChecked == true;

                profile.GpuOptimizations =
                    GpuOptimizationsCheckBoxControl.IsChecked == true;

                profile.ExperimentalGameFixesEnabled =
                    Experimental2DCheckBoxControl.IsChecked == true;

                profile.NifmOfflineEnabled =
                    NifmOfflineCheckBoxControl.IsChecked == true;

                profile.DrawFlushMode =
                    DrawFlushComboBoxControl.SelectedItem is DrawFlushMode drawFlush
                        ? drawFlush
                        : NeoGraphicsSettings.DrawFlushGlobalMode;

                profile.BufferReadbackMode =
                    BufferReadbackComboBoxControl.SelectedItem is BufferReadbackMode readback
                        ? readback
                        : NeoGraphicsSettings.BufferReadbackGlobalMode;

                profile.StrictSyncMode =
                    StrictSyncComboBoxControl.SelectedItem is StrictSyncMode strictSync
                        ? strictSync
                        : NeoGraphicsSettings.StrictSyncGlobalMode;
            }

            GameSettingsProfileStore.Save(_titleId, profile);
            Close();
        }

        private void Reset_Click(object sender, RoutedEventArgs args)
        {
            if (_titleId != 0)
            {
                GameSettingsProfileStore.Reset(_titleId);
            }

            LoadProfile();
        }

        private void Cancel_Click(object sender, RoutedEventArgs args)
        {
            Close();
        }

        private sealed class ResolutionScaleOption
        {
            public string Label { get; }
            public int Setting { get; }
            public float Custom { get; }

            public ResolutionScaleOption(string label, int setting, float custom)
            {
                Label = label;
                Setting = setting;
                Custom = custom;
            }

            public override string ToString() => Label;
        }

        private sealed class AnisotropyOption
        {
            public string Label { get; }
            public float Value { get; }

            public AnisotropyOption(string label, float value)
            {
                Label = label;
                Value = value;
            }

            public override string ToString() => Label;
        }
    }
}
