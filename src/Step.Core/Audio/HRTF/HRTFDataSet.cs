namespace Step.Core.Audio.HRTF;

/// <summary>
/// HRTF 数据集管理
/// 加载和管理头部相关传函数数据
/// </summary>
public class HRTFDataSet
{
    /// <summary>
    /// HRIR (Head-Related Impulse Response) 长度
    /// </summary>
    private const int HRIR_Length = 512;

    /// <summary>
    /// 采样率
    /// </summary>
    private const int SampleRate = 44100;

    /// <summary>
    /// 方位角分辨率 (度)
    /// </summary>
    private const float AzimuthResolution = 15f;

    /// <summary>
    /// 仰角分辨率 (度)
    /// </summary>
    private const float ElevationResolution = 15f;

    /// <summary>
    /// 左耳 HRIR 数据字典
    /// Key: "azimuth_elevation"
    /// Value: HRIR 响应数组
    /// </summary>
    private readonly Dictionary<string, float[]> _leftHRIR = new();

    /// <summary>
    /// 右耳 HRIR 数据字典
    /// </summary>
    private readonly Dictionary<string, float[]> _rightHRIR = new();

    /// <summary>
    /// 是否已初始化
    /// </summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// 初始化 HRTF 数据集
    /// 使用简化的参数化模型而非真实 HRIR 库
    /// </summary>
    public void Initialize()
    {
        // 生成参数化 HRTF 数据
        GenerateParametricHRTF();
        IsInitialized = true;
    }

    /// <summary>
    /// 生成参数化 HRTF
    /// 在完整实现中，应加载真实 HRTF 数据库（如 CIPIC、Gardner&Martin）
    /// </summary>
    private void GenerateParametricHRTF()
    {
        // 方位角范围: -180 到 180 度
        // 仰角范围: -90 到 90 度

        for (float az = -180; az <= 180; az += AzimuthResolution)
        {
            for (float el = -90; el <= 90; el += ElevationResolution)
            {
                var key = GenerateKey(az, el);

                // 生成简化的 HRIR 滤波器
                var leftHRIR = GenerateHRIR(az, el, true);
                var rightHRIR = GenerateHRIR(az, el, false);

                _leftHRIR[key] = leftHRIR;
                _rightHRIR[key] = rightHRIR;
            }
        }
    }

    /// <summary>
    /// 生成单个 HRIR 响应
    /// </summary>
    private float[] GenerateHRIR(float azimuth, float elevation, bool isLeft)
    {
        var hrir = new float[HRIR_Length];

        // ITD (Interaural Time Difference) - 两耳时间差
        // 用于水平面 (azimuth) 定位
        float itdSamples = ComputeITD(azimuth, isLeft);

        // ILD (Interaural Level Difference) - 两耳音量差
        // 用于高频定位
        float ildGain = ComputeILD(azimuth, isLeft);

        // Spectral Notch - 频谱陷阱
        // 用于仰角 (elevation) 定位
        float spectralNotchFreq = ComputeSpectralNotch(elevation);

        // 生成基础响应：Dirac 脉冲加上 ITD
        if (itdSamples >= 0 && itdSamples < HRIR_Length - 1)
        {
            int idx = (int)itdSamples;
            float frac = itdSamples - idx;

            // 线性插值 ITD
            hrir[idx] += (1 - frac) * ildGain;
            if (idx + 1 < HRIR_Length)
                hrir[idx + 1] += frac * ildGain;
        }

        // 应用频谱陷阱 (简化实现)
        // 在完整版本中，应该使用 FIR 滤波器模拟 spectral notch
        ApplySpectralNotch(hrir, spectralNotchFreq, elevation);

        return hrir;
    }

    /// <summary>
    /// 计算两耳时间差 (ITD)
    /// 基于球形头部模型
    /// </summary>
    private float ComputeITD(float azimuth, bool isLeft)
    {
        // 头部半径约 8.75 cm
        // 声速约 343 m/s
        // 最大 ITD 约 0.7 ms

        float maxITD_ms = 0.7f;
        float maxITD_samples = maxITD_ms * SampleRate / 1000f;

        // 正弦化处理
        float normalized = MathF.Sin(MathF.PI * azimuth / 180f) / 2f + 0.5f;
        float itdSamples = normalized * maxITD_samples;

        // 根据左右耳调整
        return isLeft ? itdSamples : -itdSamples;
    }

    /// <summary>
    /// 计算两耳音量差 (ILD)
    /// 高频时 ILD 更明显
    /// </summary>
    private float ComputeILD(float azimuth, bool isLeft)
    {
        // ILD 最大约 20dB（在 8kHz 以上）
        // 简化：使用线性模型
        float maxILD_dB = 20f;

        float ildDb = maxILD_dB * MathF.Abs(MathF.Sin(MathF.PI * azimuth / 180f)) / 2f;

        // 根据方向决定哪只耳朵较大
        float sign = (azimuth > 0 && !isLeft) || (azimuth < 0 && isLeft) ? 1f : -1f;

        return MathF.Pow(10f, sign * ildDb / 20f);
    }

    /// <summary>
    /// 计算频谱陷阱频率
    /// 仰角影响频谱陷阱的位置，用于 elevation 定位
    /// </summary>
    private float ComputeSpectralNotch(float elevation)
    {
        // 典型范围: 4-8 kHz
        // elevation -90° (下) 对应低频陷阱
        // elevation +90° (上) 对应高频陷阱

        float normalized = (elevation + 90f) / 180f; // 0 到 1
        float notchFreq = 4000f + normalized * 4000f; // 4kHz 到 8kHz

        return notchFreq;
    }

    /// <summary>
    /// 应用频谱陷阱
    /// </summary>
    private void ApplySpectralNotch(float[] hrir, float notchFreq, float elevation)
    {
        // 简化实现：在完整版本中应使用 FIR 滤波器
        // 这里仅作占位符
    }

    /// <summary>
    /// 生成字典 Key
    /// </summary>
    private string GenerateKey(float azimuth, float elevation)
    {
        return $"{azimuth:F1}_{elevation:F1}";
    }

    /// <summary>
    /// 获取最接近的 HRIR
    /// </summary>
    public (float[] left, float[] right) GetNearestHRIR(float azimuth, float elevation)
    {
        // 规范化角度
        azimuth = NormalizeAngle(azimuth);
        elevation = Math.Clamp(elevation, -90, 90);

        var key = FindNearestKey(azimuth, elevation);

        if (_leftHRIR.TryGetValue(key, out var leftHrir) &&
            _rightHRIR.TryGetValue(key, out var rightHrir))
        {
            return (leftHrir, rightHrir);
        }

        // 返回中心 HRIR 作为后备
        var centerKey = GenerateKey(0, 0);
        return (_leftHRIR[centerKey], _rightHRIR[centerKey]);
    }

    /// <summary>
    /// 查找最接近的预定义 HRIR 位置
    /// </summary>
    private string FindNearestKey(float azimuth, float elevation)
    {
        float nearestAz = MathF.Round(azimuth / AzimuthResolution) * AzimuthResolution;
        float nearestEl = MathF.Round(elevation / ElevationResolution) * ElevationResolution;

        nearestAz = Math.Clamp(nearestAz, -180, 180);
        nearestEl = Math.Clamp(nearestEl, -90, 90);

        return GenerateKey(nearestAz, nearestEl);
    }

    /// <summary>
    /// 规范化方位角到 -180 到 180 范围
    /// </summary>
    private float NormalizeAngle(float angle)
    {
        while (angle > 180) angle -= 360;
        while (angle < -180) angle += 360;
        return angle;
    }

    /// <summary>
    /// 获取 HRIR 长度
    /// </summary>
    public int GetHRIRLength() => HRIR_Length;
}
