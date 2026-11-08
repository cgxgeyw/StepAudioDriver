namespace Step.Core.Audio;

using NAudio.Wave;
using Step.Core.Audio.DSP;
using Step.Core.Audio.HRTF;
using Step.Core.Config;
using Step.Core.Device;
using Step.Core.Models;

/// <summary>
/// 主音频处理引擎
/// 协调 HRTF、EQ、音量等所有音频处理模块
/// </summary>
public class AudioEngine : IDisposable
{
    private IWavePlayer? _wavePlayer;
    private readonly EqualizerEngine _equalizer;
    private readonly HRTFProcessor _hrtfProcessor;
    private readonly VolumeController _volumeController;
    private readonly DeviceManager _deviceManager;
    private readonly PresetManager? _presetManager;

    private AudioDevice? _currentDevice;
    private bool _isPlaying;
    private float _fpsIntensity = 1.0f;
    private float _surroundWidth = 100f;

    /// <summary>
    /// 音频处理开始事件
    /// </summary>
    public event EventHandler<EventArgs>? PlaybackStarted;

    /// <summary>
    /// 音频处理停止事件
    /// </summary>
    public event EventHandler<EventArgs>? PlaybackStopped;

    public AudioEngine(PresetManager? presetManager = null)
    {
        _equalizer = new EqualizerEngine();
        _hrtfProcessor = new HRTFProcessor();
        _volumeController = new VolumeController();
        _deviceManager = new DeviceManager();
        _presetManager = presetManager;
    }

    /// <summary>
    /// 初始化音频引擎
    /// </summary>
    public void Initialize(AudioDevice? device = null)
    {
        try
        {
            // 选择设备
            if (device == null)
            {
                device = _deviceManager.GetDefaultDevice();
            }

            if (device == null)
            {
                throw new InvalidOperationException("未找到可用的音频设备");
            }

            _currentDevice = device;
            _deviceManager.SelectDevice(device);

            // 设置采样率
            int sampleRate = device.SupportedFormats.FirstOrDefault()?.SampleRate ?? 48000;
            _equalizer.SetSampleRate(sampleRate);
            _hrtfProcessor.SetHRTFIntensity(_fpsIntensity);
            _hrtfProcessor.SetSurroundWidth(_surroundWidth);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("初始化音频引擎失败", ex);
        }
    }

    /// <summary>
    /// 启动音频处理
    /// </summary>
    public void Start()
    {
        if (_isPlaying)
        {
            return;
        }

        if (_currentDevice == null)
        {
            throw new InvalidOperationException("音频引擎未初始化");
        }

        try
        {
            _isPlaying = true;
            PlaybackStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("启动音频处理失败", ex);
        }
    }

    /// <summary>
    /// 停止音频处理
    /// </summary>
    public void Stop()
    {
        if (!_isPlaying)
        {
            return;
        }

        _isPlaying = false;
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 应用预设
    /// </summary>
    public void SetPreset(Preset preset)
    {
        // 应用 EQ
        _equalizer.SetAllBandGains(preset.EQBands);

        // 应用 HRTF 参数
        _hrtfProcessor.SetHRTFIntensity(preset.HRTFIntensity);
        _hrtfProcessor.SetSurroundWidth(preset.SurroundWidth);

        // 应用低音增强 (通过调整低频 EQ)
        var eqBands = preset.EQBands.Clone() as float[];
        if (eqBands != null && preset.BassBoost != 1.0f)
        {
            // 增强低频频段 (100Hz, 200Hz)
            eqBands[0] *= preset.BassBoost;
            eqBands[1] *= preset.BassBoost;
            _equalizer.SetAllBandGains(eqBands);
        }

        _fpsIntensity = preset.HRTFIntensity;
        _surroundWidth = preset.SurroundWidth;
    }

    /// <summary>
    /// 更新 EQ 频段增益
    /// </summary>
    public void SetEQBandGain(int bandIndex, float gainDb)
    {
        _equalizer.SetBandGain(bandIndex, gainDb);
    }

    /// <summary>
    /// 获取 EQ 频段增益
    /// </summary>
    public float GetEQBandGain(int bandIndex)
    {
        return _equalizer.GetBandGain(bandIndex);
    }

    /// <summary>
    /// 获取所有 EQ 频段增益
    /// </summary>
    public float[] GetAllEQBandGains()
    {
        return _equalizer.GetAllBandGains();
    }

    /// <summary>
    /// 设置音量
    /// </summary>
    public void SetVolume(float volume)
    {
        _volumeController.SetVolume(volume);
    }

    /// <summary>
    /// 获取当前音量
    /// </summary>
    public float GetVolume() => _volumeController.GetVolume();

    /// <summary>
    /// 设置音量 (dB)
    /// </summary>
    public void SetVolumeDb(float volumeDb)
    {
        _volumeController.SetVolumeDb(volumeDb);
    }

    /// <summary>
    /// 获取当前音量 (dB)
    /// </summary>
    public float GetVolumeDb() => _volumeController.GetVolumeDb();

    /// <summary>
    /// 增加音量
    /// </summary>
    public void IncreaseVolume(float step = 0.05f)
    {
        _volumeController.IncreaseVolume(step);
    }

    /// <summary>
    /// 减少音量
    /// </summary>
    public void DecreaseVolume(float step = 0.05f)
    {
        _volumeController.DecreaseVolume(step);
    }

    /// <summary>
    /// 设置 HRTF 强度
    /// </summary>
    public void SetHRTFIntensity(float intensity)
    {
        _fpsIntensity = intensity;
        _hrtfProcessor.SetHRTFIntensity(intensity);
    }

    /// <summary>
    /// 设置环绕声宽度
    /// </summary>
    public void SetSurroundWidth(float degrees)
    {
        _surroundWidth = degrees;
        _hrtfProcessor.SetSurroundWidth(degrees);
    }

    /// <summary>
    /// 设置 Elevation 敏感度
    /// </summary>
    public void SetElevationSensitivity(float sensitivity)
    {
        _hrtfProcessor.SetElevationSensitivity(sensitivity);
    }

    /// <summary>
    /// 处理立体声音频
    /// </summary>
    public float[] ProcessAudio(float[] stereoInput)
    {
        if (!_isPlaying)
        {
            return stereoInput;
        }

        try
        {
            // 步骤 1: 应用 HRTF 7.1 虚拟化
            var hrtfProcessed = _hrtfProcessor.ProcessStereo(stereoInput);

            // 步骤 2: 应用 EQ 处理
            var eqProcessed = _equalizer.ProcessStereo(hrtfProcessed);

            // 步骤 3: 应用音量控制
            var volumeProcessed = _volumeController.ProcessStereo(eqProcessed);

            return volumeProcessed;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"音频处理错误: {ex.Message}");
            return stereoInput;
        }
    }

    /// <summary>
    /// 获取设备管理器
    /// </summary>
    public DeviceManager GetDeviceManager() => _deviceManager;

    /// <summary>
    /// 获取当前设备
    /// </summary>
    public AudioDevice? GetCurrentDevice() => _currentDevice;

    /// <summary>
    /// 切换设备
    /// </summary>
    public void SelectDevice(AudioDevice device)
    {
        _currentDevice = device;
        _deviceManager.SelectDevice(device);

        // 更新采样率
        int sampleRate = device.SupportedFormats.FirstOrDefault()?.SampleRate ?? 48000;
        _equalizer.SetSampleRate(sampleRate);
    }

    /// <summary>
    /// 是否正在播放
    /// </summary>
    public bool IsPlaying => _isPlaying;

    /// <summary>
    /// 获取 FPS 频率响应曲线
    /// </summary>
    public float[] GetFPSFrequencyResponse()
    {
        return _hrtfProcessor.GetFPSFrequencyResponse();
    }

    public void Dispose()
    {
        Stop();
        _wavePlayer?.Dispose();
        _deviceManager?.Dispose();
    }
}
