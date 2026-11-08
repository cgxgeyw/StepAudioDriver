namespace SK02.Core.Audio.HRTF;

using System;

/// <summary>
/// 定长 radix-2 复数 FFT（原地计算）。
///
/// 只为卷积准备：长度固定、可复用缓冲区、不分配内存。
/// </summary>
internal sealed class FftN
{
    private readonly int _size;
    private readonly int[] _reversal;
    private readonly float[] _cos;
    private readonly float[] _sin;

    public FftN(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException("FFT 长度必须是 2 的幂", nameof(size));

        _size = size;
        _reversal = new int[size];

        int bits = 0;
        while ((1 << bits) < size)
            bits++;

        for (int i = 0; i < size; i++)
            _reversal[i] = ReverseBits(i, bits);

        _cos = new float[size / 2];
        _sin = new float[size / 2];

        for (int i = 0; i < size / 2; i++)
        {
            double angle = 2.0 * Math.PI * i / size;
            _cos[i] = (float)Math.Cos(angle);
            _sin[i] = (float)Math.Sin(angle);
        }
    }

    /// <summary>
    /// 原地变换。inverse=true 时做逆变换（含 1/N 缩放）。
    /// </summary>
    public void Transform(float[] re, float[] im, bool inverse)
    {
        int n = _size;

        // 位反转置换
        for (int i = 0; i < n; i++)
        {
            int j = _reversal[i];
            if (j <= i)
                continue;

            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }

        // 蝶形运算
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            int step = n / len;

            for (int start = 0; start < n; start += len)
            {
                int k = 0;
                for (int j = 0; j < half; j++, k += step)
                {
                    float c = _cos[k];
                    float s = inverse ? _sin[k] : -_sin[k];

                    int a = start + j;
                    int b = a + half;

                    float xr = re[b] * c - im[b] * s;
                    float xi = re[b] * s + im[b] * c;

                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;

                    re[a] += xr;
                    im[a] += xi;
                }
            }
        }

        if (!inverse)
            return;

        float scale = 1f / n;
        for (int i = 0; i < n; i++)
        {
            re[i] *= scale;
            im[i] *= scale;
        }
    }

    private static int ReverseBits(int value, int bits)
    {
        int result = 0;

        for (int i = 0; i < bits; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
    }
}
