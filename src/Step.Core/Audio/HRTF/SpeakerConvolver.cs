namespace Step.Core.Audio.HRTF;

/// <summary>
/// 单只虚拟扬声器的双耳卷积（重叠相加 / overlap-add）。
///
/// 每个实例保存自己那只扬声器的 IR 频谱与重叠尾巴，
/// 输入一个 128 帧的块，把左右耳结果累加进外部立体声缓冲。
/// </summary>
internal sealed class SpeakerConvolver
{
    private readonly int _blockSize;
    private readonly int _fftSize;
    private readonly int _overlapLength;
    private readonly FftN _fft;

    private readonly float[] _irLeftRe;
    private readonly float[] _irLeftIm;
    private readonly float[] _irRightRe;
    private readonly float[] _irRightIm;

    private readonly float[] _inputRe;
    private readonly float[] _inputIm;
    private readonly float[] _accumRe;
    private readonly float[] _accumIm;

    private readonly float[] _overlapLeft;
    private readonly float[] _overlapRight;

    public SpeakerConvolver(int channel, float[] hrirLeft, float[] hrirRight, int blockSize)
    {
        Channel = channel;
        _blockSize = blockSize;
        _fftSize = NextPowerOfTwo(blockSize + Math.Max(hrirLeft.Length, hrirRight.Length) - 1);
        _overlapLength = _fftSize - _blockSize;
        _fft = new FftN(_fftSize);

        _irLeftRe = new float[_fftSize];
        _irLeftIm = new float[_fftSize];
        _irRightRe = new float[_fftSize];
        _irRightIm = new float[_fftSize];

        _inputRe = new float[_fftSize];
        _inputIm = new float[_fftSize];
        _accumRe = new float[_fftSize];
        _accumIm = new float[_fftSize];

        _overlapLeft = new float[_overlapLength];
        _overlapRight = new float[_overlapLength];

        BuildSpectrum(hrirLeft, _irLeftRe, _irLeftIm);
        BuildSpectrum(hrirRight, _irRightRe, _irRightIm);

        HrirLeft = hrirLeft;
        HrirRight = hrirRight;
    }

    /// <summary>左耳 HRIR（供 Rust 原生核心配置使用）</summary>
    public float[] HrirLeft { get; }

    /// <summary>右耳 HRIR</summary>
    public float[] HrirRight { get; }

    private static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
            result <<= 1;
        return result;
    }

    /// <summary>对应的输入声道序号</summary>
    public int Channel { get; }

    /// <summary>
    /// 处理一个块，并把双耳结果累加进 mix（交错立体声）
    /// </summary>
    public void Process(float[] interleaved, int channels, float gain, float[] mix)
    {
        Array.Clear(_inputRe, 0, _fftSize);
        Array.Clear(_inputIm, 0, _fftSize);

        for (int i = 0; i < _blockSize; i++)
        {
            _inputRe[i] = interleaved[i * channels + Channel] * gain;
        }

        _fft.Transform(_inputRe, _inputIm, false);

        AccumulateEar(_irLeftRe, _irLeftIm, _overlapLeft, mix, 0);
        AccumulateEar(_irRightRe, _irRightIm, _overlapRight, mix, 1);
    }

    private void AccumulateEar(float[] irRe, float[] irIm, float[] overlap, float[] mix, int ear)
    {
        for (int i = 0; i < _fftSize; i++)
        {
            _accumRe[i] = _inputRe[i] * irRe[i] - _inputIm[i] * irIm[i];
            _accumIm[i] = _inputRe[i] * irIm[i] + _inputIm[i] * irRe[i];
        }

        _fft.Transform(_accumRe, _accumIm, true);

        for (int i = 0; i < _blockSize; i++)
        {
            mix[i * 2 + ear] += _accumRe[i] + overlap[i];
        }

        // overlap-add：FFT 尾部比一个块长时，继续向后携带
        for (int i = 0; i < _overlapLength; i++)
        {
            float tail = _accumRe[_blockSize + i];

            if (i + _blockSize < _overlapLength)
                tail += overlap[i + _blockSize];

            overlap[i] = tail;
        }
    }

    private void BuildSpectrum(float[] hrir, float[] re, float[] im)
    {
        int length = Math.Min(hrir.Length, _fftSize);

        for (int i = 0; i < length; i++)
            re[i] = hrir[i];

        _fft.Transform(re, im, false);
    }
}
