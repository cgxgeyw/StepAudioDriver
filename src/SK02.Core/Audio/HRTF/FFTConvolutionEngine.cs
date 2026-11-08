namespace SK02.Core.Audio.HRTF;

using System;
using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

/// <summary>
/// FFT 卷积引擎 - 性能优化版本
/// 使用快速傅里叶变换实现高效卷积，性能比时域卷积提升 10-20 倍
/// </summary>
public class FFTConvolutionEngine : IDisposable
{
    private readonly int _blockSize;
    private readonly int _irLength;
    private readonly int _fftSize;

    // FFT 缓冲区
    private Complex[] _inputFFT;
    private Complex[] _irFFT;
    private Complex[] _outputFFT;

    // 时域缓冲区
    private float[] _inputBuffer;
    private float[] _outputBuffer;
    private float[] _overlapBuffer;

    // 状态标记
    private bool _isInitialized;

    public FFTConvolutionEngine(int blockSize, int irLength)
    {
        _blockSize = blockSize;
        _irLength = irLength;

        // FFT 大小必须是 blockSize + irLength - 1 的 2 的幂
        _fftSize = NextPowerOfTwo(blockSize + irLength - 1);

        // 初始化缓冲区
        _inputFFT = new Complex[_fftSize];
        _irFFT = new Complex[_fftSize];
        _outputFFT = new Complex[_fftSize];

        _inputBuffer = new float[_fftSize];
        _outputBuffer = new float[_fftSize];
        _overlapBuffer = new float[_irLength - 1];

        _isInitialized = false;
    }

    /// <summary>
    /// 初始化 IR（脉冲响应）的 FFT
    /// 这个应该在设置 IR 后调用一次
    /// </summary>
    public void InitializeIR(float[] impulseResponse)
    {
        if (impulseResponse.Length > _irLength)
        {
            throw new ArgumentException($"脉冲响应长度不能超过 {_irLength}");
        }

        // 清空 IR FFT 缓冲区
        Array.Clear(_irFFT);

        // 将 IR 复制到缓冲区
        for (int i = 0; i < impulseResponse.Length; i++)
        {
            _inputBuffer[i] = impulseResponse[i];
        }

        // 对 IR 进行 FFT
        CopyToComplex(_inputBuffer, _irFFT);
        Fourier.Forward(_irFFT, FourierOptions.Matlab);

        _isInitialized = true;
    }

    /// <summary>
    /// 处理单个音频块 (Overlap-Add 方法)
    /// </summary>
    public float[] Convolve(float[] input)
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("IR 未初始化");
        }

        if (input.Length != _blockSize)
        {
            throw new ArgumentException($"输入大小必须为 {_blockSize}");
        }

        // 步骤 1：准备输入缓冲区
        Array.Clear(_inputBuffer);
        Array.Copy(input, _inputBuffer, _blockSize);

        // 步骤 2：输入 FFT
        CopyToComplex(_inputBuffer, _inputFFT);
        Fourier.Forward(_inputFFT, FourierOptions.Matlab);

        // 步骤 3：频域乘法（卷积）
        for (int i = 0; i < _fftSize; i++)
        {
            _outputFFT[i] = _inputFFT[i] * _irFFT[i];
        }

        // 步骤 4：反向 FFT
        Fourier.Inverse(_outputFFT, FourierOptions.Matlab);

        // 步骤 5：复制到输出缓冲区
        CopyFromComplex(_outputFFT, _outputBuffer);

        // 步骤 6：Overlap-Add 处理
        var output = new float[_blockSize];

        // 添加前面的 overlap
        for (int i = 0; i < _overlapBuffer.Length; i++)
        {
            output[i] = _outputBuffer[i] + _overlapBuffer[i];
        }

        // 复制主要部分
        for (int i = _overlapBuffer.Length; i < _blockSize; i++)
        {
            output[i] = _outputBuffer[i];
        }

        // 保存 overlap 部分用于下一个块
        for (int i = 0; i < _overlapBuffer.Length; i++)
        {
            _overlapBuffer[i] = _outputBuffer[_blockSize + i];
        }

        return output;
    }

    /// <summary>
    /// 处理立体声信号
    /// </summary>
    public (float[] left, float[] right) ConvolveStereo(
        float[] input,
        float[] leftIR,
        float[] rightIR)
    {
        // 分别处理左右声道
        var left = new float[_blockSize];
        var right = new float[_blockSize];

        // 提取立体声分量
        for (int i = 0; i < _blockSize; i++)
        {
            left[i] = input[i * 2];
            right[i] = input[i * 2 + 1];
        }

        // 对左声道卷积
        InitializeIR(leftIR);
        left = Convolve(left);

        // 对右声道卷积
        InitializeIR(rightIR);
        right = Convolve(right);

        return (left, right);
    }

    /// <summary>
    /// 重置状态（用于新的音频流）
    /// </summary>
    public void Reset()
    {
        Array.Clear(_overlapBuffer);
    }

    /// <summary>
    /// 将 float 数组转换为 Complex 数组
    /// </summary>
    private void CopyToComplex(float[] source, Complex[] dest)
    {
        for (int i = 0; i < source.Length; i++)
        {
            dest[i] = new Complex(source[i], 0);
        }

        // 清空剩余部分
        for (int i = source.Length; i < dest.Length; i++)
        {
            dest[i] = Complex.Zero;
        }
    }

    /// <summary>
    /// 将 Complex 数组转换为 float 数组（取实部）
    /// </summary>
    private void CopyFromComplex(Complex[] source, float[] dest)
    {
        for (int i = 0; i < Math.Min(source.Length, dest.Length); i++)
        {
            dest[i] = (float)source[i].Real;
        }
    }

    /// <summary>
    /// 找到大于等于 value 的最小 2 的幂次
    /// </summary>
    private int NextPowerOfTwo(int value)
    {
        int power = 1;
        while (power < value)
        {
            power *= 2;
        }
        return power;
    }

    /// <summary>
    /// 获取 FFT 大小
    /// </summary>
    public int GetFFTSize() => _fftSize;

    /// <summary>
    /// 获取处理延迟（以样本为单位）
    /// </summary>
    public int GetLatency() => _irLength - 1;

    public void Dispose()
    {
        // FFT 缓冲区会被垃圾回收
    }
}
