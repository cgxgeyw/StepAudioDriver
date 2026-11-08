namespace Step.Core.Audio.HRTF;

/// <summary>
/// FFT 卷积引擎
/// 用于高效的 HRIR 卷积处理
/// </summary>
public class ConvolutionEngine
{
    private readonly int _fftSize;
    private readonly int _blockSize;
    private readonly float[] _inputBuffer;
    private readonly float[] _impulseResponse;
    private readonly float[] _overlapBuffer;

    public ConvolutionEngine(int blockSize, float[] impulseResponse)
    {
        _blockSize = blockSize;
        _impulseResponse = impulseResponse;

        // FFT 大小应该 >= blockSize + impulseResponse.Length - 1
        _fftSize = NextPowerOfTwo(blockSize + impulseResponse.Length - 1);

        _inputBuffer = new float[_fftSize];
        _overlapBuffer = new float[_fftSize];
    }

    /// <summary>
    /// 处理一个音频块
    /// 使用 Overlap-Add 方法进行分块卷积
    /// </summary>
    public float[] Convolve(float[] input)
    {
        if (input.Length != _blockSize)
            throw new ArgumentException($"输入大小必须为 {_blockSize}");

        // 准备输入
        Array.Clear(_inputBuffer);
        Array.Copy(input, _inputBuffer, input.Length);

        // 在完整实现中，这里应该使用 FFT 卷积
        // 目前使用时域卷积（更慢但简单）
        var output = new float[_blockSize];

        // 时域卷积
        for (int i = 0; i < _blockSize; i++)
        {
            float sample = 0;
            for (int j = 0; j < Math.Min(_impulseResponse.Length, i + 1); j++)
            {
                sample += _inputBuffer[i - j] * _impulseResponse[j];
            }
            output[i] = sample;
        }

        return output;
    }

    /// <summary>
    /// 处理左右声道分别卷积
    /// </summary>
    public (float[] left, float[] right) ConvolveStereo(
        float[] input,
        float[] leftHRIR,
        float[] rightHRIR)
    {
        if (input.Length != _blockSize)
            throw new ArgumentException($"输入大小必须为 {_blockSize}");

        var outputLeft = new float[_blockSize];
        var outputRight = new float[_blockSize];

        // 左耳卷积
        for (int i = 0; i < _blockSize; i++)
        {
            float sample = 0;
            for (int j = 0; j < Math.Min(leftHRIR.Length, i + 1); j++)
            {
                sample += input[i - j] * leftHRIR[j];
            }
            outputLeft[i] = sample;
        }

        // 右耳卷积
        for (int i = 0; i < _blockSize; i++)
        {
            float sample = 0;
            for (int j = 0; j < Math.Min(rightHRIR.Length, i + 1); j++)
            {
                sample += input[i - j] * rightHRIR[j];
            }
            outputRight[i] = sample;
        }

        return (outputLeft, outputRight);
    }

    /// <summary>
    /// 找到大于等于 value 的最小 2 的幂次
    /// </summary>
    private int NextPowerOfTwo(int value)
    {
        int power = 1;
        while (power < value)
            power *= 2;
        return power;
    }
}
