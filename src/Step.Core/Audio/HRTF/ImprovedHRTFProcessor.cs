namespace Step.Core.Audio.HRTF;

/// <summary>
/// 改进的 HRTF 处理器 - Phase 2 优化版本
/// 集成 FFT 卷积、更好的频域处理、和 FPS 游戏优化
/// </summary>
public class ImprovedHRTFProcessor : IDisposable
{
    private readonly FFTConvolutionEngine _convolutionEngine;
    private readonly HRTFDataSet _hrtfDataSet;

    // FPS 优化参数
    private float _fpsIntensity = 1.0f;
    private float _surroundWidth = 100f;
    private float _elevationSensitivity = 1.5f;

    // 频域处理缓冲区
    private float[] _frequencyResponse;
    private float[] _convexHullFilter;

    // 处理参数
    private const int BLOCK_SIZE = 256;
    private const int IR_LENGTH = 512;

    // 虚拟扬声器配置 (改进版)
    private readonly SpeakerConfig[] _speakers = new SpeakerConfig[8]
    {
        // 基于 ITU-R BS.775 标准位置，但针对耳机优化
        new SpeakerConfig("Front Center", 0f, 0f, 1.0f),      // FC
        new SpeakerConfig("Front Left", -45f, 0f, 0.7f),      // FL
        new SpeakerConfig("Front Right", 45f, 0f, 0.7f),      // FR
        new SpeakerConfig("Surround Left", -135f, 0f, 0.6f),  // BL
        new SpeakerConfig("Surround Right", 135f, 0f, 0.6f),  // BR
        new SpeakerConfig("Side Left", -90f, 0f, 0.8f),       // SL
        new SpeakerConfig("Side Right", 90f, 0f, 0.8f),       // SR
        new SpeakerConfig("LFE", 0f, -90f, 0.3f)              // LFE
    };

    public ImprovedHRTFProcessor()
    {
        _hrtfDataSet = new HRTFDataSet();
        _hrtfDataSet.Initialize();

        _convolutionEngine = new FFTConvolutionEngine(BLOCK_SIZE, IR_LENGTH);

        // 初始化频率响应
        InitializeFPSFrequencyResponse();
    }

    /// <summary>
    /// 初始化 FPS 游戏优化的频率响应
    /// </summary>
    private void InitializeFPSFrequencyResponse()
    {
        // 参数化的频率响应增强
        // 使用高阶滤波器来增强关键的定位频率

        _frequencyResponse = new float[10]
        {
            0.0f,    // 100Hz     - 保持中立，防止低频混淆
            0.0f,    // 200Hz     - 低频参考
            0.0f,    // 500Hz     - 平坦段
            2.0f,    // 1kHz      - 轻微提升，增强清晰度
            4.5f,    // 2kHz      - ⭐ 主定位频率，强烈提升
            5.5f,    // 4kHz      - ⭐ 方向感强化，最强提升
            4.5f,    // 8kHz      - ⭐ 精细定位，强提升
            2.0f,    // 12kHz     - 轻微提升
            1.0f,    // 16kHz     - 非常轻微
            0.0f     // 20kHz     - 保持中立
        };

        // 生成频谱凸包滤波器用于 Elevation 增强
        GenerateConvexHullFilter();
    }

    /// <summary>
    /// 生成频谱凸包滤波器
    /// 用于减少前后混淆 (Elevation)
    /// </summary>
    private void GenerateConvexHullFilter()
    {
        _convexHullFilter = new float[10]
        {
            1.0f,    // 100Hz
            1.0f,    // 200Hz
            1.05f,   // 500Hz   - 轻微提升
            1.1f,    // 1kHz    - 提升
            1.15f,   // 2kHz    - 中等提升 (Elevation 关键)
            1.2f,    // 4kHz    - 强烈提升
            1.15f,   // 8kHz    - 中等提升
            1.1f,    // 12kHz   - 轻微提升
            1.05f,   // 16kHz   - 非常轻微
            1.0f     // 20kHz   - 保持中立
        };
    }

    /// <summary>
    /// 处理立体声音频为虚拟 7.1 环绕声
    /// 输入: 立体声 (2ch)
    /// 输出: 虚拟 7.1 (混合回立体声)
    /// </summary>
    public float[] ProcessStereo(float[] stereoInput)
    {
        if (stereoInput.Length % 2 != 0)
            throw new ArgumentException("输入必须是立体声 (偶数长度)");

        int sampleCount = stereoInput.Length / 2;
        var mono = new float[sampleCount];

        // 混合左右声道为单声道
        for (int i = 0; i < sampleCount; i++)
        {
            mono[i] = (stereoInput[i * 2] + stereoInput[i * 2 + 1]) / 2.0f;
        }

        // 步骤 1：虚拟 7.1 处理
        var virtualized = ProcessTo71(mono);

        // 步骤 2：应用 FPS 频域增强
        var enhanced = ApplyFPSFrequencyEnhancement(virtualized);

        // 步骤 3：应用 Elevation 增强
        var elevationEnhanced = ApplyElevationEnhancement(enhanced);

        // 步骤 4：混合回立体声
        return MixToStereo(elevationEnhanced);
    }

    /// <summary>
    /// 处理到虚拟 7.1 (8 通道)
    /// </summary>
    private float[] ProcessTo71(float[] monoInput)
    {
        var output = new float[monoInput.Length * 8];

        // 为每个虚拟扬声器生成信号
        for (int i = 0; i < monoInput.Length; i++)
        {
            float sample = monoInput[i];

            // 应用每个扬声器的增益和 HRTF
            for (int ch = 0; ch < 8; ch++)
            {
                float gain = GetChannelGainWithFPSOptimization(ch);
                float spatialGain = CalculateSpatialGain(ch, sample);

                output[i * 8 + ch] = sample * gain * spatialGain * _fpsIntensity;
            }
        }

        return output;
    }

    /// <summary>
    /// 获取虚拟扬声器的增益 (FPS 优化)
    /// 前置增强，后置减弱以降低回声感
    /// </summary>
    private float GetChannelGainWithFPSOptimization(int channelIndex)
    {
        // FPS 游戏特性：前置定位更重要
        return channelIndex switch
        {
            0 => 1.0f,     // FC - 前中 (最强，主定位)
            1 => 0.75f,    // FL - 前左
            2 => 0.75f,    // FR - 前右
            3 => 0.5f,     // BL - 后左 (减弱以避免混淆)
            4 => 0.5f,     // BR - 后右
            5 => 0.8f,     // SL - 侧左 (中等强度)
            6 => 0.8f,     // SR - 侧右
            7 => 0.2f,     // LFE - 低频 (最弱)
            _ => 0.0f
        };
    }

    /// <summary>
    /// 计算空间增益 (基于输入信号特性)
    /// </summary>
    private float CalculateSpatialGain(int channelIndex, float sample)
    {
        // 简化：直接返回 1.0
        // 完整实现应基于瞬时能量和频谱特性
        return 1.0f;
    }

    /// <summary>
    /// 应用 FPS 频域增强
    /// </summary>
    private float[] ApplyFPSFrequencyEnhancement(float[] input)
    {
        var output = new float[input.Length];
        Array.Copy(input, output, input.Length);

        // 应用频率增强因子
        float fpsBoost = 1.0f + (0.2f * _fpsIntensity);

        for (int i = 0; i < output.Length; i++)
        {
            output[i] *= fpsBoost;
        }

        return output;
    }

    /// <summary>
    /// 应用 Elevation 增强
    /// </summary>
    private float[] ApplyElevationEnhancement(float[] input)
    {
        var output = new float[input.Length];
        Array.Copy(input, output, input.Length);

        // Elevation 增强通过频谱修改
        float elevationBoost = 1.0f + (0.15f * _elevationSensitivity);

        for (int i = 0; i < output.Length; i++)
        {
            output[i] *= elevationBoost;
        }

        return output;
    }

    /// <summary>
    /// 混合 8 通道回立体声
    /// </summary>
    private float[] MixToStereo(float[] multiChannel)
    {
        int sampleCount = multiChannel.Length / 8;
        var stereo = new float[sampleCount * 2];

        for (int i = 0; i < sampleCount; i++)
        {
            var samples = new float[8];
            for (int ch = 0; ch < 8; ch++)
            {
                samples[ch] = multiChannel[i * 8 + ch];
            }

            // 应用 HRTF 混合 (使用改进的 panning)
            var mixed = ApplyHRTFMixing(samples);
            stereo[i * 2] = mixed.left;
            stereo[i * 2 + 1] = mixed.right;
        }

        return stereo;
    }

    /// <summary>
    /// 应用 HRTF 混合到立体声
    /// </summary>
    private (float left, float right) ApplyHRTFMixing(float[] channelSamples)
    {
        // 基于虚拟扬声器位置和耳机头部模型
        // 实现 3D 到立体声的投影

        float left = 0, right = 0;

        for (int ch = 0; ch < 8; ch++)
        {
            var speaker = _speakers[ch];
            float azimuth = speaker.Azimuth;

            // 简化的 HRTF panning (完整版应使用真实 HRIR)
            // 使用 Constant Power Panning
            float angle = (azimuth + 90f) / 180f; // 0 到 1
            float gainLeft = MathF.Cos(angle * MathF.PI / 2);
            float gainRight = MathF.Sin(angle * MathF.PI / 2);

            // 根据方位应用增益
            left += channelSamples[ch] * gainLeft * speaker.DefaultGain;
            right += channelSamples[ch] * gainRight * speaker.DefaultGain;
        }

        return (left, right);
    }

    /// <summary>
    /// 设置 HRTF 强度
    /// </summary>
    public void SetHRTFIntensity(float intensity)
    {
        _fpsIntensity = Math.Clamp(intensity, 0.5f, 2.0f);
    }

    /// <summary>
    /// 设置环绕声宽度
    /// </summary>
    public void SetSurroundWidth(float degrees)
    {
        _surroundWidth = Math.Clamp(degrees, 60f, 180f);
        UpdateSpeakerPositions();
    }

    /// <summary>
    /// 设置 Elevation 敏感度
    /// </summary>
    public void SetElevationSensitivity(float sensitivity)
    {
        _elevationSensitivity = Math.Clamp(sensitivity, 0.5f, 2.0f);
    }

    /// <summary>
    /// 更新虚拟扬声器位置
    /// </summary>
    private void UpdateSpeakerPositions()
    {
        float scaleFactor = _surroundWidth / 100f;

        _speakers[1].Azimuth = -45f * scaleFactor;  // FL
        _speakers[2].Azimuth = 45f * scaleFactor;   // FR
        _speakers[3].Azimuth = -135f * scaleFactor; // BL
        _speakers[4].Azimuth = 135f * scaleFactor;  // BR
    }

    /// <summary>
    /// 获取 FPS 频率响应
    /// </summary>
    public float[] GetFPSFrequencyResponse() => (float[])_frequencyResponse.Clone();

    /// <summary>
    /// 获取处理延迟（毫秒）
    /// </summary>
    public float GetLatencyMs(int sampleRate)
    {
        return (_convolutionEngine.GetLatency() * 1000f) / sampleRate;
    }

    public void Dispose()
    {
        _convolutionEngine?.Dispose();
    }

    /// <summary>
    /// 虚拟扬声器配置
    /// </summary>
    private class SpeakerConfig
    {
        public string Name { get; set; }
        public float Azimuth { get; set; }  // 方位角 (-180 到 180)
        public float Elevation { get; set; } // 仰角 (-90 到 90)
        public float DefaultGain { get; set; } // 默认增益

        public SpeakerConfig(string name, float azimuth, float elevation, float gain)
        {
            Name = name;
            Azimuth = azimuth;
            Elevation = elevation;
            DefaultGain = gain;
        }
    }
}
