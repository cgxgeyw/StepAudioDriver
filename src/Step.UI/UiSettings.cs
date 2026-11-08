using System.IO;
using System.Text.Json;

namespace Step.UI;

/// <summary>
/// 记住上次使用的预设、EQ、设备、格式和缓冲档位。
/// </summary>
public sealed class UiSettings
{
    public string? PresetId { get; set; }
    public double[]? EqBands { get; set; }
    public double[]? EqFrequencies { get; set; }
    public double[]? EqQValues { get; set; }

    public bool? HrtfEnabled { get; set; }
    public double? HrtfIntensity { get; set; }
    public double? SurroundWidth { get; set; }
    public bool? DspBypass { get; set; }
    public bool? SetDefaultDevice { get; set; }

    public double? LowShelfFrequency { get; set; }
    public double? LowShelfGain { get; set; }
    public double? LowShelfQ { get; set; }
    public double? HighShelfFrequency { get; set; }
    public double? HighShelfGain { get; set; }
    public double? HighShelfQ { get; set; }

    public bool? OverlayVisible { get; set; }
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }

    public string? CloseAction { get; set; }
    public bool? CloseActionRemembered { get; set; }

    public string? CaptureDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? OutputFormatTag { get; set; }
    public int? BufferMilliseconds { get; set; }
}

public static class UiSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StepAudioDriver",
        "ui-settings.json");

    private static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SK02AudioDriver",
        "ui-settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static UiSettings? Load()
    {
        try
        {
            // 从旧安装目录迁移一次设置
            if (!File.Exists(FilePath) && File.Exists(LegacyFilePath))
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.Copy(LegacyFilePath, FilePath);
            }

            if (!File.Exists(FilePath))
                return null;

            return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(UiSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // 设置保存失败不影响退出
        }
    }
}
