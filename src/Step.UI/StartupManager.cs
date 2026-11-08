namespace Step.UI;

using Microsoft.Win32;

/// <summary>
/// 管理 HKCU\...\Run 中的开机自启动项。按用户生效，不需要管理员权限。
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "StepAudioDriver";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled, string executablePath)
    {
        if (enabled && string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("无法确定程序路径，不能设置开机自启动。");

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法写入当前用户的启动项。");

        if (enabled)
        {
            key.SetValue(ValueName, $"\"{executablePath}\" --autostart", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
