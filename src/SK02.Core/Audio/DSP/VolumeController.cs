namespace SK02.Core.Audio.DSP;

/// <summary>
/// 音量控制器
/// 支持线性和对数音量调整，带有平滑淡入淡出
/// </summary>
public class VolumeController
{
    /// <summary>
    /// 最小音量 (dB)
    /// </summary>
    private const float MinVolume_dB = -60f;

    /// <summary>
    /// 最大音量 (dB)
    /// </summary>
    private const float MaxVolume_dB = 0f;

    /// <summary>
    /// 当前音量 (0.0 - 1.0)
    /// </summary>
    private float _volume = 1.0f;

    /// <summary>
    /// 目标音量
    /// </summary>
    private float _targetVolume = 1.0f;

    /// <summary>
    /// 平滑因子 (0-1，越小变化越慢)
    /// </summary>
    private float _smoothingFactor = 0.1f;

    /// <summary>
    /// 是否启用平滑淡入淡出
    /// </summary>
    private bool _enableSmoothing = true;

    /// <summary>
    /// 是否启用峰值限制器
    /// </summary>
    private bool _enableLimiter = true;

    /// <summary>
    /// 峰值限制阈值 (0.0 - 1.0)
    /// </summary>
    private float _limiterThreshold = 0.88f;

    private int _sampleRate = 48000;
    private int _lookaheadSamples = 96;
    private float[] _delayLeft = new float[96];
    private float[] _delayRight = new float[96];
    private int _delayIndex;
    private float _limiterGain = 1f;
    private float _limiterEnvelope;
    private float _attackCoeff = 0.01f;
    private float _releaseCoeff = 0.0001f;

    public VolumeController()
    {
        SetSampleRate(48000);
    }

    /// <summary>
    /// 设置采样率（决定限幅器的前瞻与时间常数）
    /// </summary>
    public void SetSampleRate(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _lookaheadSamples = Math.Max(1, (int)(_sampleRate * 0.002)); // 2 ms 前瞻
        _delayLeft = new float[_lookaheadSamples];
        _delayRight = new float[_lookaheadSamples];
        _delayIndex = 0;
        _limiterGain = 1f;
        _limiterEnvelope = 0f;

        // 2 ms 内压下来（约 4 个时间常数），150 ms 释放
        _attackCoeff = 1f - MathF.Exp(-4f / _lookaheadSamples);
        _releaseCoeff = 1f - MathF.Exp(-1f / (_sampleRate * 0.150f));
    }

    /// <summary>
    /// 设置音量 (0.0 - 1.0)
    /// </summary>
    public void SetVolume(float volume)
    {
        _targetVolume = Math.Clamp(volume, 0.0f, 1.0f);
    }

    /// <summary>
    /// 获取当前音量 (0.0 - 1.0)
    /// </summary>
    public float GetVolume() => _volume;

    /// <summary>
    /// 设置音量 (dB)
    /// </summary>
    public void SetVolumeDb(float volumeDb)
    {
        float clampedDb = Math.Clamp(volumeDb, MinVolume_dB, MaxVolume_dB);
        float linear = MathF.Pow(10.0f, clampedDb / 20.0f);
        SetVolume(linear);
    }

    /// <summary>
    /// 获取当前音量 (dB)
    /// </summary>
    public float GetVolumeDb() => 20.0f * MathF.Log10(Math.Max(_volume, 0.0001f));

    /// <summary>
    /// 增加音量
    /// </summary>
    public void IncreaseVolume(float step = 0.05f)
    {
        SetVolume(_targetVolume + step);
    }

    /// <summary>
    /// 减少音量
    /// </summary>
    public void DecreaseVolume(float step = 0.05f)
    {
        SetVolume(_targetVolume - step);
    }

    /// <summary>
    /// 设置平滑因子 (0.0 - 1.0)
    /// 数值越大，变化越快
    /// </summary>
    public void SetSmoothingFactor(float factor)
    {
        _smoothingFactor = Math.Clamp(factor, 0.01f, 1.0f);
    }

    /// <summary>
    /// 启用/禁用平滑淡入淡出
    /// </summary>
    public void SetSmoothingEnabled(bool enabled)
    {
        _enableSmoothing = enabled;
        if (!enabled)
            _volume = _targetVolume;
    }

    /// <summary>
    /// 启用/禁用峰值限制器
    /// </summary>
    public void SetLimiterEnabled(bool enabled)
    {
        _enableLimiter = enabled;
    }

    /// <summary>
    /// 设置峰值限制阈值 (0.0 - 1.0)
    /// </summary>
    public void SetLimiterThreshold(float threshold)
    {
        _limiterThreshold = Math.Clamp(threshold, 0.5f, 1.0f);
    }

    /// <summary>
    /// 处理单个样本
    /// </summary>
    public float ProcessSample(float sample)
    {
        // 更新音量 (平滑)
        UpdateVolume();

        // 应用音量
        float output = sample * _volume;

        // 应用峰值限制器
        if (_enableLimiter && Math.Abs(output) > _limiterThreshold)
        {
            // 软限制 (tanh 饱和)
            output = MathF.Tanh(output / _limiterThreshold) * _limiterThreshold;
        }

        return output;
    }

    /// <summary>
    /// 处理音频块
    /// </summary>
    public float[] Process(float[] input)
    {
        var output = new float[input.Length];

        for (int i = 0; i < input.Length; i++)
        {
            output[i] = ProcessSample(input[i]);
        }

        return output;
    }

    /// <summary>
    /// 处理立体声音频（带 2 ms 前瞻的峰值限幅）
    /// </summary>
    public float[] ProcessStereo(float[] stereoInput)
    {
        if (stereoInput.Length % 2 != 0)
            throw new ArgumentException("输入必须是立体声 (偶数长度)");

        var output = new float[stereoInput.Length];

        if (!_enableLimiter || _lookaheadSamples <= 1)
        {
            for (int i = 0; i < stereoInput.Length; i++)
            {
                UpdateVolume();
                output[i] = stereoInput[i] * _volume;
            }

            return output;
        }

        int frames = stereoInput.Length / 2;
        int lookahead = _lookaheadSamples;

        for (int frame = 0; frame < frames; frame++)
        {
            UpdateVolume();

            float left = stereoInput[frame * 2] * _volume;
            float right = stereoInput[frame * 2 + 1] * _volume;

            float peak = Math.Max(Math.Abs(left), Math.Abs(right));

            // 峰值包络：瞬时起、缓慢落
            if (peak > _limiterEnvelope)
                _limiterEnvelope = peak;
            else
                _limiterEnvelope += (peak - _limiterEnvelope) * _releaseCoeff;

            float targetGain = _limiterEnvelope > _limiterThreshold
                ? _limiterThreshold / _limiterEnvelope
                : 1f;

            // 压限快、恢复慢
            _limiterGain += (targetGain - _limiterGain) *
                            (targetGain < _limiterGain ? _attackCoeff : _releaseCoeff);

            // 输出的是 2 ms 之前的样本，给压限留出时间
            float delayedLeft = _delayLeft[_delayIndex];
            float delayedRight = _delayRight[_delayIndex];

            _delayLeft[_delayIndex] = left;
            _delayRight[_delayIndex] = right;

            if (++_delayIndex >= lookahead)
                _delayIndex = 0;

            output[frame * 2] = Math.Clamp(delayedLeft * _limiterGain, -1f, 1f);
            output[frame * 2 + 1] = Math.Clamp(delayedRight * _limiterGain, -1f, 1f);
        }

        return output;
    }

    /// <summary>
    /// 更新当前音量（如果启用平滑）
    /// </summary>
    private void UpdateVolume()
    {
        if (!_enableSmoothing)
        {
            _volume = _targetVolume;
            return;
        }

        // 指数平滑
        _volume += (_targetVolume - _volume) * _smoothingFactor;

        // 防止数值漂移
        if (Math.Abs(_volume - _targetVolume) < 0.0001f)
            _volume = _targetVolume;
    }
}
