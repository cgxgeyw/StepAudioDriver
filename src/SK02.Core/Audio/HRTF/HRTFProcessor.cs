namespace SK02.Core.Audio.HRTF;

/// <summary>
/// FPS 优化的 HRTF 7.1 虚拟化处理器
/// 基于 OpenAL Soft 改进，针对 FPS 游戏的方向感优化
/// </summary>
public class HRTFProcessor
{
    private readonly HRTFDataSet _hrtfDataSet;
    private readonly ConvolutionEngine? _convolutionEngine;
    private readonly int _blockSize;

    // FPS 优化参数
    private float _fpsIntensity = 1.0f;
    private float _surroundWidth = 100f;
    private float _elevationSensitivity = 1.5f;

    // 虚拟扬声器位置 (7.1 配置)
    private readonly Vector3[] _speakerPositions = new Vector3[8]
    {
        new Vector3(0, 0, 1),          // 前中 (FC)
        new Vector3(-45, 0, 1),        // 前左 (FL)
        new Vector3(45, 0, 1),         // 前右 (FR)
        new Vector3(-135, 0, 1),       // 后左 (BL)
        new Vector3(135, 0, 1),        // 后右 (BR)
        new Vector3(-90, 0, 1),        // 侧左 (SL)
        new Vector3(90, 0, 1),         // 侧右 (SR)
        new Vector3(0, -90, 0.1f)      // 低频 (LFE)
    };

    // FPS 游戏频率响应增强 (10段 EQ)
    private readonly float[] _fpsFrequencyResponse = new float[10]
    {
        0.0f,    // 100Hz
        0.0f,    // 200Hz
        0.0f,    // 500Hz
        2.0f,    // 1kHz
        4.0f,    // 2kHz    ⭐ 主定位频率
        5.0f,    // 4kHz    ⭐ 方向感强化
        4.0f,    // 8kHz    ⭐ 精细定位
        2.0f,    // 12kHz
        1.0f,    // 16kHz
        0.0f     // 20kHz
    };

    public HRTFProcessor(int blockSize = 256)
    {
        _blockSize = blockSize;
        _hrtfDataSet = new HRTFDataSet();
        _hrtfDataSet.Initialize();

        // 初始化卷积引擎
        var dummyIR = new float[512];
        _convolutionEngine = new ConvolutionEngine(blockSize, dummyIR);
    }

    /// <summary>
    /// 设置 HRTF 强度 (0.5 - 2.0)
    /// 控制 7.1 虚拟化的强度
    /// </summary>
    public void SetHRTFIntensity(float intensity)
    {
        _fpsIntensity = Math.Clamp(intensity, 0.5f, 2.0f);
    }

    /// <summary>
    /// 设置环绕声宽度 (60 - 180 度)
    /// 虚拟扬声器之间的距离感
    /// </summary>
    public void SetSurroundWidth(float degrees)
    {
        _surroundWidth = Math.Clamp(degrees, 60f, 180f);
        UpdateSpeakerPositions();
    }

    /// <summary>
    /// 设置 Elevation 敏感度 (0.5 - 2.0)
    /// FPS 优化：增强上下方向感
    /// </summary>
    public void SetElevationSensitivity(float sensitivity)
    {
        _elevationSensitivity = Math.Clamp(sensitivity, 0.5f, 2.0f);
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

        // 分离左右声道
        int sampleCount = stereoInput.Length / 2;
        var left = new float[sampleCount];
        var right = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            left[i] = stereoInput[i * 2];
            right[i] = stereoInput[i * 2 + 1];
        }

        // 计算虚拟 7.1 混合
        var output7_1 = ProcessTo71(left, right);

        // 应用 FPS 频域增强
        var enhanced = ApplyFPSFrequencyEnhancement(output7_1);

        // 应用 Elevation 混淆减少
        var elevationEnhanced = EnhanceElevationCues(enhanced);

        // 混合回立体声
        return MixToStereo(elevationEnhanced);
    }

    /// <summary>
    /// 处理到虚拟 7.1 (8 通道)
    /// </summary>
    private float[] ProcessTo71(float[] left, float[] right)
    {
        // 8 个虚拟扬声器的输出
        var output = new float[left.Length * 8];

        // 简化的虚拟化处理
        // 在完整实现中，应该：
        // 1. 计算输入信号的空间特性
        // 2. 将单声道混合分配到 8 个虚拟位置
        // 3. 应用相应的 HRTF

        for (int i = 0; i < left.Length; i++)
        {
            float mono = (left[i] + right[i]) / 2f;

            // 根据虚拟扬声器位置分配
            for (int ch = 0; ch < 8; ch++)
            {
                float gain = GetChannelGain(ch);
                output[i * 8 + ch] = mono * gain * _fpsIntensity;
            }
        }

        return output;
    }

    /// <summary>
    /// 获取虚拟扬声器的增益
    /// FPS 优化：前置增强，侧方和后方减弱
    /// </summary>
    private float GetChannelGain(int channelIndex)
    {
        return channelIndex switch
        {
            0 => 1.0f,     // FC - 前中 (最强)
            1 => 0.7f,     // FL - 前左
            2 => 0.7f,     // FR - 前右
            3 => 0.6f,     // BL - 后左
            4 => 0.6f,     // BR - 后右
            5 => 0.8f,     // SL - 侧左
            6 => 0.8f,     // SR - 侧右
            7 => 0.3f,     // LFE - 低频
            _ => 0.0f
        };
    }

    /// <summary>
    /// 应用 FPS 频域增强
    /// 增强 2-8kHz 以提升定位清晰度
    /// </summary>
    private float[] ApplyFPSFrequencyEnhancement(float[] input)
    {
        // 简化实现：在完整版本中应使用参数化 EQ
        // 目前返回原始输入，增益在 EQ 引擎中处理

        var output = new float[input.Length];
        Array.Copy(input, output, input.Length);

        // FPS 增强因子
        const float fpsBoost = 1.2f;

        // 应用整体 boost
        for (int i = 0; i < output.Length; i++)
        {
            output[i] *= fpsBoost * _fpsIntensity;
        }

        return output;
    }

    /// <summary>
    /// 增强 Elevation 线索以减少前后混淆
    /// </summary>
    private float[] EnhanceElevationCues(float[] input)
    {
        var output = new float[input.Length];
        Array.Copy(input, output, input.Length);

        // Elevation 增强通过改变频谱特性
        // 简化实现：在完整版本中应使用频域滤波

        for (int i = 0; i < output.Length; i++)
        {
            output[i] *= (1.0f + 0.1f * _elevationSensitivity);
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
            // 提取 8 个通道的样本
            var channelSamples = new float[8];
            for (int ch = 0; ch < 8; ch++)
            {
                channelSamples[ch] = multiChannel[i * 8 + ch];
            }

            // 应用虚拟 HRTF 混合
            // 简化：直接混合，后续 EQ 处理
            float mixedL = 0, mixedR = 0;

            // 左耳
            mixedL = channelSamples[0] * 0.7f +  // FC
                     channelSamples[1] * 0.8f +  // FL
                     channelSamples[3] * 0.5f +  // BL
                     channelSamples[5] * 0.6f +  // SL
                     channelSamples[7] * 0.2f;   // LFE

            // 右耳
            mixedR = channelSamples[0] * 0.7f +  // FC
                     channelSamples[2] * 0.8f +  // FR
                     channelSamples[4] * 0.5f +  // BR
                     channelSamples[6] * 0.6f +  // SR
                     channelSamples[7] * 0.2f;   // LFE

            stereo[i * 2] = mixedL;
            stereo[i * 2 + 1] = mixedR;
        }

        return stereo;
    }

    /// <summary>
    /// 更新虚拟扬声器位置（根据环绕声宽度）
    /// </summary>
    private void UpdateSpeakerPositions()
    {
        // 调整扬声器角度以改变宽度感
        float scaleFactor = _surroundWidth / 100f;

        _speakerPositions[1].X = -45 * scaleFactor;  // FL
        _speakerPositions[2].X = 45 * scaleFactor;   // FR
        _speakerPositions[3].X = -135 * scaleFactor; // BL
        _speakerPositions[4].X = 135 * scaleFactor;  // BR
    }

    /// <summary>
    /// 获取 FPS 频率响应曲线
    /// </summary>
    public float[] GetFPSFrequencyResponse() => _fpsFrequencyResponse;

    /// <summary>
    /// 简单的 3D 向量结构
    /// </summary>
    private struct Vector3
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }
}
