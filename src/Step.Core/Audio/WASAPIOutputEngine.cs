namespace Step.Core.Audio;

using NAudio.CoreAudioApi;
using NAudio.Wave;
using Step.Core.Models;

/// <summary>
/// WASAPI 输出引擎
///
/// 负责把处理后的立体声浮点样本送到 SK02（或用户选择的输出设备）。
/// 初始化时会按「独占 → 共享 → 降级格式」顺序尝试，直到设备接受为止。
/// </summary>
public class WASAPIOutputEngine : IDisposable
{
    private WasapiOut? _wasapiOut;
    private BufferedWaveProvider? _waveProvider;
    private MMDevice? _outputDevice;
    private WaveFormat? _activeFormat;

    private readonly AudioDevice _device;
    private bool _isPlaying;
    private bool _disposed;

    // 因缓冲满而丢弃的字节数（两个设备时钟漂移时会出现）
    private long _discardedBytes;

    /// <summary>
    /// 输出错误事件
    /// </summary>
    public event EventHandler<Exception>? OutputError;

    /// <summary>
    /// 输出播放停止（设备被拔出、驱动重置、被其它程序抢占等）
    /// </summary>
    public event EventHandler<Exception?>? PlaybackStopped;

    public WASAPIOutputEngine(AudioDevice device)
    {
        _device = device;
    }

    /// <summary>
    /// 当前实际打开的输出格式
    /// </summary>
    public WaveFormat? OutputFormat => _activeFormat;

    /// <summary>
    /// 当前输出设备名称
    /// </summary>
    public string? OutputDeviceName => _outputDevice?.FriendlyName;

    /// <summary>
    /// 是否成功以独占模式打开
    /// </summary>
    public bool IsExclusive { get; private set; }

    /// <summary>
    /// 是否正在播放
    /// </summary>
    public bool IsPlaying => _isPlaying;

    /// <summary>
    /// 初始化输出设备
    /// </summary>
    /// <param name="preferredFormat">首选格式（立体声）</param>
    /// <param name="preferExclusive">是否优先独占（独占会踢掉其它播放器）</param>
    /// <param name="bufferMilliseconds">输出缓冲长度（毫秒）</param>
    public void Initialize(WaveFormat preferredFormat, bool preferExclusive = true, int bufferMilliseconds = 100)
    {
        var enumerator = new MMDeviceEnumerator();

        _outputDevice = FindOutputDevice(enumerator);
        if (_outputDevice == null)
        {
            throw new InvalidOperationException(
                "未找到可用的音频输出设备。请确认 SK02 已连接，或在设置里选择其它输出设备。");
        }

        Exception? lastError = null;
        int bufferMs = Math.Clamp(bufferMilliseconds, 20, 500);

        // N9V2/ATK 这类虚拟 7.1 dongle 的驱动在独占初始化时可能直接蓝屏（FlowAudioDevice.sys）。
        // 已知设备强制走共享，避免再触发驱动 bug。
        if (IsSharedOnlyDevice(_outputDevice.FriendlyName))
            preferExclusive = false;

        foreach (var format in BuildCandidates(preferredFormat))
        {
            if (TryOpen(format, preferExclusive, bufferMs, out lastError))
                return;
        }

        string hint = lastError is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x8889000A) }
            ? "；设备被其它程序独占，请先关闭或切走该程序"
            : string.Empty;

        throw new InvalidOperationException(
            $"初始化 WASAPI 输出失败（{_outputDevice.FriendlyName}）：设备不接受 {preferredFormat.SampleRate}Hz 立体声格式{hint}。",
            lastError);
    }

    /// <summary>
    /// 按优先级尝试打开：共享优先（兼容其它播放器）或独占优先（低延迟）
    /// </summary>
    private bool TryOpen(WaveFormat format, bool preferExclusive, int bufferMilliseconds, out Exception? lastError)
    {
        lastError = null;

        (AudioClientShareMode Mode, int LatencyMs)[] modes = preferExclusive
            ? [(AudioClientShareMode.Exclusive, Math.Clamp(bufferMilliseconds / 5, 5, 20)),
               (AudioClientShareMode.Shared, Math.Clamp(bufferMilliseconds / 2, 10, 40))]
            : [(AudioClientShareMode.Shared, Math.Clamp(bufferMilliseconds / 2, 10, 40)),
               (AudioClientShareMode.Exclusive, Math.Clamp(bufferMilliseconds / 5, 5, 20))];

        foreach (var (shareMode, latencyMs) in modes)
        {
            WasapiOut? wasapiOut = null;
            try
            {
                int bufferBytes = Math.Max(format.AverageBytesPerSecond * bufferMilliseconds / 1000, format.BlockAlign * 8);
                var provider = new BufferedWaveProvider(format)
                {
                    BufferLength = bufferBytes,
                    DiscardOnBufferOverflow = true
                };

                wasapiOut = new WasapiOut(_outputDevice!, shareMode, false, latencyMs);
                wasapiOut.Init(provider);
                wasapiOut.PlaybackStopped += OnPlaybackStopped;

                _wasapiOut = wasapiOut;
                _waveProvider = provider;
                _activeFormat = format;
                IsExclusive = shareMode == AudioClientShareMode.Exclusive;

                System.Diagnostics.Debug.WriteLine(
                    $"输出设备: {_outputDevice!.FriendlyName} / {shareMode} / {latencyMs}ms / {format}");

                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;

                try { wasapiOut?.Dispose(); }
                catch { /* ignore */ }

                // 设备被其它程序独占：不回退共享，避免变成混音输出。
                // 设备只是不支持独占（例如 N9V2 dongle）时继续尝试共享。
                if (shareMode == AudioClientShareMode.Exclusive &&
                    ex.HResult == unchecked((int)0x8889000A))
                {
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 首选格式 + 降级格式
    /// </summary>
    private static List<WaveFormat> BuildCandidates(WaveFormat preferred)
    {
        var candidates = new List<WaveFormat> { preferred };

        void Add(WaveFormat format)
        {
            if (!candidates.Any(c => c.Equals(format)))
                candidates.Add(format);
        }

        // 24-bit PCM 立体声（多数 USB DAC 的原生格式，优先于 float 独占）
        Add(new WaveFormatExtensible(preferred.SampleRate, 24, 2));

        // 32-bit float 立体声（Windows 共享混音格式，兼容性最好）
        Add(WaveFormat.CreateIeeeFloatWaveFormat(preferred.SampleRate, 2));

        // 16-bit PCM 立体声
        Add(new WaveFormat(preferred.SampleRate, 16, 2));

        // 最后退回 48k/16bit
        if (preferred.SampleRate != 48000)
            Add(new WaveFormat(48000, 16, 2));

        return candidates;
    }

    private static bool IsSharedOnlyDevice(string name)
    {
        return name.Contains("N9V2", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("N9 Ultra", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("ATK N9", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("ATS3031", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 查找输出设备：用户选择 → SK02 → 默认设备（跳过虚拟线缆，避免反馈回路）
    /// </summary>
    private MMDevice? FindOutputDevice(MMDeviceEnumerator enumerator)
    {
        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .ToList();

        // 1. 用户在设置里显式选择的设备优先
        if (!string.IsNullOrEmpty(_device.Id))
        {
            try
            {
                var selected = enumerator.GetDevice(_device.Id);
                if (selected.State == DeviceState.Active && !IsVirtualCable(selected.FriendlyName))
                    return selected;
            }
            catch
            {
                // 设备已失效，继续自动选择
            }
        }

        // 2. 自动匹配 SK02 硬件
        var hardware = devices.FirstOrDefault(d =>
            d.FriendlyName.Contains("SK02", StringComparison.OrdinalIgnoreCase) ||
            d.FriendlyName.Contains("Fosi", StringComparison.OrdinalIgnoreCase) ||
            d.FriendlyName.Contains("Bravo-HD", StringComparison.OrdinalIgnoreCase));

        if (hardware != null)
            return hardware;

        // 3. 系统默认设备，但不能是虚拟线缆（否则输出会被再次捕获）
        var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        if (defaultDevice != null && !IsVirtualCable(defaultDevice.FriendlyName))
            return defaultDevice;

        // 4. 第一个非虚拟线缆的播放设备
        return devices.FirstOrDefault(d => !IsVirtualCable(d.FriendlyName)) ?? defaultDevice;
    }

    private static bool IsVirtualCable(string friendlyName)
    {
        return friendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("VB-CABLE", StringComparison.OrdinalIgnoreCase);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        _isPlaying = false;
        PlaybackStopped?.Invoke(this, e.Exception);
    }

    /// <summary>
    /// 开始播放
    /// </summary>
    public void Start()
    {
        if (_isPlaying || _wasapiOut == null)
            return;

        _wasapiOut.Play();
        _isPlaying = true;
    }

    /// <summary>
    /// 停止播放
    /// </summary>
    public void Stop()
    {
        if (!_isPlaying || _wasapiOut == null)
            return;

        try
        {
            _wasapiOut.Stop();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"停止输出时出错: {ex.Message}");
        }
        finally
        {
            _isPlaying = false;
        }
    }

    /// <summary>
    /// 写入音频数据（使用实际打开的格式）
    /// </summary>
    public void WriteAudio(float[] audioSamples)
    {
        if (_waveProvider == null || _activeFormat == null || !_isPlaying)
            return;

        try
        {
            byte[] buffer = ConvertToBytes(audioSamples, _activeFormat);

            // 缓冲不够时 AddSamples 会丢弃数据（DiscardOnBufferOverflow），这里记账方便排查
            int overflow = _waveProvider.BufferedBytes + buffer.Length - _waveProvider.BufferLength;
            if (overflow > 0)
                Interlocked.Add(ref _discardedBytes, overflow);

            _waveProvider.AddSamples(buffer, 0, buffer.Length);
        }
        catch (Exception ex)
        {
            OutputError?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// float[] → byte[]（支持 16/24/32-bit PCM 与 32-bit float）
    /// </summary>
    private static byte[] ConvertToBytes(float[] samples, WaveFormat format)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        byte[] buffer = new byte[samples.Length * bytesPerSample];

        if (format.IsFloat() && format.BitsPerSample == 32)
        {
            Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);
            return buffer;
        }

        switch (format.BitsPerSample)
        {
            case 16:
                for (int i = 0; i < samples.Length; i++)
                {
                    short sample = (short)(Math.Clamp(samples[i], -1f, 1f) * 32767f);
                    buffer[i * 2] = (byte)(sample & 0xFF);
                    buffer[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                }
                break;

            case 24:
                for (int i = 0; i < samples.Length; i++)
                {
                    int sample = (int)(Math.Clamp(samples[i], -1f, 1f) * 8388607f);
                    buffer[i * 3] = (byte)(sample & 0xFF);
                    buffer[i * 3 + 1] = (byte)((sample >> 8) & 0xFF);
                    buffer[i * 3 + 2] = (byte)((sample >> 16) & 0xFF);
                }
                break;

            case 32:
                for (int i = 0; i < samples.Length; i++)
                {
                    int sample = (int)(Math.Clamp(samples[i], -1f, 1f) * 2147483647f);
                    buffer[i * 4] = (byte)(sample & 0xFF);
                    buffer[i * 4 + 1] = (byte)((sample >> 8) & 0xFF);
                    buffer[i * 4 + 2] = (byte)((sample >> 16) & 0xFF);
                    buffer[i * 4 + 3] = (byte)((sample >> 24) & 0xFF);
                }
                break;

            default:
                throw new NotSupportedException($"不支持的输出格式: {format.BitsPerSample}bit {format.Encoding}");
        }

        return buffer;
    }

    /// <summary>
    /// 获取缓冲区状态
    /// </summary>
    public BufferStatus GetBufferStatus()
    {
        if (_waveProvider == null)
            return new BufferStatus();

        return new BufferStatus
        {
            BufferedBytes = _waveProvider.BufferedBytes,
            BufferLength = _waveProvider.BufferLength,
            BufferedDuration = _waveProvider.BufferedDuration,
            DiscardedBytes = Interlocked.Read(ref _discardedBytes)
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        Stop();

        if (_wasapiOut != null)
            _wasapiOut.PlaybackStopped -= OnPlaybackStopped;

        try { _wasapiOut?.Dispose(); }
        catch { /* ignore */ }

        _wasapiOut = null;
        _waveProvider = null;
    }
}

/// <summary>
/// 缓冲区状态
/// </summary>
public class BufferStatus
{
    public int BufferedBytes { get; set; }
    public int BufferLength { get; set; }
    public TimeSpan BufferedDuration { get; set; }

    /// <summary>因缓冲溢出被丢弃的累计字节数</summary>
    public long DiscardedBytes { get; set; }

    public float FillPercentage => BufferLength > 0 ? (float)BufferedBytes / BufferLength * 100 : 0;
}
