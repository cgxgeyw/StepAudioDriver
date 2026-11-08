namespace Step.Core.Audio.HRTF;

/// <summary>
/// 方向试听用的测试信号：短噪声脉冲 + HRIR 卷积成双耳立体声。
///
/// 用宽带噪声而不是正弦，是因为人耳的方向感主要依赖 ITD/ILD 和频谱差异，
/// 单频正弦几乎听不出左右（低频更是完全听不出）。
/// </summary>
public static class DirectionalTestTone
{
    /// <summary>
    /// 生成一列短噪声脉冲（单声道）。
    ///
    /// 用「短脉冲串」而不是一段长噪声，是因为人耳主要靠瞬态的 ITD 判断方向；
    /// 长包络的噪声起始太软，方向感会明显变弱。
    /// </summary>
    public static float[] Generate(int sampleRate, double durationSeconds = 0.4, float amplitude = 0.22f)
    {
        int count = (int)(sampleRate * durationSeconds);
        if (count <= 0)
            return [];

        var samples = new float[count];
        var random = new Random(20251107);

        const int pulseCount = 4;
        double spacing = durationSeconds / pulseCount;
        double pulseDuration = 0.03;
        int pulseSamples = Math.Max(8, (int)(pulseDuration * sampleRate));

        for (int pulse = 0; pulse < pulseCount; pulse++)
        {
            int start = (int)(pulse * spacing * sampleRate);

            for (int i = 0; i < pulseSamples && start + i < count; i++)
            {
                double t = (double)i / pulseSamples;

                // 2 ms 快速起振 + 指数衰减
                double envelope = t < 0.08
                    ? t / 0.08
                    : Math.Exp(-(t - 0.08) * 6.0);

                float noise = (float)(random.NextDouble() * 2.0 - 1.0);
                samples[start + i] += noise * (float)(envelope * amplitude);
            }
        }

        // 一阶低通，压掉最刺耳的高频
        float lowPass = 0f;

        for (int i = 0; i < count; i++)
        {
            lowPass += 0.65f * (samples[i] - lowPass);
            samples[i] = lowPass;
        }

        // 去直流
        double mean = 0;
        foreach (var sample in samples)
            mean += sample;

        mean /= count;

        for (int i = 0; i < count; i++)
            samples[i] -= (float)mean;

        return samples;
    }

    /// <summary>
    /// 把单声道信号按指定方位做双耳卷积
    /// </summary>
    /// <param name="mono">单声道信号</param>
    /// <param name="azimuthDegrees">0 = 正前，正值向右，±180 = 正后</param>
    /// <param name="sampleRate">采样率</param>
    public static float[] RenderBinaural(float[] mono, float azimuthDegrees, int sampleRate)
    {
        var leftHrir = new float[ParametricHrir.Length];
        var rightHrir = new float[ParametricHrir.Length];

        ParametricHrir.Generate(azimuthDegrees, 0f, sampleRate, leftHrir, rightHrir);

        var output = new float[mono.Length * 2];

        for (int n = 0; n < mono.Length; n++)
        {
            float sample = mono[n];
            if (sample == 0f)
                continue;

            int taps = Math.Min(ParametricHrir.Length, mono.Length - n);

            for (int k = 0; k < taps; k++)
            {
                output[(n + k) * 2] += sample * leftHrir[k];
                output[(n + k) * 2 + 1] += sample * rightHrir[k];
            }
        }

        return output;
    }
}
