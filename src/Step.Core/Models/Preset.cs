namespace Step.Core.Models;

/// <summary>
/// 音频预设配置
/// </summary>
public class Preset
{
    /// <summary>
    /// 预设唯一标识符
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 预设名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 预设描述
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// EQ 增益 (dB)，最多 15 段。默认前 10 段为普通模式。
    /// </summary>
    public float[] EQBands { get; set; } = new float[15];

    /// <summary>
    /// EQ 中心频率 (Hz)，与 EQBands 一一对应
    /// </summary>
    public float[] EQFrequencies { get; set; } =
        { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000, 2500, 6300, 10000, 12500, 16000 };

    /// <summary>
    /// EQ 的 Q 值
    /// </summary>
    public float[] EQQValues { get; set; } =
        { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };

    /// <summary>
    /// 是否使用 15 段精准模式（仅自定义预设）
    /// </summary>
    public bool UsePrecisionEq { get; set; }

    /// <summary>低频搁架</summary>
    public float LowShelfFrequency { get; set; } = 100f;
    public float LowShelfGain { get; set; }
    public float LowShelfQ { get; set; } = 0.7f;

    /// <summary>高频搁架</summary>
    public float HighShelfFrequency { get; set; } = 10000f;
    public float HighShelfGain { get; set; }
    public float HighShelfQ { get; set; } = 0.7f;

    /// <summary>
    /// HRTF 强度 (0.5 - 2.0)
    /// 控制 7.1 虚拟化的强度
    /// </summary>
    public float HRTFIntensity { get; set; } = 1.0f;

    /// <summary>
    /// 环绕声宽度 (60 - 180 度)
    /// 虚拟扬声器之间的距离感
    /// </summary>
    public float SurroundWidth { get; set; } = 100f;

    /// <summary>
    /// 低音增强 (0.5 - 2.0)
    /// </summary>
    public float BassBoost { get; set; } = 1.0f;

    /// <summary>
    /// 是否为收藏预设
    /// </summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// 是否为内置预设（内置预设只读，不允许修改）
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// 是否允许编辑
    /// </summary>
    public bool IsEditable => !IsBuiltIn;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 修改时间
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 字符串表示
    /// </summary>
    public override string ToString() => $"{Name} ({Description})";

    /// <summary>
    /// 克隆预设
    /// </summary>
    public Preset Clone()
    {
        return new Preset
        {
            Id = Guid.NewGuid().ToString(),
            Name = Name,
            Description = Description,
            EQBands = (float[])EQBands.Clone(),
            EQFrequencies = (float[])EQFrequencies.Clone(),
            EQQValues = (float[])EQQValues.Clone(),
            UsePrecisionEq = UsePrecisionEq,
            LowShelfFrequency = LowShelfFrequency,
            LowShelfGain = LowShelfGain,
            LowShelfQ = LowShelfQ,
            HighShelfFrequency = HighShelfFrequency,
            HighShelfGain = HighShelfGain,
            HighShelfQ = HighShelfQ,
            HRTFIntensity = HRTFIntensity,
            SurroundWidth = SurroundWidth,
            BassBoost = BassBoost,
            IsFavorite = IsFavorite,
            CreatedAt = CreatedAt,
            ModifiedAt = DateTime.Now
        };
    }

    /// <summary>
    /// 重置为默认值
    /// </summary>
    public void Reset()
    {
        Array.Clear(EQBands);
        EQFrequencies = new float[] { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000, 2500, 6300, 10000, 12500, 16000 };
        EQQValues = new float[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
        UsePrecisionEq = false;
        LowShelfFrequency = 100f;
        LowShelfGain = 0f;
        LowShelfQ = 0.7f;
        HighShelfFrequency = 10000f;
        HighShelfGain = 0f;
        HighShelfQ = 0.7f;
        HRTFIntensity = 1.0f;
        SurroundWidth = 100f;
        BassBoost = 1.0f;
        ModifiedAt = DateTime.Now;
    }
}
