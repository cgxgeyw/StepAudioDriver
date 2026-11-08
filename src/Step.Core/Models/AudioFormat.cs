namespace Step.Core.Models;

/// <summary>
/// 音频格式配置
/// </summary>
public class AudioFormat
{
    /// <summary>
    /// 采样率 (Hz)
    /// </summary>
    public int SampleRate { get; set; }

    /// <summary>
    /// 位深度 (bits)
    /// </summary>
    public int BitDepth { get; set; }

    /// <summary>
    /// 声道数（SK02 固定为 2）
    /// </summary>
    public int Channels { get; set; } = 2;

    /// <summary>
    /// 字符串表示
    /// </summary>
    public override string ToString() => $"{SampleRate}Hz, {BitDepth}-bit";

    public static class StandardFormats
    {
        public static AudioFormat PCM_44100_16 => new() { SampleRate = 44100, BitDepth = 16 };
        public static AudioFormat PCM_44100_24 => new() { SampleRate = 44100, BitDepth = 24 };
        public static AudioFormat PCM_48000_16 => new() { SampleRate = 48000, BitDepth = 16 };
        public static AudioFormat PCM_48000_24 => new() { SampleRate = 48000, BitDepth = 24 };
        public static AudioFormat PCM_96000_24 => new() { SampleRate = 96000, BitDepth = 24 };
        public static AudioFormat PCM_192000_24 => new() { SampleRate = 192000, BitDepth = 24 };
    }
}
