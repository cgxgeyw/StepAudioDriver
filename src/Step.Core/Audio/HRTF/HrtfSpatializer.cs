namespace Step.Core.Audio.HRTF;

/// <summary>
/// 双耳空间化器：把多声道输入按虚拟扬声器布局做 HRIR 卷积，下混成双耳立体声。
///
/// - 2ch：左右声道映射到前左/前右虚拟扬声器（宽度可调），产生真实的 ITD/ILD/头影
/// - 4/6/8ch：按 WAVE 顺序映射 FL/FR/FC/LFE/BL/BR/SL/SR
/// - LFE 不做 HRTF，按固定增益混入双耳
///
/// 内部按 256 帧分块做 overlap-add 卷积，输出会延迟最多一个块（约 5.3 ms @48k）。
/// </summary>
public sealed class HrtfSpatializer
{
    private const int BlockSize = 128;

    private readonly int _sampleRate;

    private SpeakerBank? _bank;

    private float _width = 120f;
    private float _intensity = 1.2f;

    private float[] _pending = Array.Empty<float>();
    private float[] _pendingDry = Array.Empty<float>();
    private float[] _mix = Array.Empty<float>();
    private int _pendingFrames;
    private int _pendingChannels = -1;

    private readonly IntPtr _nativeEngine;
    private volatile bool _nativeConfigured;

    public HrtfSpatializer(int sampleRate) : this(sampleRate, true)
    {
    }

    public HrtfSpatializer(int sampleRate, bool useNative)
    {
        if (sampleRate < 8000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        _sampleRate = sampleRate;
        _nativeEngine = useNative ? NativeHrtf.Create(sampleRate, BlockSize) : IntPtr.Zero;
    }

    ~HrtfSpatializer()
    {
        NativeHrtf.Destroy(_nativeEngine);
    }

    /// <summary>扬声器组是否已构建</summary>
    public bool IsReady => _bank != null;

    /// <summary>当前宽度的虚拟扬声器布局（便于诊断/显示）</summary>
    public (int Channel, float Azimuth, bool IsLfe)[] CurrentLayout =>
        _bank == null ? [] : BuildLayout(_bank.Channels, _width);

    /// <summary>
    /// 设置宽度与强度。宽度变化会重建 IR（会有一帧左右的过渡）。
    /// </summary>
    public void Configure(float widthDegrees, float intensity)
    {
        float width = Math.Clamp(widthDegrees, 60f, 180f);
        _intensity = Math.Clamp(intensity, 0.5f, 2f);

        bool widthChanged = MathF.Abs(width - _width) >= 0.5f;
        _width = width;

        if (_bank == null || widthChanged)
            Rebuild(_bank?.Channels ?? 2);
    }

    /// <summary>
    /// 只调整干湿比（不重建 IR）
    /// </summary>
    public void SetIntensity(float intensity)
    {
        _intensity = Math.Clamp(intensity, 0.5f, 2f);
    }

    /// <summary>
    /// 取指定方向的 HRIR（诊断 / 可视化用）
    /// </summary>
    public (float[] Left, float[] Right) GetImpulseResponse(float azimuthDegrees, float elevationDegrees = 0f)
    {
        var left = new float[ParametricHrir.Length];
        var right = new float[ParametricHrir.Length];

        ParametricHrir.Generate(azimuthDegrees, elevationDegrees, _sampleRate, left, right);

        return (left, right);
    }

    /// <summary>
    /// 输入任意声道数的交错样本，返回双耳立体声（可能为空，表示还在攒块）。
    /// </summary>
    /// <param name="input">原始多声道样本</param>
    /// <param name="channels">输入声道数</param>
    /// <param name="frames">输入帧数</param>
    /// <param name="dryStereo">同一批帧的立体声下混（用于干声混合）</param>
    public float[] Process(float[] input, int channels, int frames, float[] dryStereo)
    {
        if (frames <= 0 || channels <= 0)
            return [];

        var bank = _bank;
        if (bank == null || bank.Channels != channels)
        {
            Rebuild(channels);
            bank = _bank;
        }

        if (bank == null)
            return [];

        EnsureBuffers(channels);

        int total = _pendingFrames + frames;
        int blocks = total / BlockSize;

        if (blocks == 0)
        {
            Array.Copy(input, 0, _pending, _pendingFrames * channels, frames * channels);
            Array.Copy(dryStereo, 0, _pendingDry, _pendingFrames * 2, frames * 2);
            _pendingFrames = total;
            return [];
        }

        var output = new float[blocks * BlockSize * 2];

        int readOffset = 0;
        int writeOffset = 0;

        while (blocks-- > 0)
        {
            int need = BlockSize - _pendingFrames;

            if (need > 0)
            {
                Array.Copy(input, readOffset * channels, _pending, _pendingFrames * channels, need * channels);
                Array.Copy(dryStereo, readOffset * 2, _pendingDry, _pendingFrames * 2, need * 2);

                readOffset += need;
                _pendingFrames = BlockSize;
            }

            ProcessBlock(bank, output, writeOffset);
            writeOffset += BlockSize * 2;
            _pendingFrames = 0;
        }

        int remaining = frames - readOffset;
        if (remaining > 0)
        {
            Array.Copy(input, readOffset * channels, _pending, 0, remaining * channels);
            Array.Copy(dryStereo, readOffset * 2, _pendingDry, 0, remaining * 2);
            _pendingFrames = remaining;
        }

        return output;
    }

    private void ProcessBlock(SpeakerBank bank, float[] output, int outOffset)
    {
        bool nativeDone = _nativeConfigured && NativeHrtf.Process(_nativeEngine, _pending, _mix);

        if (!nativeDone)
        {
            Array.Clear(_mix, 0, BlockSize * 2);

            foreach (var speaker in bank.Speakers)
            {
                speaker.Process(_pending, bank.Channels, bank.MixGain, _mix);
            }
        }

        if (bank.LfeChannel >= 0)
        {
            for (int i = 0; i < BlockSize; i++)
            {
                float lfe = _pending[i * bank.Channels + bank.LfeChannel] * 0.4f;
                _mix[i * 2] += lfe;
                _mix[i * 2 + 1] += lfe;
            }
        }

        float wet = Math.Clamp(_intensity / 2f, 0.15f, 1f);
        float dry = 1f - wet;

        for (int i = 0; i < BlockSize * 2; i++)
        {
            // 不在这里硬削：湿/干相加可能超过 1，交给最终的前瞻限幅器
            output[outOffset + i] = _mix[i] * wet + _pendingDry[i] * dry;
        }
    }

    private void Rebuild(int channels)
    {
        channels = Math.Clamp(channels, 1, 16);

        var bank = new SpeakerBank(channels, _width);
        var layout = BuildLayout(channels, _width);

        foreach (var (channel, azimuth, isLfe) in layout)
        {
            if (isLfe)
            {
                bank.LfeChannel = channel;
                continue;
            }

            var left = new float[ParametricHrir.Length];
            var right = new float[ParametricHrir.Length];

            ParametricHrir.Generate(azimuth, 0f, _sampleRate, left, right);

            bank.Speakers.Add(new SpeakerConvolver(channel, left, right, BlockSize));
        }

        bank.MixGain = 1f / MathF.Sqrt(Math.Max(1, bank.Speakers.Count));

        // 只替换扬声器组，不碰音频线程的待处理缓冲（避免与 Process 竞争）
        _nativeConfigured = false;
        _bank = bank;
        ConfigureNative(bank);
    }

    private void ConfigureNative(SpeakerBank bank)
    {
        if (_nativeEngine == IntPtr.Zero || bank.Speakers.Count == 0)
            return;

        int taps = ParametricHrir.Length;
        int speakerCount = bank.Speakers.Count;
        var irs = new float[speakerCount * 2 * taps];
        var gains = new float[speakerCount];
        var channels = new int[speakerCount];

        for (int i = 0; i < speakerCount; i++)
        {
            var speaker = bank.Speakers[i];
            Array.Copy(speaker.HrirLeft, 0, irs, i * 2 * taps, taps);
            Array.Copy(speaker.HrirRight, 0, irs, i * 2 * taps + taps, taps);
            gains[i] = bank.MixGain;
            channels[i] = speaker.Channel;
        }

        _nativeConfigured = NativeHrtf.Configure(
            _nativeEngine, bank.Channels, speakerCount, taps, irs, gains, channels);
    }

    private void EnsureBuffers(int channels)
    {
        if (_pendingChannels == channels &&
            _pending.Length == BlockSize * channels &&
            _pendingDry.Length == BlockSize * 2 &&
            _mix.Length == BlockSize * 2)
        {
            return;
        }

        _pendingChannels = channels;
        _pending = new float[BlockSize * channels];
        _pendingDry = new float[BlockSize * 2];
        _mix = new float[BlockSize * 2];
        _pendingFrames = 0;
    }

    private static (int Channel, float Azimuth, bool IsLfe)[] BuildLayout(int channels, float width)
    {
        // 立体声输入时前左/右保持 ±30..60°，避免中间声道相消；
        // 拿到 4 声道以上（真正的 7.1）时把前方拉宽到 ±72°，前后左右都更容易分辨。
        float front = channels >= 4
            ? Math.Min(30f + (width - 60f) * 0.5f, 72f)
            : 30f + (width - 60f) * 0.25f;

        float side = Math.Clamp(front + 45f, 90f, 110f);

        const float rear = 150f;

        return channels switch
        {
            1 => [(0, 0f, false)],

            2 => [(0, -front, false), (1, front, false)],

            4 => [(0, -front, false), (1, front, false),
                  (2, -rear, false), (3, rear, false)],

            6 => [(0, -front, false), (1, front, false), (2, 0f, false), (3, 0f, true),
                  (4, -rear, false), (5, rear, false)],

            _ => [(0, -front, false), (1, front, false), (2, 0f, false), (3, 0f, true),
                  (4, -rear, false), (5, rear, false), (6, -side, false), (7, side, false)]
        };
    }

    private sealed class SpeakerBank
    {
        public SpeakerBank(int channels, float width)
        {
            Channels = channels;
            Width = width;
        }

        public int Channels { get; }
        public float Width { get; }
        public List<SpeakerConvolver> Speakers { get; } = [];
        public int LfeChannel { get; set; } = -1;
        public float MixGain { get; set; } = 1f;
    }
}
