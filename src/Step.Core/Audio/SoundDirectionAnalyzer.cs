namespace Step.Core.Audio;

/// <summary>
/// 由 7.1 各声道 RMS 估算声音来自哪个方向。
/// X 向右、Y 向前；Confidence 表示方向集中程度（0–1）。
/// </summary>
public readonly record struct SoundDirection(
    float X,
    float Y,
    float Confidence,
    float Level,
    int DominantChannel);

public static class SoundDirectionAnalyzer
{
    // Windows 7.1 WAVE 顺序：FL FR FC LFE BL BR SL SR
    private static readonly (float X, float Y)[] SpeakerPositions =
    {
        (-0.500f, 0.866f),  // FL -30°
        ( 0.500f, 0.866f),  // FR +30°
        ( 0.000f, 1.000f),  // FC 0°
        ( 0.000f, 0.000f),  // LFE 不参与方向
        (-0.500f, -0.866f), // BL -150°
        ( 0.500f, -0.866f), // BR +150°
        (-1.000f, 0.000f),  // SL -90°
        ( 1.000f, 0.000f)   // SR +90°
    };

    public static SoundDirection Analyze(ReadOnlySpan<float> levels)
    {
        if (levels.Length < 2)
            return default;

        int count = Math.Min(levels.Length, SpeakerPositions.Length);
        double x = 0;
        double y = 0;
        double total = 0;
        float maxLevel = 0;
        int dominant = -1;

        for (int i = 0; i < count; i++)
        {
            float level = levels[i];

            if (level > maxLevel)
            {
                maxLevel = level;
                dominant = i;
            }

            // LFE 没有方向；静音声道不参与
            if (i == 3 || level < 1e-4f)
                continue;

            // 用幅度权重，避免特别响的声道把方向完全拉死
            float weight = MathF.Sqrt(level);
            var (px, py) = SpeakerPositions[i];

            x += weight * px;
            y += weight * py;
            total += weight;
        }

        if (total <= 1e-6)
            return new SoundDirection(0, 0, 0, maxLevel, dominant);

        x /= total;
        y /= total;

        float confidence = (float)Math.Sqrt(x * x + y * y);
        return new SoundDirection(
            (float)x,
            (float)y,
            Math.Clamp(confidence, 0f, 1f),
            maxLevel,
            dominant);
    }
}
