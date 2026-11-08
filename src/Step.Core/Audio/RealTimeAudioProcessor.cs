namespace Step.Core.Audio;

using System.Diagnostics;
using NAudio.Wave;
using Step.Core.Audio.DSP;
using Step.Core.Audio.HRTF;
using Step.Core.Performance;
using Step.Core.Models;

/// <summary>
/// 实时音频处理器 —— 完整的音频处理管道
///
/// 数据流：
/// VB-CABLE (CABLE Input) → loopback 捕获 → 声道下混到立体声
///     → [可选] HRTF 空间化 → 10 段 EQ → 音量/限幅 → WASAPI 输出 → SK02
///
/// 注意：HRTF 阶段目前默认关闭（见 <see cref="HrtfEnabled"/>），
/// 现有实现只是固定增益混音，不是真正的 HRIR 卷积；接入真实卷积前不应默认开启。
/// </summary>
public class RealTimeAudioProcessor : IDisposable
{
    private readonly LoopbackCaptureEngine _captureEngine;
    private readonly WASAPIOutputEngine _outputEngine;
    private readonly EqualizerEngine _equalizerEngine;
    private readonly VolumeController _volumeController;
    private readonly PerformanceMonitor _performanceMonitor;
    private readonly string? _captureDeviceId;
    private readonly WaveFormat? _requestedOutputFormat;
    private readonly bool _preferExclusiveOutput;
    private readonly int _bufferMilliseconds;
    private readonly float[] _channelLevels = new float[16];
    private StereoUpsampler? _upsampler;

    private WaveFormat? _inputFormat;
    // HRTF 参数
    private float _hrtfIntensity = 1.2f;
    private float _surroundWidth = 120f;
    private HrtfSpatializer? _spatializer;

    // 音量（线性 0-1）
    private float _volume = 1.0f;
    private float _preampGain = 1f;

    /// <summary>
    /// 诊断/外部处理用：跳过 EQ、HRTF、限幅和测试音，直接输出捕获的立体声。
    /// </summary>
    public bool DspBypass { get; set; }

    // 最近一个处理块的下混峰值（用于音量表 / 冒烟测试判断是否有信号）
    private float _inputPeak;

    // 帧计数（诊断采样率漂移用）
    private long _capturedFrames;
    private long _writtenFrames;

    // 方向试听音（由 UI 触发，混入主输出）
    private readonly object _testToneSync = new();
    private float[]? _testTone;
    private int _testTonePosition;

    // 状态
    private bool _isRunning;
    private bool _intentionalStop;
    private bool _disposed;

    /// <summary>
    /// 是否启用 HRTF 双耳空间化（默认开启）。
    /// </summary>
    public bool HrtfEnabled { get; set; } = true;

    /// <summary>
    /// 运行时开关 HRTF
    /// </summary>
    public void SetHrtfEnabled(bool enabled)
    {
        HrtfEnabled = enabled;
    }

    /// <summary>
    /// 处理错误（捕获错误、输出错误、处理异常）
    /// </summary>
    public event EventHandler<ProcessingErrorEventArgs>? ProcessingError;

    /// <summary>
    /// 捕获/输出端点停止，但处理器本身仍处于运行状态（需要外部重建链路）
    /// </summary>
    public event EventHandler<PipelineStoppedEventArgs>? PipelineStopped;

    /// <summary>
    /// 性能更新（保留，当前由 UI 定时轮询 <see cref="GetPerformanceReport"/>）
    /// </summary>
    public event EventHandler<PerformanceReport>? PerformanceUpdated;

    public RealTimeAudioProcessor(
        AudioDevice outputDevice,
        string? captureDeviceId = null,
        WaveFormat? requestedOutputFormat = null,
        bool preferExclusiveOutput = true,
        int bufferMilliseconds = 100)
    {
        _captureEngine = new LoopbackCaptureEngine();
        _outputEngine = new WASAPIOutputEngine(outputDevice);
        _equalizerEngine = new EqualizerEngine();
        _volumeController = new VolumeController();
        _performanceMonitor = new PerformanceMonitor();
        _captureDeviceId = captureDeviceId;
        _requestedOutputFormat = requestedOutputFormat;
        _preferExclusiveOutput = preferExclusiveOutput;
        _bufferMilliseconds = Math.Clamp(bufferMilliseconds, 20, 500);

        _captureEngine.AudioDataAvailable += OnAudioDataAvailable;
        _captureEngine.CaptureError += OnCaptureError;
        _captureEngine.CaptureStopped += OnCaptureStopped;
        _outputEngine.OutputError += OnOutputError;
        _outputEngine.PlaybackStopped += OnPlaybackStopped;
    }

    /// <summary>捕获格式（初始化后可用）</summary>
    public WaveFormat? CaptureFormat => _inputFormat;

    /// <summary>输出格式（初始化后可用，取实际打开成功的格式）</summary>
    public WaveFormat? OutputFormat => _outputEngine.OutputFormat;

    /// <summary>输出是否使用独占模式</summary>
    public bool OutputIsExclusive => _outputEngine.IsExclusive;

    /// <summary>捕获设备名</summary>
    public string? CaptureDeviceName => _captureEngine.CaptureDeviceName;

    /// <summary>输出设备名</summary>
    public string? OutputDeviceName => _outputEngine.OutputDeviceName;

    /// <summary>是否正在运行</summary>
    public bool IsRunning => _isRunning;

    /// <summary>捕获端点是否仍在接收</summary>
    public bool IsCaptureActive => _captureEngine.IsCapturing;

    /// <summary>最近一个音频块的下混峰值（0-1），用于判断是否有信号</summary>
    public float InputPeak => _inputPeak;

    /// <summary>累计捕获帧数 / 累计写出帧数（诊断时钟漂移用）</summary>
    public (long Captured, long Written) GetFrameCounters() =>
        (Interlocked.Read(ref _capturedFrames), Interlocked.Read(ref _writtenFrames));

    /// <summary>
    /// 初始化（打开捕获端点 + 输出端点）
    /// </summary>
    public void Initialize()
    {
        try
        {
            _captureEngine.Initialize(_captureDeviceId, _bufferMilliseconds);

            _inputFormat = _captureEngine.GetCaptureFormat()
                ?? throw new InvalidOperationException("无法读取捕获格式");

            // 输出统一为立体声：SK02 是立体声设备，多声道捕获在 DSP 前下混。
            // 优先 24-bit PCM 独占：不少 USB DAC 对 32-bit float 独占的内部转换会失真。
            var preferredOutput = _requestedOutputFormat
                ?? new WaveFormatExtensible(_inputFormat.SampleRate, 24, 2);

            _outputEngine.Initialize(preferredOutput, _preferExclusiveOutput, _bufferMilliseconds);

            // 反馈回路保护：捕获设备与输出设备相同会把输出再次抓回来
            if (!string.IsNullOrEmpty(CaptureDeviceName) &&
                string.Equals(CaptureDeviceName, OutputDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"捕获设备与输出设备相同（{CaptureDeviceName}），会形成反馈回路。" +
                    "请把播放器/游戏输出设为 \"CABLE Input\"，耳机输出设为 SK02 或其它扬声器。");
            }

            _equalizerEngine.SetSampleRate(_inputFormat.SampleRate);
            _volumeController.SetSampleRate(_inputFormat.SampleRate);
            _volumeController.SetVolume(_volume);

            _spatializer = new HrtfSpatializer(_inputFormat.SampleRate);
            _spatializer.Configure(_surroundWidth, _hrtfIntensity);

            // 用户强制了更高的输出采样率：DSP 仍在捕获率运行，最后再升采样
            if (OutputFormat is { } actualFormat && actualFormat.SampleRate != _inputFormat.SampleRate)
            {
                if (actualFormat.SampleRate % _inputFormat.SampleRate != 0)
                {
                    throw new InvalidOperationException(
                        $"输出采样率 {actualFormat.SampleRate} Hz 不是捕获采样率 {_inputFormat.SampleRate} Hz 的整数倍，暂不支持。");
                }

                _upsampler = new StereoUpsampler(_inputFormat.SampleRate, actualFormat.SampleRate);
            }

            Debug.WriteLine(
                $"初始化成功 - 捕获 {Describe(_inputFormat)} / 输出 {Describe(OutputFormat)}");
        }
        catch
        {
            // 初始化失败时不要留下半开的设备
            SafeDispose();
            throw;
        }
    }

    /// <summary>
    /// 启动实时处理
    /// </summary>
    public void Start()
    {
        if (_isRunning)
            return;

        if (OutputFormat == null)
            throw new InvalidOperationException("处理器尚未初始化");

        _outputEngine.Start();
        _captureEngine.Start();
        _isRunning = true;

        Debug.WriteLine("实时音频处理已启动");
    }

    /// <summary>
    /// 停止实时处理
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _intentionalStop = true;

        lock (_testToneSync)
        {
            _testTone = null;
            _testTonePosition = 0;
        }

        try
        {
            _captureEngine.Stop();
            _outputEngine.Stop();
        }
        finally
        {
            _intentionalStop = false;
        }

        Debug.WriteLine("实时音频处理已停止");
    }

    /// <summary>
    /// 音频数据回调：下混 → EQ → 音量 → 输出
    /// </summary>
    private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (!_isRunning || _inputFormat == null || OutputFormat == null)
            return;

        _performanceMonitor.StartMeasure();

        try
        {
            int channels = _inputFormat.Channels;
            int frames = e.AudioData.Length / channels;
            if (frames == 0)
                return;

            Interlocked.Add(ref _capturedFrames, frames);

            UpdateChannelLevels(e.AudioData, channels, frames);

            // 按实际帧数分配，避免复用缓冲时把上一块的残留样本写进输出
            var stereo = new float[frames * 2];

            DownmixToStereo(e.AudioData, channels, stereo, frames);

            float peak = 0f;
            for (int i = 0; i < stereo.Length; i++)
            {
                float abs = Math.Abs(stereo[i]);
                if (abs > peak)
                    peak = abs;
            }

            _inputPeak = peak;

            if (DspBypass)
            {
                Interlocked.Add(ref _writtenFrames, stereo.Length / 2);
                _outputEngine.WriteAudio(stereo);
                return;
            }

            float[] block = stereo;

            // HRTF 双耳空间化：按输入声道数映射虚拟扬声器并做 HRIR 卷积
            if (HrtfEnabled && _spatializer != null)
            {
                var spatial = _spatializer.Process(e.AudioData, channels, frames, stereo);

                // 卷积按 256 帧分块，输入不足一块时先缓存，本次没有可写出的样本
                if (spatial.Length == 0)
                    return;

                block = spatial;
            }

            if (Math.Abs(_preampGain - 1f) > 1e-4f)
            {
                for (int i = 0; i < block.Length; i++)
                    block[i] *= _preampGain;
            }

            block = _equalizerEngine.ProcessStereo(block);
            block = _volumeController.ProcessStereo(block);

            MixTestTone(block);

            if (_upsampler != null)
                block = _upsampler.Process(block);

            Interlocked.Add(ref _writtenFrames, block.Length / 2);

            _outputEngine.WriteAudio(block);
        }
        catch (Exception ex)
        {
            ProcessingError?.Invoke(this, new ProcessingErrorEventArgs(ex));
        }
        finally
        {
            _performanceMonitor.StopMeasure();
        }
    }

    /// <summary>
    /// 任意声道布局 → 立体声下混
    /// 采用 WAVE 常见声道顺序：FL, FR, FC, LFE, BL, BR, SL, SR ...
    /// </summary>
    private static void DownmixToStereo(float[] input, int channels, float[] output, int frames)
    {
        switch (channels)
        {
            case 1:
                for (int i = 0; i < frames; i++)
                {
                    float mono = input[i];
                    output[i * 2] = mono;
                    output[i * 2 + 1] = mono;
                }
                return;

            case 2:
                Array.Copy(input, output, frames * 2);
                return;
        }

        // 多声道：前 4 声道按 FL/FR/FC/LFE 处理，其余按左右交替环绕对
        int surroundPairs = Math.Max(0, (channels - 4) / 2);

        for (int i = 0; i < frames; i++)
        {
            int offset = i * channels;

            float left = input[offset];
            float right = input[offset + 1];

            if (channels >= 3)
            {
                float center = input[offset + 2] * 0.707f;
                left += center;
                right += center;
            }

            if (channels >= 4)
            {
                float lfe = input[offset + 3] * 0.2f;
                left += lfe;
                right += lfe;
            }

            for (int p = 0; p < surroundPairs; p++)
            {
                left += input[offset + 4 + p * 2] * 0.5f;
                right += input[offset + 5 + p * 2] * 0.5f;
            }

            // 留出限幅余量，避免多声道下混在进入空间化前就被硬削
            output[i * 2] = left * 0.5f;
            output[i * 2 + 1] = right * 0.5f;
        }
    }

    /// <summary>捕获声道数（未启动为 0）</summary>
    public int CaptureChannelCount => _inputFormat?.Channels ?? 0;

    /// <summary>
    /// 复制各声道的平滑 RMS 电平（0-1），用于 UI 电平表。
    /// </summary>
    public void CopyChannelLevels(float[] destination)
    {
        int count = Math.Min(destination.Length, _channelLevels.Length);

        for (int i = 0; i < count; i++)
            destination[i] = Volatile.Read(ref _channelLevels[i]);
    }

    private void UpdateChannelLevels(float[] data, int channels, int frames)
    {
        int count = Math.Min(channels, _channelLevels.Length);

        for (int c = 0; c < count; c++)
        {
            double sum = 0;

            for (int i = 0; i < frames; i++)
            {
                float sample = data[i * channels + c];
                sum += (double)sample * sample;
            }

            float rms = (float)Math.Sqrt(sum / Math.Max(1, frames));

            // 快起慢落，UI 上看起来像正常电平表
            float previous = Volatile.Read(ref _channelLevels[c]);
            Volatile.Write(ref _channelLevels[c], Math.Max(rms, previous * 0.85f));
        }

        for (int c = count; c < _channelLevels.Length; c++)
        {
            float previous = Volatile.Read(ref _channelLevels[c]);
            Volatile.Write(ref _channelLevels[c], previous * 0.85f);
        }
    }

    private void OnCaptureError(object? sender, Exception ex)
    {
        ProcessingError?.Invoke(this, new ProcessingErrorEventArgs(ex));
    }

    private void OnOutputError(object? sender, Exception ex)
    {
        ProcessingError?.Invoke(this, new ProcessingErrorEventArgs(ex));
    }

    private void OnCaptureStopped(object? sender, Exception? ex)
    {
        if (_intentionalStop || !_isRunning)
            return;

        PipelineStopped?.Invoke(this, new PipelineStoppedEventArgs("捕获设备已停止", ex));
    }

    private void OnPlaybackStopped(object? sender, Exception? ex)
    {
        if (_intentionalStop || !_isRunning)
            return;

        if (ex != null)
            ProcessingError?.Invoke(this, new ProcessingErrorEventArgs(ex));
        else
            PipelineStopped?.Invoke(this, new PipelineStoppedEventArgs("输出设备已停止", null));
    }

    /// <summary>
    /// 设置 HRTF 干湿比（0.5 微弱 ~ 2.0 全湿）
    /// </summary>
    public void SetHRTFIntensity(float intensity)
    {
        _hrtfIntensity = Math.Clamp(intensity, 0.5f, 2.0f);
        _spatializer?.SetIntensity(_hrtfIntensity);
    }

    /// <summary>
    /// 设置虚拟扬声器宽度（60° ~ 180°），会重建 HRIR
    /// </summary>
    public void SetSurroundWidth(float degrees)
    {
        _surroundWidth = Math.Clamp(degrees, 60f, 180f);
        _spatializer?.Configure(_surroundWidth, _hrtfIntensity);
    }

    /// <summary>
    /// 设置单段 EQ 增益 (dB)
    /// </summary>
    public void SetEQBandGain(int bandIndex, float gainDb)
    {
        _equalizerEngine.SetBandGain(bandIndex, gainDb);
    }

    /// <summary>
    /// 设置单段 EQ 的中心频率与 Q 值
    /// </summary>
    public void SetEQBandParameters(int bandIndex, float frequencyHz, float q)
    {
        _equalizerEngine.SetBandParameters(bandIndex, frequencyHz, q);
    }

    /// <summary>
    /// 设置音量（线性 0-1）
    /// </summary>
    public void SetVolume(float volumeLinear)
    {
        _volume = Math.Clamp(volumeLinear, 0f, 1f);
        _volumeController.SetVolume(_volume);
    }

    /// <summary>
    /// 设置 EQ 前的预衰减（线性），用于给正增益留 headroom，避免 EQ 后顶到限幅器。
    /// </summary>
    public void SetPreampGain(float linearGain)
    {
        _preampGain = Math.Clamp(linearGain, 0.05f, 1f);
    }

    /// <summary>
    /// 获取当前音量
    /// </summary>
    public float GetVolume() => _volume;

    /// <summary>
    /// 播放一段方向测试音（混入当前输出，不打断游戏声音）
    /// </summary>
    /// <param name="azimuthDegrees">0 = 正前，正值向右，±180 = 正后</param>
    public void PlayTestTone(float azimuthDegrees)
    {
        int sampleRate = _inputFormat?.SampleRate ?? 48000;

        var burst = DirectionalTestTone.Generate(sampleRate);
        var stereo = DirectionalTestTone.RenderBinaural(burst, azimuthDegrees, sampleRate);

        lock (_testToneSync)
        {
            _testTone = stereo;
            _testTonePosition = 0;
        }
    }

    /// <summary>
    /// 把尚未播放完的测试音混进输出块
    /// </summary>
    private void MixTestTone(float[] block)
    {
        lock (_testToneSync)
        {
            if (_testTone == null)
                return;

            int count = Math.Min(block.Length, _testTone.Length - _testTonePosition);

            for (int i = 0; i < count; i++)
            {
                block[i] = Math.Clamp(block[i] + _testTone[_testTonePosition + i], -1f, 1f);
            }

            _testTonePosition += count;

            if (_testTonePosition >= _testTone.Length)
                _testTone = null;
        }
    }

    /// <summary>
    /// 获取性能报告（会顺带刷新 CPU 采样）
    /// </summary>
    public PerformanceReport GetPerformanceReport()
    {
        return _performanceMonitor.GetReport();
    }

    /// <summary>
    /// 获取输出缓冲区状态
    /// </summary>
    public BufferStatus GetBufferStatus()
    {
        return _outputEngine.GetBufferStatus();
    }

    /// <summary>
    /// 生成用于状态栏的一行描述
    /// </summary>
    public string GetPipelineDescription()
    {
        string capture = $"{CaptureDeviceName ?? "?"} ({Describe(_inputFormat)})";
        string output = $"{OutputDeviceName ?? "?"} ({Describe(OutputFormat)})";
        string dsp = HrtfEnabled ? "HRTF 卷积 → EQ" : "EQ";
        return $"{capture} → {dsp} → {output}";
    }

    private static string Describe(WaveFormat? format)
    {
        if (format == null)
            return "?";

        string kind = format.IsFloat() ? "float" : "PCM";
        return $"{format.SampleRate / 1000.0:0.#}kHz/{format.BitsPerSample}bit {kind}/{format.Channels}ch";
    }

    private void SafeDispose()
    {
        try { _captureEngine.Dispose(); } catch { /* ignore */ }
        try { _outputEngine.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        Stop();
        _captureEngine.AudioDataAvailable -= OnAudioDataAvailable;
        _captureEngine.CaptureError -= OnCaptureError;
        _captureEngine.CaptureStopped -= OnCaptureStopped;
        _outputEngine.OutputError -= OnOutputError;
        _outputEngine.PlaybackStopped -= OnPlaybackStopped;

        SafeDispose();
    }
}

/// <summary>
/// 处理错误事件参数
/// </summary>
public class ProcessingErrorEventArgs : EventArgs
{
    public Exception Exception { get; }

    public ProcessingErrorEventArgs(Exception exception)
    {
        Exception = exception;
    }
}

/// <summary>
/// 捕获/输出端点停止事件参数。
/// </summary>
public class PipelineStoppedEventArgs : EventArgs
{
    public string Reason { get; }
    public Exception? Exception { get; }

    public PipelineStoppedEventArgs(string reason, Exception? exception)
    {
        Reason = reason;
        Exception = exception;
    }
}
