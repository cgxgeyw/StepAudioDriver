namespace Step.Core.Audio.DSP;

/// <summary>
/// 参数化均衡器引擎，最多 15 段。
/// 默认 10 段；自定义预设可切换到 15 段精准模式。
/// </summary>
public class EqualizerEngine
{
    public const int MaxBandCount = 15;
    public const int DefaultBandCount = 10;

    private static readonly float[] DefaultCenterFrequencies =
    {
        32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000,
        2500, 6300, 10000, 12500, 16000
    };

    private int _activeBandCount = DefaultBandCount;

    private const float DefaultQ = 1.0f;
    private const float MinQ = 0.5f;
    private const float MaxQ = 10f;
    private const float MinGainDb = -12f;
    private const float MaxGainDb = 12f;

    private readonly float[] _centerFrequencies = (float[])DefaultCenterFrequencies.Clone();
    private readonly float[] _qFactors = Enumerable.Repeat(DefaultQ, MaxBandCount).ToArray();
    private readonly float[] _bandGains = new float[MaxBandCount];

    private float _lowShelfFrequency = 100f;
    private float _lowShelfGainDb;
    private float _lowShelfQ = 0.7f;

    private float _highShelfFrequency = 10000f;
    private float _highShelfGainDb;
    private float _highShelfQ = 0.7f;

    private int _sampleRate = 48000;

    /// <summary>
    /// 系数快照：[band][b0, b1, b2, a1, a2]（已按 a0 归一化）
    /// </summary>
    private volatile float[][] _coefficients;
    private volatile float[] _lowShelfCoefficients = [1f, 0f, 0f, 0f, 0f];
    private volatile float[] _highShelfCoefficients = [1f, 0f, 0f, 0f, 0f];

    // 每段、每声道独立的延迟线状态
    private readonly DelayLine[] _delayLeft;
    private readonly DelayLine[] _delayRight;
    private readonly DelayLine _lowShelfLeft = new();
    private readonly DelayLine _lowShelfRight = new();
    private readonly DelayLine _highShelfLeft = new();
    private readonly DelayLine _highShelfRight = new();

    public EqualizerEngine()
    {
        _delayLeft = Enumerable.Range(0, MaxBandCount).Select(_ => new DelayLine()).ToArray();
        _delayRight = Enumerable.Range(0, MaxBandCount).Select(_ => new DelayLine()).ToArray();
        _coefficients = BuildCoefficients();
        RebuildShelfCoefficients();
    }

    /// <summary>
    /// 设置采样率
    /// </summary>
    public void SetSampleRate(int sampleRate)
    {
        if (sampleRate < 8000 || sampleRate > 768000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "采样率超出支持范围");

        if (_sampleRate == sampleRate)
            return;

        _sampleRate = sampleRate;
        _coefficients = BuildCoefficients();
        RebuildShelfCoefficients();
        ResetStates();
    }

    /// <summary>
    /// 设置某一频段的增益
    /// </summary>
    public void SetBandGain(int bandIndex, float gainDb)
    {
        ValidateBand(bandIndex);

        _bandGains[bandIndex] = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
        _coefficients = BuildCoefficients();
    }

    /// <summary>
    /// 设置某一频段的中心频率与 Q 值
    /// </summary>
    public void SetBandParameters(int bandIndex, float frequencyHz, float q)
    {
        ValidateBand(bandIndex);

        float nyquist = _sampleRate / 2f;
        _centerFrequencies[bandIndex] = Math.Clamp(frequencyHz, 20f, nyquist * 0.9f);
        _qFactors[bandIndex] = Math.Clamp(q, MinQ, MaxQ);

        _coefficients = BuildCoefficients();
    }

    /// <summary>
    /// 获取某一频段的增益
    /// </summary>
    public float GetBandGain(int bandIndex)
    {
        ValidateBand(bandIndex);
        return _bandGains[bandIndex];
    }

    /// <summary>
    /// 获取某一频段的中心频率
    /// </summary>
    public float GetBandFrequency(int bandIndex)
    {
        ValidateBand(bandIndex);
        return _centerFrequencies[bandIndex];
    }

    /// <summary>
    /// 一次性设置所有频段的增益、中心频率与 Q 值（只重建一次系数）
    /// </summary>
    public void SetAllBands(float[] gains, float[] frequencies, float[] qFactors)
    {
        if (gains.Length != frequencies.Length || gains.Length != qFactors.Length)
            throw new ArgumentException("三个数组长度必须一致");

        if (gains.Length is not (DefaultBandCount or MaxBandCount))
            throw new ArgumentException($"数组长度必须为 {DefaultBandCount} 或 {MaxBandCount}");

        _activeBandCount = gains.Length;
        float nyquist = _sampleRate / 2f;

        for (int i = 0; i < _activeBandCount; i++)
        {
            _bandGains[i] = Math.Clamp(gains[i], MinGainDb, MaxGainDb);
            _centerFrequencies[i] = Math.Clamp(frequencies[i], 20f, nyquist * 0.9f);
            _qFactors[i] = Math.Clamp(qFactors[i], MinQ, MaxQ);
        }

        _coefficients = BuildCoefficients();
    }

    /// <summary>
    /// 设置低频搁架。Q 决定搁架斜率，与 10 段 peaking 的 Q 独立。
    /// </summary>
    public void SetLowShelf(float frequencyHz, float gainDb, float q)
    {
        float nyquist = _sampleRate / 2f;
        _lowShelfFrequency = Math.Clamp(frequencyHz, 20f, nyquist * 0.9f);
        _lowShelfGainDb = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
        _lowShelfQ = Math.Clamp(q, 0.4f, 4f);

        _lowShelfCoefficients = ComputeLowShelf(_lowShelfFrequency, _lowShelfGainDb, _lowShelfQ);
    }

    /// <summary>
    /// 设置高频搁架。
    /// </summary>
    public void SetHighShelf(float frequencyHz, float gainDb, float q)
    {
        float nyquist = _sampleRate / 2f;
        _highShelfFrequency = Math.Clamp(frequencyHz, 20f, nyquist * 0.9f);
        _highShelfGainDb = Math.Clamp(gainDb, MinGainDb, MaxGainDb);
        _highShelfQ = Math.Clamp(q, 0.4f, 4f);

        _highShelfCoefficients = ComputeHighShelf(_highShelfFrequency, _highShelfGainDb, _highShelfQ);
    }

    public int ActiveBandCount => _activeBandCount;
    public float LowShelfFrequency => _lowShelfFrequency;    public float LowShelfGainDb => _lowShelfGainDb;
    public float LowShelfQ => _lowShelfQ;
    public float HighShelfFrequency => _highShelfFrequency;
    public float HighShelfGainDb => _highShelfGainDb;
    public float HighShelfQ => _highShelfQ;

    /// <summary>
    /// 计算当前参数下的幅频响应 (dB)，用于界面曲线
    /// </summary>
    public float[] GetMagnitudeResponseDb(float[] frequencies)
    {
        var coefficients = _coefficients;
        var lowShelf = _lowShelfCoefficients;
        var highShelf = _highShelfCoefficients;
        var result = new float[frequencies.Length];

        for (int k = 0; k < frequencies.Length; k++)
        {
            double frequency = Math.Clamp(frequencies[k], 1f, _sampleRate * 0.499);
            double w = 2.0 * Math.PI * frequency / _sampleRate;

            double cos1 = Math.Cos(w);
            double sin1 = -Math.Sin(w);
            double cos2 = Math.Cos(2 * w);
            double sin2 = -Math.Sin(2 * w);

            double magnitude = 1.0;

            for (int band = 0; band < _activeBandCount; band++)
                magnitude *= CoefficientMagnitude(coefficients[band], cos1, sin1, cos2, sin2);

            magnitude *= CoefficientMagnitude(lowShelf, cos1, sin1, cos2, sin2);
            magnitude *= CoefficientMagnitude(highShelf, cos1, sin1, cos2, sin2);

            result[k] = (float)(20.0 * Math.Log10(Math.Max(magnitude, 1e-9)));
        }

        return result;
    }

    private static double CoefficientMagnitude(float[] c, double cos1, double sin1, double cos2, double sin2)
    {
        double numRe = c[0] + c[1] * cos1 + c[2] * cos2;
        double numIm = c[1] * sin1 + c[2] * sin2;
        double denRe = 1.0 + c[3] * cos1 + c[4] * cos2;
        double denIm = c[3] * sin1 + c[4] * sin2;

        double num = Math.Sqrt(numRe * numRe + numIm * numIm);
        double den = Math.Sqrt(denRe * denRe + denIm * denIm);

        return num / Math.Max(den, 1e-12);
    }

    /// <summary>
    /// 设置所有频段的增益
    /// </summary>
    public void SetAllBandGains(float[] gains)
    {
        if (gains.Length != _activeBandCount && gains.Length != MaxBandCount)
            throw new ArgumentException($"增益数组长度必须为 {_activeBandCount} 或 {MaxBandCount}", nameof(gains));

        for (int i = 0; i < _activeBandCount; i++)
        {
            _bandGains[i] = Math.Clamp(gains[i], MinGainDb, MaxGainDb);
        }

        _coefficients = BuildCoefficients();
    }

    /// <summary>
    /// 获取所有频段的增益
    /// </summary>
    public float[] GetAllBandGains()
    {
        return (float[])_bandGains.Clone();
    }

    /// <summary>
    /// 重置为平坦响应
    /// </summary>
    public void Reset()
    {
        Array.Clear(_bandGains);
        _lowShelfGainDb = 0;
        _highShelfGainDb = 0;
        _coefficients = BuildCoefficients();
        RebuildShelfCoefficients();
        ResetStates();
    }

    /// <summary>
    /// 处理音频样本（单声道）
    /// </summary>
    public float[] Process(float[] input)
    {
        var output = new float[input.Length];
        ProcessInto(input, input.Length, output, _delayLeft, 1);

        return output;
    }

    /// <summary>
    /// 处理立体声音频
    /// </summary>
    public float[] ProcessStereo(float[] stereoInput)
    {
        if (stereoInput.Length % 2 != 0)
            throw new ArgumentException("输入必须是立体声 (偶数长度)", nameof(stereoInput));

        var output = new float[stereoInput.Length];
        ProcessInto(stereoInput, stereoInput.Length, output, _delayLeft, 2);

        return output;
    }

    /// <summary>
    /// 实际处理：块内只读取一次系数快照
    /// </summary>
    private void ProcessInto(float[] input, int length, float[] output, DelayLine[] leftStates, int stride)
    {
        var coefficients = _coefficients;
        var lowShelf = _lowShelfCoefficients;
        var highShelf = _highShelfCoefficients;

        for (int i = 0; i < length; i += stride)
        {
            float left = input[i];
            float right = stride == 2 ? input[i + 1] : 0f;

            for (int band = 0; band < _activeBandCount; band++)
            {
                left = ApplyBiquadFilter(left, coefficients[band], leftStates[band]);
                if (stride == 2)
                    right = ApplyBiquadFilter(right, coefficients[band], _delayRight[band]);
            }

            left = ApplyBiquadFilter(left, lowShelf, _lowShelfLeft);
            left = ApplyBiquadFilter(left, highShelf, _highShelfLeft);

            if (stride == 2)
            {
                right = ApplyBiquadFilter(right, lowShelf, _lowShelfRight);
                right = ApplyBiquadFilter(right, highShelf, _highShelfRight);
            }

            output[i] = left;
            if (stride == 2)
                output[i + 1] = right;
        }
    }

    /// <summary>
    /// 应用二阶 IIR 滤波器 (Biquad)
    /// y(n) = b0*x(n) + b1*x(n-1) + b2*x(n-2) - a1*y(n-1) - a2*y(n-2)
    /// </summary>
    private static float ApplyBiquadFilter(float input, float[] coef, DelayLine state)
    {
        float output = coef[0] * input + coef[1] * state.X1 + coef[2] * state.X2
                     - coef[3] * state.Y1 - coef[4] * state.Y2;

        state.X2 = state.X1;
        state.X1 = input;
        state.Y2 = state.Y1;
        state.Y1 = output;

        // 数值异常保护：清空状态，避免脏数据在音频里持续循环
        if (!float.IsFinite(output))
        {
            state.Reset();
            return 0f;
        }

        return output;
    }

    /// <summary>
    /// 生成新的系数快照（UI 线程调用）
    /// </summary>
    private float[][] BuildCoefficients()
    {
        var snapshot = new float[_activeBandCount][];

        for (int i = 0; i < _activeBandCount; i++)
        {
            snapshot[i] = ComputePeakingEq(_centerFrequencies[i], _bandGains[i], _qFactors[i]);
        }

        return snapshot;
    }

    /// <summary>
    /// RBJ peaking EQ 系数（a0 归一化）
    /// </summary>
    private float[] ComputePeakingEq(float frequency, float gainDb, float q)
    {
        // 增益为 0 时直接通过，省掉无意义的运算与数值误差
        if (Math.Abs(gainDb) < 0.01f)
            return [1f, 0f, 0f, 0f, 0f];

        float w0 = 2f * MathF.PI * frequency / _sampleRate;
        float sinW0 = MathF.Sin(w0);
        float cosW0 = MathF.Cos(w0);

        float amp = MathF.Pow(10f, gainDb / 40f);
        float alpha = sinW0 / (2f * q);

        float b0 = 1f + alpha * amp;
        float b1 = -2f * cosW0;
        float b2 = 1f - alpha * amp;
        float a0 = 1f + alpha / amp;
        float a1 = -2f * cosW0;
        float a2 = 1f - alpha / amp;

        return [b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0];
    }

    private void RebuildShelfCoefficients()
    {
        _lowShelfCoefficients = ComputeLowShelf(_lowShelfFrequency, _lowShelfGainDb, _lowShelfQ);
        _highShelfCoefficients = ComputeHighShelf(_highShelfFrequency, _highShelfGainDb, _highShelfQ);
    }

    /// <summary>
    /// RBJ low shelf 系数（a0 归一化）
    /// </summary>
    private float[] ComputeLowShelf(float frequency, float gainDb, float q)
    {
        if (Math.Abs(gainDb) < 0.01f)
            return [1f, 0f, 0f, 0f, 0f];

        float w0 = 2f * MathF.PI * frequency / _sampleRate;
        float sinW0 = MathF.Sin(w0);
        float cosW0 = MathF.Cos(w0);
        float amp = MathF.Pow(10f, gainDb / 40f);
        float alpha = sinW0 / (2f * Math.Clamp(q, 0.4f, 4f));
        float twoSqrtAAlpha = 2f * MathF.Sqrt(amp) * alpha;

        float b0 = amp * ((amp + 1f) - (amp - 1f) * cosW0 + twoSqrtAAlpha);
        float b1 = 2f * amp * ((amp - 1f) - (amp + 1f) * cosW0);
        float b2 = amp * ((amp + 1f) - (amp - 1f) * cosW0 - twoSqrtAAlpha);
        float a0 = (amp + 1f) + (amp - 1f) * cosW0 + twoSqrtAAlpha;
        float a1 = -2f * ((amp - 1f) + (amp + 1f) * cosW0);
        float a2 = (amp + 1f) + (amp - 1f) * cosW0 - twoSqrtAAlpha;

        return [b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0];
    }

    /// <summary>
    /// RBJ high shelf 系数（a0 归一化）
    /// </summary>
    private float[] ComputeHighShelf(float frequency, float gainDb, float q)
    {
        if (Math.Abs(gainDb) < 0.01f)
            return [1f, 0f, 0f, 0f, 0f];

        float w0 = 2f * MathF.PI * frequency / _sampleRate;
        float sinW0 = MathF.Sin(w0);
        float cosW0 = MathF.Cos(w0);
        float amp = MathF.Pow(10f, gainDb / 40f);
        float alpha = sinW0 / (2f * Math.Clamp(q, 0.4f, 4f));
        float twoSqrtAAlpha = 2f * MathF.Sqrt(amp) * alpha;

        float b0 = amp * ((amp + 1f) + (amp - 1f) * cosW0 + twoSqrtAAlpha);
        float b1 = -2f * amp * ((amp - 1f) + (amp + 1f) * cosW0);
        float b2 = amp * ((amp + 1f) + (amp - 1f) * cosW0 - twoSqrtAAlpha);
        float a0 = (amp + 1f) - (amp - 1f) * cosW0 + twoSqrtAAlpha;
        float a1 = 2f * ((amp - 1f) - (amp + 1f) * cosW0);
        float a2 = (amp + 1f) - (amp - 1f) * cosW0 - twoSqrtAAlpha;

        return [b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0];
    }

    private void ResetStates()
    {
        foreach (var state in _delayLeft)
            state.Reset();
        foreach (var state in _delayRight)
            state.Reset();

        _lowShelfLeft.Reset();
        _lowShelfRight.Reset();
        _highShelfLeft.Reset();
        _highShelfRight.Reset();
    }

    private static void ValidateBand(int bandIndex)
    {
        if (bandIndex < 0 || bandIndex >= MaxBandCount)
            throw new ArgumentOutOfRangeException(nameof(bandIndex));
    }

    /// <summary>
    /// IIR 滤波器延迟线
    /// </summary>
    private class DelayLine
    {
        public float X1;
        public float X2;
        public float Y1;
        public float Y2;

        public void Reset()
        {
            X1 = X2 = Y1 = Y2 = 0f;
        }
    }
}
