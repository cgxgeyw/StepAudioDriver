namespace SK02.Core.Audio;

using NAudio.CoreAudioApi;
using NAudio.Wave;

/// <summary>
/// 未启动主处理链路时，用来单独播放方向测试音的轻量播放器。
///
/// 主链路运行时请改用 <see cref="RealTimeAudioProcessor.PlayTestTone"/>，
/// 把测试音混进同一条输出，避免两个 WASAPI 流抢设备。
/// </summary>
public sealed class DirectionalTestPlayer : IDisposable
{
    private WasapiOut? _output;
    private bool _disposed;

    /// <summary>
    /// 播放一段已渲染好的双耳立体声
    /// </summary>
    public void Play(float[] stereo, int sampleRate)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DirectionalTestPlayer));

        Stop();

        var device = FindOutputDevice()
            ?? throw new InvalidOperationException("没有可用的音频输出设备");

        AudioClientShareMode shareMode = AudioClientShareMode.Shared;
        WasapiOut? output = null;

        try
        {
            // 优先 32-bit float；设备不接受时退回 16-bit PCM
            var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
            var provider = CreateProvider(format, stereo, isFloat: true);

            output = new WasapiOut(device, shareMode, false, 60);
            output.Init(provider);
        }
        catch
        {
            try { output?.Dispose(); }
            catch { /* ignore */ }

            var format = new WaveFormat(sampleRate, 16, 2);
            var provider = CreateProvider(format, stereo, isFloat: false);

            output = new WasapiOut(device, shareMode, false, 60);
            output.Init(provider);
        }

        var current = output;

        current.PlaybackStopped += (_, _) =>
        {
            try { current.Dispose(); }
            catch { /* ignore */ }

            if (ReferenceEquals(_output, current))
                _output = null;
        };

        _output = current;
        current.Play();
    }

    /// <summary>
    /// 停止当前试听音
    /// </summary>
    public void Stop()
    {
        var output = _output;
        _output = null;

        if (output == null)
            return;

        try { output.Stop(); }
        catch { /* ignore */ }

        try { output.Dispose(); }
        catch { /* ignore */ }
    }

    private static BufferedWaveProvider CreateProvider(WaveFormat format, float[] stereo, bool isFloat)
    {
        int bytesPerSample = isFloat ? 4 : 2;
        var buffer = new byte[stereo.Length * bytesPerSample];

        if (isFloat)
        {
            Buffer.BlockCopy(stereo, 0, buffer, 0, buffer.Length);
        }
        else
        {
            for (int i = 0; i < stereo.Length; i++)
            {
                short sample = (short)(Math.Clamp(stereo[i], -1f, 1f) * 32767f);
                buffer[i * 2] = (byte)(sample & 0xFF);
                buffer[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }

        var provider = new BufferedWaveProvider(format)
        {
            BufferLength = Math.Max(format.AverageBytesPerSecond, buffer.Length),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };

        provider.AddSamples(buffer, 0, buffer.Length);
        return provider;
    }

    private static MMDevice? FindOutputDevice()
    {
        using var enumerator = new MMDeviceEnumerator();

        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .ToList();

        var hardware = devices.FirstOrDefault(d =>
            d.FriendlyName.Contains("SK02", StringComparison.OrdinalIgnoreCase) ||
            d.FriendlyName.Contains("Fosi", StringComparison.OrdinalIgnoreCase) ||
            d.FriendlyName.Contains("Bravo-HD", StringComparison.OrdinalIgnoreCase));

        if (hardware != null)
            return hardware;

        var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        if (defaultDevice != null && !IsVirtualCable(defaultDevice.FriendlyName))
            return defaultDevice;

        return devices.FirstOrDefault(d => !IsVirtualCable(d.FriendlyName));
    }

    private static bool IsVirtualCable(string friendlyName)
    {
        return friendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("VB-CABLE", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
    }
}
