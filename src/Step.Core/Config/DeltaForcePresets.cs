namespace Step.Core.Config;

using Step.Core.Models;

/// <summary>
/// 三角洲行动 (Delta Force) 专用预设
/// 针对大地图、全向敌人、战术复杂的音频优化
/// </summary>
public static class DeltaForcePresets
{
    /// <summary>
    /// 全能定位预设 (推荐)
    /// 平衡所有方向，最大化定位精度
    /// 适合: 全局游戏、学习阶段
    /// </summary>
    public static Preset CreateUniversalLocalization()
    {
        return new Preset
        {
            Id = "delta_universal",
            Name = "三角洲：全能定位",
            Description = "平衡所有方向，最大化定位精度。推荐首选。",

            // EQ: 2-4kHz 强化（定位频率）
            EQBands = new float[10]
            {
                0f,    // 100Hz   - 中性
                1f,    // 200Hz   - 轻微
                0f,    // 500Hz   - 中性
                1f,    // 1kHz    - 定位增强开始
                2f,    // 2kHz    - 定位频率
                3f,    // 4kHz    - 主定位强化 ⭐
                2f,    // 8kHz    - 精细定位
                1f,    // 12kHz   - 轻微
                0f,    // 16kHz   - 中性
                0f     // 20kHz   - 中性
            },

            // HRTF 强度: 中等虚拟化
            HRTFIntensity = 1.2f,

            // 环绕声宽度: 标准
            SurroundWidth = 120f,

            // 低音增强: 轻微
            BassBoost = 1.1f,

            IsFavorite = true
        };
    }

    /// <summary>
    /// 竞技精准预设
    /// 强化中高频定位，更极端的虚拟化
    /// 适合: 竞技比赛、追求精度
    /// </summary>
    public static Preset CreateCompetitivePrecision()
    {
        return new Preset
        {
            Id = "delta_competitive",
            Name = "三角洲：竞技精准",
            Description = "强化中高频定位，更极端的虚拟化。竞技首选。",

            // EQ: 激进的中高频提升
            EQBands = new float[10]
            {
                -1f,   // 100Hz   - 消除低频混淆
                0f,    // 200Hz   - 中性
                -1f,   // 500Hz   - 消除低频混淆
                2f,    // 1kHz    - 强方向感
                3f,    // 2kHz    - 强定位
                4f,    // 4kHz    - 最强增强 ⭐
                3f,    // 8kHz    - 强精细定位
                1f,    // 12kHz   - 轻微
                0f,    // 16kHz   - 中性
                0f     // 20kHz   - 中性
            },

            // HRTF 强度: 强虚拟化
            HRTFIntensity = 1.4f,

            // 环绕声宽度: 稍宽
            SurroundWidth = 130f,

            // 低音增强: 略低
            BassBoost = 0.9f,

            IsFavorite = true
        };
    }

    /// <summary>
    /// 舒适长时间预设
    /// 温和虚拟化，适合长时间游戏
    /// 适合: 日常游戏、长比赛、敏感人群
    /// </summary>
    public static Preset CreateComfortableLongSession()
    {
        return new Preset
        {
            Id = "delta_comfort",
            Name = "三角洲：舒适长时间",
            Description = "温和虚拟化，适合长时间游戏不疲劳。",

            // EQ: 温和提升
            EQBands = new float[10]
            {
                0f,    // 100Hz
                0f,    // 200Hz
                0f,    // 500Hz
                0f,    // 1kHz    - 最小
                1f,    // 2kHz    - 轻微
                2f,    // 4kHz    - 中等
                1f,    // 8kHz    - 轻微
                0f,    // 12kHz
                0f,    // 16kHz
                0f     // 20kHz
            },

            // HRTF 强度: 参考标准
            HRTFIntensity = 1.0f,

            // 环绕声宽度: 标准
            SurroundWidth = 100f,

            // 低音增强: 标准
            BassBoost = 1.0f,

            IsFavorite = false
        };
    }

    /// <summary>
    /// 高度感增强预设
    /// 强化 Elevation 定位，用于楼道/多层建筑
    /// 适合: 建筑战术、高度判断困难
    /// </summary>
    public static Preset CreateElevationEnhanced()
    {
        return new Preset
        {
            Id = "delta_elevation",
            Name = "三角洲：高度感增强",
            Description = "强化上下方向感，适合多层建筑地图。",

            // EQ: 强化频谱陷阱区域（Elevation 关键频率）
            EQBands = new float[10]
            {
                0f,    // 100Hz
                0f,    // 200Hz
                0f,    // 500Hz
                1f,    // 1kHz
                2f,    // 2kHz    - 开始频谱陷阱区
                4f,    // 4kHz    - 频谱陷阱强化 ⭐
                4f,    // 8kHz    - 频谱陷阱强化 ⭐
                3f,    // 12kHz   - 高频强化
                2f,    // 16kHz   - 轻微强化
                1f     // 20kHz   - 保留高频信息
            },

            // HRTF 强度: 中等偏强
            HRTFIntensity = 1.3f,

            // 环绕声宽度: 标准（Elevation 不受影响）
            SurroundWidth = 120f,

            // 低音增强: 标准
            BassBoost = 1.0f,

            IsFavorite = false
        };
    }

    /// <summary>
    /// 绕后警觉预设
    /// 增强后置感知，适合防守
    /// 适合: 被绕后经常，需要后置警觉
    /// </summary>
    public static Preset CreateRearAwarenessBoost()
    {
        return new Preset
        {
            Id = "delta_rear",
            Name = "三角洲：绕后警觉",
            Description = "增强后置定位感知，适合防守战术。",

            // EQ: 平衡，略强调低中频（后置主要频率）
            EQBands = new float[10]
            {
                1f,    // 100Hz   - 低频增强
                1f,    // 200Hz   - 低频增强
                0f,    // 500Hz   - 中性
                1f,    // 1kHz    - 轻微
                2f,    // 2kHz    - 定位
                3f,    // 4kHz    - 定位
                2f,    // 8kHz    - 精细
                1f,    // 12kHz   - 轻微
                0f,    // 16kHz   - 中性
                0f     // 20kHz   - 中性
            },

            // HRTF 强度: 中等
            HRTFIntensity = 1.2f,

            // 环绕声宽度: 略宽（后置分离感更强）
            SurroundWidth = 130f,

            // 低音增强: 标准
            BassBoost = 1.0f,

            IsFavorite = false
        };
    }

    /// <summary>
    /// 极限竞技预设
    /// 最大定位精度，关键是速度
    /// 适合: 职业级、追求反应时间
    /// 警告: 可能疲劳，短时间使用
    /// </summary>
    public static Preset CreateUltraCompetitive()
    {
        return new Preset
        {
            Id = "delta_ultra",
            Name = "三角洲：极限竞技",
            Description = "最大定位精度。仅建议短时间竞技使用。⚠️ 易疲劳",

            // EQ: 激进频率处理
            EQBands = new float[10]
            {
                -2f,   // 100Hz   - 消除低频混淆
                -1f,   // 200Hz   - 消除低频
                -1f,   // 500Hz   - 消除低频
                3f,    // 1kHz    - 强定位
                4f,    // 2kHz    - 强定位
                5f,    // 4kHz    - 最强 ⭐
                4f,    // 8kHz    - 强精细定位
                2f,    // 12kHz   - 高频
                1f,    // 16kHz   - 轻微高频
                0f     // 20kHz   - 中性
            },

            // HRTF 强度: 最大虚拟化
            HRTFIntensity = 1.6f,

            // 环绕声宽度: 最宽
            SurroundWidth = 150f,

            // 低音增强: 略低（减少混淆）
            BassBoost = 0.8f,

            IsFavorite = false
        };
    }

    /// <summary>
    /// 获取所有三角洲行动预设
    /// </summary>
    public static List<Preset> GetAllDeltaForcePresets()
    {
        return new List<Preset>
        {
            CreateUniversalLocalization(),
            CreateCompetitivePrecision(),
            CreateComfortableLongSession(),
            CreateElevationEnhanced(),
            CreateRearAwarenessBoost(),
            CreateUltraCompetitive()
        };
    }

    /// <summary>
    /// 获取推荐预设（新手）
    /// </summary>
    public static Preset GetRecommendedForBeginners() => CreateUniversalLocalization();

    /// <summary>
    /// 获取推荐预设（竞技）
    /// </summary>
    public static Preset GetRecommendedForCompetitive() => CreateCompetitivePrecision();

    /// <summary>
    /// 获取推荐预设（长时间）
    /// </summary>
    public static Preset GetRecommendedForLongSession() => CreateComfortableLongSession();
}
