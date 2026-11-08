namespace Step.Core.Audio;

using NAudio.Wave;

/// <summary>
/// NAudio <see cref="WaveFormat"/> 的辅助判断。
///
/// WASAPI 设备经常返回 WAVE_FORMAT_EXTENSIBLE，此时 <see cref="WaveFormat.Encoding"/>
/// 是 Extensible 而不是 IeeeFloat；如果不做归一化，32-bit float 会被当成 32-bit PCM，
/// 转换结果会是噪声。这里统一处理。
/// </summary>
internal static class WaveFormatExtensions
{
    /// <summary>
    /// 是否为 32-bit IEEE float（含 WAVE_FORMAT_EXTENSIBLE 包装）
    /// </summary>
    public static bool IsFloat(this WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            return true;

        return format is WaveFormatExtensible extensible &&
               extensible.ToStandardWaveFormat().Encoding == WaveFormatEncoding.IeeeFloat;
    }

    /// <summary>
    /// 生成相同采样率/位深的立体声格式
    /// </summary>
    public static WaveFormat ToStereo(this WaveFormat format)
    {
        return format.IsFloat()
            ? WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, 2)
            : new WaveFormat(format.SampleRate, format.BitsPerSample, 2);
    }
}
