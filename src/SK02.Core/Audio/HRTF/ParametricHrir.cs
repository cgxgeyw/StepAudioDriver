namespace SK02.Core.Audio.HRTF;

/// <summary>
/// 参数化 HRIR 生成器。
///
/// 模型（刚性球头 + 耳廓 + 躯干反射）：
/// - ITD：Woodworth 球头公式，最大约 0.66 ms
/// - ILD / 头影：远耳一阶低通 + 宽频衰减，遮蔽程度随水平角和仰角变化
/// - 耳廓：仰角相关的两个陷波（约 5 kHz / 9.5 kHz）
/// - 躯干：约 1.2 ms 的衰减反射
///
/// 说明：这是物理模型生成的 HRIR，不是实测数据；接口与实测数据集一致
/// （每个方向返回一对 256 taps 的 IR），后续可替换为 KEMAR/CIPIC 实测 HRIR。
/// </summary>
internal static class ParametricHrir
{
    /// <summary>IR 长度（48kHz 下约 5.3 ms）</summary>
    public const int Length = 256;

    private const float HeadRadius = 0.0875f;   // 8.75 cm
    private const float SpeedOfSound = 343f;    // m/s

    /// <summary>
    /// 生成指定方向的左右耳 HRIR（已归一化）
    /// </summary>
    /// <param name="azimuthDegrees">水平角，0=正前，正值=右侧</param>
    /// <param name="elevationDegrees">仰角，正值向上</param>
    public static void Generate(float azimuthDegrees, float elevationDegrees, int sampleRate, float[] left, float[] right)
    {
        if (left.Length < Length || right.Length < Length)
            throw new ArgumentException($"HRIR 缓冲长度必须 >= {Length}");

        Array.Clear(left, 0, left.Length);
        Array.Clear(right, 0, right.Length);

        float azimuth = azimuthDegrees * MathF.PI / 180f;
        float elevation = elevationDegrees * MathF.PI / 180f;

        float sinAzimuth = MathF.Sin(azimuth);
        float cosElevation = MathF.Cos(elevation);
        bool sourceOnRight = azimuthDegrees >= 0f;

        // ── ITD（Woodworth）──
        float absAzimuth = MathF.Abs(azimuth);
        float extraPath = absAzimuth <= MathF.PI / 2f
            ? HeadRadius * (absAzimuth + MathF.Abs(sinAzimuth))
            : HeadRadius * (MathF.PI - absAzimuth + MathF.Abs(sinAzimuth));

        float itdSamples = extraPath / SpeedOfSound * sampleRate;

        float leftDelay = sourceOnRight ? itdSamples : 0f;
        float rightDelay = sourceOnRight ? 0f : itdSamples;

        AddDelayedImpulse(left, leftDelay, 1f);
        AddDelayedImpulse(right, rightDelay, 1f);

        // ── 躯干反射（先加入，再经过头影，远耳会被一起削弱）──
        AddTorsoReflection(left, sampleRate);
        AddTorsoReflection(right, sampleRate);

        // ── 头影（远耳）──
        float shadow = MathF.Abs(sinAzimuth) * cosElevation; // 0 = 正前/正后，1 = 正侧
        float openness = 1f - shadow;

        if (sourceOnRight)
            ApplyHeadShadow(left, openness, sampleRate);
        else
            ApplyHeadShadow(right, openness, sampleRate);

        // ── 耳廓陷波（仰角线索）──
        float notch1 = 5200f + 2400f * MathF.Sin(elevation);
        float notch2 = 9500f + 1200f * MathF.Cos(elevation);

        ApplyNotch(left, notch1, 0.55f, sampleRate);
        ApplyNotch(left, notch2, 0.60f, sampleRate);
        ApplyNotch(right, notch1, 0.55f, sampleRate);
        ApplyNotch(right, notch2, 0.60f, sampleRate);

        // ── 后方音色：高频衰减 + 3.5 kHz 陷波，缓解前后混淆 ──
        float rearAmount = Math.Clamp((MathF.Abs(azimuthDegrees) - 90f) / 70f, 0f, 1f);

        if (rearAmount > 0f)
        {
            ApplyRearColoration(left, rearAmount, sampleRate);
            ApplyRearColoration(right, rearAmount, sampleRate);
        }

        // ── 前方音色：3 kHz 耳廓增益 + 9 kHz 轻微陷波，让“正前”更有存在感 ──
        float frontAmount = Math.Clamp(1f - MathF.Abs(azimuthDegrees) / 90f, 0f, 1f);

        if (frontAmount > 0f)
        {
            ApplyPeak(left, 3000f, 2.5f * frontAmount, 1.0f, sampleRate);
            ApplyPeak(right, 3000f, 2.5f * frontAmount, 1.0f, sampleRate);
            ApplyNotch(left, 9000f, 0.25f * frontAmount, sampleRate);
            ApplyNotch(right, 9000f, 0.25f * frontAmount, sampleRate);
        }

        Normalize(left, right);
    }

    /// <summary>
    /// 后方方向的音色处理：比正前方更暗、3.2 kHz 附近有陷波、1.1 kHz 附近有抬升。
    /// 纯球头模型在 0° 和 180° 的 ITD/ILD 完全相同，没有这一步会前后不分。
    /// </summary>
    private static void ApplyRearColoration(float[] ir, float amount, int sampleRate)
    {
        float cutoff = 7500f - 3000f * amount;
        float coefficient = 1f - MathF.Exp(-2f * MathF.PI * cutoff / sampleRate);
        float gain = 1f - 0.22f * amount;

        float state = 0f;

        for (int i = 0; i < ir.Length; i++)
        {
            state += coefficient * (ir[i] - state);
            ir[i] = state * gain;
        }

        ApplyNotch(ir, 3200f, 0.50f * amount, sampleRate);
        ApplyPeak(ir, 1100f, 4.0f * amount, 1.0f, sampleRate);
    }

    /// <summary>
    /// 二阶峰值滤波器（RBJ peaking），用于后方的 1 kHz 抬升
    /// </summary>
    private static void ApplyPeak(float[] ir, float frequency, float gainDb, float q, int sampleRate)
    {
        if (MathF.Abs(gainDb) < 0.01f || frequency <= 20f || frequency >= sampleRate * 0.45f)
            return;

        float w0 = 2f * MathF.PI * frequency / sampleRate;
        float cosW0 = MathF.Cos(w0);
        float sinW0 = MathF.Sin(w0);
        float amp = MathF.Pow(10f, gainDb / 40f);
        float alpha = sinW0 / (2f * q);

        float a0 = 1f + alpha / amp;
        float b0 = (1f + alpha * amp) / a0;
        float b1 = -2f * cosW0 / a0;
        float b2 = (1f - alpha * amp) / a0;
        float a1 = -2f * cosW0 / a0;
        float a2 = (1f - alpha / amp) / a0;

        float x1 = 0f, x2 = 0f, y1 = 0f, y2 = 0f;

        for (int i = 0; i < ir.Length; i++)
        {
            float x = ir[i];
            float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;

            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = y;

            ir[i] = y;
        }
    }

    /// <summary>
    /// 按 ITD 写入一个（分数延迟的）脉冲
    /// </summary>
    private static void AddDelayedImpulse(float[] ir, float delaySamples, float gain)
    {
        if (delaySamples < 0f)
            delaySamples = 0f;

        int index = (int)delaySamples;
        float fraction = delaySamples - index;

        if (index < ir.Length)
            ir[index] += gain * (1f - fraction);

        if (index + 1 < ir.Length)
            ir[index + 1] += gain * fraction;
    }

    /// <summary>
    /// 头影：一阶低通 + 宽频衰减。openness=1 为近耳（几乎不处理），0 为远耳。
    /// </summary>
    private static void ApplyHeadShadow(float[] ir, float openness, int sampleRate)
    {
        openness = Math.Clamp(openness, 0f, 1f);

        // 远耳截止频率低至 ~2.5 kHz，近耳接近全通
        float cutoff = 2500f + 15000f * MathF.Pow(openness, 1.5f);
        float coefficient = 1f - MathF.Exp(-2f * MathF.PI * cutoff / sampleRate);
        float gain = 0.62f + 0.38f * openness;

        float state = 0f;

        for (int i = 0; i < ir.Length; i++)
        {
            state += coefficient * (ir[i] - state);
            ir[i] = state * gain;
        }
    }

    /// <summary>
    /// 二阶陷波（RBJ），depth=1 为完整陷波，0 为直通
    /// </summary>
    private static void ApplyNotch(float[] ir, float frequency, float depth, int sampleRate)
    {
        if (frequency <= 20f || frequency >= sampleRate * 0.45f)
            return;

        float w0 = 2f * MathF.PI * frequency / sampleRate;
        float cosW0 = MathF.Cos(w0);
        float alpha = MathF.Sin(w0) / (2f * 2f); // Q = 2

        float a0 = 1f + alpha;
        float b0 = 1f / a0;
        float b1 = -2f * cosW0 / a0;
        float b2 = b0;
        float a1 = -2f * cosW0 / a0;
        float a2 = (1f - alpha) / a0;

        float x1 = 0f, x2 = 0f, y1 = 0f, y2 = 0f;

        for (int i = 0; i < ir.Length; i++)
        {
            float x = ir[i];
            float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;

            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = y;

            ir[i] = x + depth * (y - x);
        }
    }

    /// <summary>
    /// 躯干反射：约 1.2 ms 后的一条衰减尾巴
    /// </summary>
    private static void AddTorsoReflection(float[] ir, int sampleRate)
    {
        int delay = (int)(0.0012f * sampleRate);
        float decay = Math.Max(1f, sampleRate * 0.0006f);

        for (int i = delay; i < ir.Length; i++)
        {
            ir[i] += 0.10f * MathF.Exp(-(i - delay) / decay);
        }
    }

    /// <summary>
    /// 能量归一化：让两只耳中能量较大的一侧总能量为 1，
    /// 卷积后整体响度接近干声；同时保留 ILD。
    /// </summary>
    private static void Normalize(float[] left, float[] right)
    {
        float energyLeft = 0f;
        float energyRight = 0f;

        for (int i = 0; i < Length; i++)
        {
            energyLeft += left[i] * left[i];
            energyRight += right[i] * right[i];
        }

        float scale = 1f / MathF.Sqrt(MathF.Max(energyLeft, energyRight) + 1e-12f);

        float peak = 0f;

        for (int i = 0; i < Length; i++)
        {
            left[i] *= scale;
            right[i] *= scale;

            peak = MathF.Max(peak, MathF.Max(MathF.Abs(left[i]), MathF.Abs(right[i])));
        }

        // 防止单个 IR 峰值过高（后续多扬声器叠加时留余量）
        const float maxPeak = 1.5f;

        if (peak <= maxPeak)
            return;

        float peakScale = maxPeak / peak;

        for (int i = 0; i < Length; i++)
        {
            left[i] *= peakScale;
            right[i] *= peakScale;
        }
    }
}
