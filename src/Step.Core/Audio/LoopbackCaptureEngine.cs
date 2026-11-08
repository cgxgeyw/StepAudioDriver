namespace Step.Core.Audio;

using NAudio.CoreAudioApi;
using NAudio.Wave;

/// <summary>
/// WASAPI Loopback 捕获器
///
/// 只负责一件事：把 VB-CABLE (CABLE Input) 上正在播放的音频抓成 float 样本。
/// 不做任何 DSP —— 处理统一由 <see cref="RealTimeAudioProcessor"/> 完成，
/// 避免同一份音频被处理两次。
/// </summary>
public class LoopbackCaptureEngine : IDisposable
{
    private WasapiCapture? _capture;
    private MMDevice? _captureDevice;
    private WaveFormat? _captureFormat;
    private bool _isCapturing;

    /// <summary>
    /// 音频数据可用事件（原始样本，未处理）
    /// </summary>
    public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;

    /// <summary>
    /// 捕获错误事件
    /// </summary>
    public event EventHandler<Exception>? CaptureError;

    /// <summary>
    /// 捕获端点停止（设备切换、驱动重置、USB 休眠等），无异常时也会触发
    /// </summary>
    public event EventHandler<Exception?>? CaptureStopped;

    /// <summary>
    /// 当前捕获设备名称（用于状态显示、反馈回路检测）
    /// </summary>
    public string? CaptureDeviceName => _captureDevice?.FriendlyName;

    /// <summary>
    /// 初始化捕获设备
    /// </summary>
    /// <param name="deviceId">指定设备 ID；为空时自动选择 CABLE Input</param>
    /// <param name="bufferMilliseconds">捕获缓冲长度（毫秒）</param>
    public void Initialize(string? deviceId = null, int bufferMilliseconds = 100)
    {
        var enumerator = new MMDeviceEnumerator();

        _captureDevice = FindCaptureDevice(enumerator, deviceId);
        if (_captureDevice == null)
        {
            throw new InvalidOperationException(
                "未找到 VB-CABLE 设备（CABLE Input）。请先安装 VB-CABLE，并把游戏/播放器的输出设备设为 \"CABLE Input\"。");
        }

        _capture = new BufferedLoopbackCapture(_captureDevice, bufferMilliseconds);

        _captureFormat = _capture.WaveFormat;
        ValidateFormat(_captureFormat);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    /// <summary>
    /// 按 ID 或名称规则选择捕获端点
    /// </summary>
    private static MMDevice? FindCaptureDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var selected = enumerator.GetDevice(deviceId);
                if (selected.State == DeviceState.Active)
                    return selected;
            }
            catch
            {
                // 指定设备已失效，回退到自动选择
            }
        }

        return FindVBCableDevice(enumerator);
    }

    /// <summary>
    /// 查找 VB-CABLE 播放端点（loopback 捕获的是它的渲染流）
    /// </summary>
    private static MMDevice? FindVBCableDevice(MMDeviceEnumerator enumerator)
    {
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

        // 优先 "CABLE Input"（VB-CABLE 的默认立体声端点）
        var exact = devices.FirstOrDefault(d =>
            d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        // 其次任意 VB-Audio 渲染端点（例如 "CABLE In 16ch"）
        return devices.FirstOrDefault(d =>
            d.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
            d.FriendlyName.Contains("VB-CABLE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 校验捕获格式是否可被转换
    /// </summary>
    private static void ValidateFormat(WaveFormat format)
    {
        if (format.Channels < 1)
            throw new NotSupportedException($"捕获格式声道数无效: {format.Channels}");

        bool supported = format.BitsPerSample switch
        {
            16 => true,
            24 => true,
            32 => true, // IEEE float 或 32-bit PCM，均在转换时区分
            _ => false
        };

        if (!supported)
        {
            throw new NotSupportedException(
                $"不支持的捕获格式: {format.SampleRate}Hz / {format.BitsPerSample}bit / {format.Channels}ch ({format.Encoding})");
        }
    }

    /// <summary>
    /// 开始捕获
    /// </summary>
    public void Start()
    {
        if (_isCapturing || _capture == null)
            return;

        _capture.StartRecording();
        _isCapturing = true;
    }

    /// <summary>
    /// 停止捕获
    /// </summary>
    public void Stop()
    {
        if (!_isCapturing || _capture == null)
            return;

        _isCapturing = false;

        try
        {
            _capture.StopRecording();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"停止捕获时出错: {ex.Message}");
        }
    }

    /// <summary>
    /// 音频数据回调：只做 byte[] → float[] 转换
    /// </summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isCapturing || e.BytesRecorded == 0)
            return;

        try
        {
            var format = _capture?.WaveFormat ?? _captureFormat;
            if (format == null)
                return;

            var samples = ConvertToFloat(e.Buffer, e.BytesRecorded, format);
            if (samples.Length > 0)
            {
                AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(samples));
            }
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(this, ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _isCapturing = false;

        if (e.Exception != null)
        {
            CaptureError?.Invoke(this, e.Exception);
        }
        else
        {
            CaptureStopped?.Invoke(this, null);
        }
    }

    /// <summary>
    /// byte[] → float[]（支持 16/24/32-bit PCM 与 32-bit float）
    /// </summary>
    private static float[] ConvertToFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0)
            return [];

        // 只转换完整帧，避免半帧错位
        int frameSize = bytesPerSample * format.Channels;
        int samplesRecorded = bytesRecorded / frameSize * format.Channels;
        if (samplesRecorded == 0)
            return [];

        var samples = new float[samplesRecorded];

        if (format.IsFloat() && format.BitsPerSample == 32)
        {
            Buffer.BlockCopy(buffer, 0, samples, 0, samplesRecorded * 4);
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i < samplesRecorded; i++)
            {
                short sample = BitConverter.ToInt16(buffer, i * 2);
                samples[i] = sample / 32768f;
            }
        }
        else if (format.BitsPerSample == 24)
        {
            for (int i = 0; i < samplesRecorded; i++)
            {
                int offset = i * 3;
                int sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                // 24-bit 符号扩展
                if ((sample & 0x800000) != 0)
                    sample |= unchecked((int)0xFF000000);

                samples[i] = sample / 8388608f;
            }
        }
        else if (format.BitsPerSample == 32)
        {
            for (int i = 0; i < samplesRecorded; i++)
            {
                int sample = BitConverter.ToInt32(buffer, i * 4);
                samples[i] = sample / 2147483648f;
            }
        }

        return samples;
    }

    /// <summary>
    /// 获取捕获格式
    /// </summary>
    public WaveFormat? GetCaptureFormat() => _capture?.WaveFormat ?? _captureFormat;

    /// <summary>
    /// 是否正在捕获
    /// </summary>
    public bool IsCapturing => _isCapturing;

    public void Dispose()
    {
        Stop();

        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
    }
}

/// <summary>
/// 音频数据事件参数
/// </summary>
public class AudioDataEventArgs : EventArgs
{
    /// <summary>
    /// 交错排列的样本（未处理）
    /// </summary>
    public float[] AudioData { get; }

    public AudioDataEventArgs(float[] audioData)
    {
        AudioData = audioData;
    }
}
