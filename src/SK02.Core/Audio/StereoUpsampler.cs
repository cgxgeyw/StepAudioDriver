namespace SK02.Core.Audio;

/// <summary>
/// 整数倍升采样器（48k → 96k / 192k / 384k 这类）。
///
/// 用多相 windowed-sinc 插值：每个输出相位一组滤波器系数，
/// 组内和归一到 1（直流增益 1），延迟为 taps/2 个输入样本。
/// 只处理立体声，用来把 DSP（HRTF/EQ 都在 48k）的结果送到更高采样率的设备。
/// </summary>
public sealed class StereoUpsampler
{
    private readonly int _factor;
    private readonly int _taps;
    private readonly int _center;
    private readonly float[][] _phases;

    private readonly float[] _historyLeft;
    private readonly float[] _historyRight;
    private int _writeIndex;

    public StereoUpsampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputRate));

        if (outputRate % inputRate != 0)
            throw new ArgumentException($"输出采样率 {outputRate} 必须是输入采样率 {inputRate} 的整数倍");

        _factor = outputRate / inputRate;
        _taps = Math.Clamp(_factor * 4, 24, 48);
        _center = _taps / 2;

        _phases = BuildPhases(_factor, _taps, _center);
        _historyLeft = new float[_taps];
        _historyRight = new float[_taps];
    }

    /// <summary>升采样倍数</summary>
    public int Factor => _factor;

    /// <summary>处理一块交错立体声，返回升采样后的交错立体声</summary>
    public float[] Process(float[] stereo)
    {
        if (stereo.Length % 2 != 0)
            throw new ArgumentException("输入必须是立体声", nameof(stereo));

        int frames = stereo.Length / 2;
        var output = new float[frames * _factor * 2];
        int outIndex = 0;

        for (int n = 0; n < frames; n++)
        {
            _historyLeft[_writeIndex] = stereo[n * 2];
            _historyRight[_writeIndex] = stereo[n * 2 + 1];

            for (int phase = 0; phase < _factor; phase++)
            {
                var coefficients = _phases[phase];

                float left = 0f;
                float right = 0f;
                int index = _writeIndex;

                for (int k = 0; k < _taps; k++)
                {
                    float coefficient = coefficients[k];
                    left += coefficient * _historyLeft[index];
                    right += coefficient * _historyRight[index];

                    if (--index < 0)
                        index = _taps - 1;
                }

                output[outIndex++] = left;
                output[outIndex++] = right;
            }

            if (++_writeIndex >= _taps)
                _writeIndex = 0;
        }

        return output;
    }

    private static float[][] BuildPhases(int factor, int taps, int center)
    {
        var phases = new float[factor][];

        for (int phase = 0; phase < factor; phase++)
        {
            var coefficients = new float[taps];
            double sum = 0;

            for (int k = 0; k < taps; k++)
            {
                // 相对插值点的距离（以输入样本为单位）
                double x = (k - center) + (double)phase / factor;

                double sinc = Math.Abs(x) < 1e-9
                    ? 1.0
                    : Math.Sin(Math.PI * x) / (Math.PI * x);

                double window = 0.42
                    + 0.5 * Math.Cos(Math.PI * x / center)
                    + 0.08 * Math.Cos(2 * Math.PI * x / center);

                double value = sinc * window;
                coefficients[k] = (float)value;
                sum += value;
            }

            // 每个相位的直流增益归一到 1
            if (Math.Abs(sum) > 1e-9)
            {
                float scale = (float)(1.0 / sum);

                for (int k = 0; k < taps; k++)
                    coefficients[k] *= scale;
            }

            phases[phase] = coefficients;
        }

        return phases;
    }
}
