namespace SK02.Core.Models;

/// <summary>
/// 音频输出设备信息
/// </summary>
public class AudioDevice
{
    /// <summary>
    /// 设备唯一标识符
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 设备名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 设备描述
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 是否为默认设备
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// 支持的音频格式列表
    /// </summary>
    public List<AudioFormat> SupportedFormats { get; set; } = [];

    /// <summary>
    /// 字符串表示
    /// </summary>
    public override string ToString() => $"{Name} ({Description})";

    public override bool Equals(object? obj)
    {
        if (obj is AudioDevice other)
            return Id == other.Id;
        return false;
    }

    public override int GetHashCode() => Id.GetHashCode();
}
