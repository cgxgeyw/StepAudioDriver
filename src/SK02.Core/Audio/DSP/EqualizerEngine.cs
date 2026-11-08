namespace SK02.Core.Audio.DSP;

/// <summary>
/// 10 段参数化均衡器引擎
///
/// 每段是一个 RBJ peaking biquad，级联处理。
/// 系数由 UI 线程更新（构建新的不可变快照后原子替换），
/// 音频线程每块只读取一次快照，避免处理过程中系数被改写。
/// 立体声的两个声道各自维护滤波器状态，不会互相污染。
/// </summary>
public class EqualizerEngine
{
    /// <summary>
    /// EQ 频段数
    /// </summary>
    public const int BandCount = 10;

    /// <summary>
    /// 默认中心频率 (Hz)，与 UI 滑块一致
    /// </summary>
    private static readonly float[] DefaultCenterFrequencies =
    {
        32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000
    };

    private const float DefaultQ = 1.0f;
    private const float MinGainDb = -12f;
    private const float MaxGainDb = 12f;

    private readonly float[] _centerFrequencies = (float[])DefaultCenterFrequencies.Clone();
    private readonly float[] _qFactors = Enumerable.Repeat(DefaultQ, BandCount).ToArray();
    private readonly float[] _bandGains = new float[BandCount];

    private int _sampleRate = 48000;

    /// <summary>
    /// 系数快照：[band][b0, b1, b2, a1, a2]（已按 a0 归一化）
    /// </summary>
    private volatile float[][] _coefficients;

    // 每段、每声道独立的延迟线状态
    private readonly DelayLine[] _delayLeft;
    private readonly DelayLine[] _delayRight;

    public EqualizerEngine()
    {
        _delayLeft = Enumerable.Range(0, BandCount).Select(_ => new DelayLine()).ToArray();
        _delayRight = Enumerable.Range(0, BandCount).Select(_ => new DelayLine()).ToArray();
        _coefficients = BuildCoefficients();
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
        _qFactors[bandIndex] = Math.Clamp(q, 0.85f, 10f);

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
        if (gains.Length != BandCount || frequencies.Length != BandCount || qFactors.Length != BandCount)
            throw new ArgumentException($"数组长度必须为 {BandCount}");

        float nyquist = _sampleRate / 2f;

        for (int i = 0; i < BandCount; i++)
        {
            _bandGains[i] = Math.Clamp(gains[i], MinGainDb, MaxGainDb);
            _centerFrequencies[i] = Math.Clamp(frequencies[i], 20f, nyquist * 0.9f);
            _qFactors[i] = Math.Clamp(qFactors[i], 0.85f, 10f);
        }

        _coefficients = BuildCoefficients();
    }

    /// <summary>
    /// 计算当前参数下的幅频响应 (dB)，用于界面曲线
    /// </summary>
    public float[] GetMagnitudeResponseDb(float[] frequencies)
    {
        var coefficients = _coefficients;
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

            for (int band = 0; band < BandCount; band++)
            {
                var c = coefficients[band];

                double numRe = c[0] + c[1] * cos1 + c[2] * cos2;
                double numIm = c[1] * sin1 + c[2] * sin2;
                double denRe = 1.0 + c[3] * cos1 + c[4] * cos2;
                double denIm = c[3] * sin1 + c[4] * sin2;

                double num = Math.Sqrt(numRe * numRe + numIm * numIm);
                double den = Math.Sqrt(denRe * denRe + denIm * denIm);

                magnitude *= num / Math.Max(den, 1e-12);
            }

            result[k] = (float)(20.0 * Math.Log10(Math.Max(magnitude, 1e-9)));
        }

        return result;
    }

    /// <summary>
    /// 设置所有频段的增益
    /// </summary>
    public void SetAllBandGains(float[] gains)
    {
        if (gains.Length != BandCount)
            throw new ArgumentException($"增益数组长度必须为 {BandCount}", nameof(gains));

        for (int i = 0; i < BandCount; i++)
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
        _coefficients = BuildCoefficients();
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

        for (int i = 0; i < length; i += stride)
        {
            float left = input[i];
            float right = stride == 2 ? input[i + 1] : 0f;

            for (int band = 0; band < BandCount; band++)
            {
                left = ApplyBiquadFilter(left, coefficients[band], leftStates[band]);
                if (stride == 2)
                    right = ApplyBiquadFilter(right, coefficients[band], _delayRight[band]);
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
        var snapshot = new float[BandCount][];

        for (int i = 0; i < BandCount; i++)
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

    private void ResetStates()
    {
        foreach (var state in _delayLeft)
            state.Reset();
        foreach (var state in _delayRight)
            state.Reset();
    }

    private static void ValidateBand(int bandIndex)
    {
        if (bandIndex < 0 || bandIndex >= BandCount)
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
