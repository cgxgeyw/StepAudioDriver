using System.Runtime.InteropServices;

namespace SK02.Core.Audio.HRTF;

/// <summary>
/// Rust 原生 HRTF 卷积核心（sk02_dsp.dll）。
/// DLL 不存在时回退到 C# 的 SpeakerConvolver。
/// </summary>
internal static unsafe class NativeHrtf
{
    private const string DllName = "sk02_dsp";

    private static readonly bool Available = Probe();

    public static bool IsAvailable => Available;

    public static IntPtr Create(int sampleRate, int blockSize)
    {
        if (!Available)
            return IntPtr.Zero;

        return sk02_hrtf_create((uint)sampleRate, (uint)blockSize);
    }

    public static bool Configure(
        IntPtr engine,
        int inputChannels,
        int speakerCount,
        int taps,
        float[] irs,
        float[] gains,
        int[] channelIndices)
    {
        if (engine == IntPtr.Zero ||
            irs.Length != speakerCount * 2 * taps ||
            gains.Length != speakerCount ||
            channelIndices.Length != speakerCount)
        {
            return false;
        }

        fixed (float* irsPtr = irs)
        fixed (float* gainsPtr = gains)
        fixed (int* channelsPtr = channelIndices)
        {
            return sk02_hrtf_configure(
                engine,
                (uint)inputChannels,
                (uint)speakerCount,
                (uint)taps,
                irsPtr,
                gainsPtr,
                (uint*)channelsPtr) == 0;
        }
    }

    public static bool Process(IntPtr engine, float[] input, float[] output)
    {
        if (engine == IntPtr.Zero)
            return false;

        fixed (float* inputPtr = input)
        fixed (float* outputPtr = output)
        {
            return sk02_hrtf_process(engine, inputPtr, outputPtr) == 0;
        }
    }

    public static void Destroy(IntPtr engine)
    {
        if (engine != IntPtr.Zero)
            sk02_hrtf_destroy(engine);
    }

    private static bool Probe()
    {
        if (Environment.GetEnvironmentVariable("SK02_DISABLE_NATIVE_HRTF") == "1")
            return false;

        if (!NativeLibrary.TryLoad($"{DllName}.dll", out _))
            return false;

        try
        {
            var engine = sk02_hrtf_create(48000, 128);
            if (engine == IntPtr.Zero)
                return false;

            sk02_hrtf_destroy(engine);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sk02_hrtf_create(uint sampleRate, uint blockSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sk02_hrtf_configure(
        IntPtr engine,
        uint inputChannels,
        uint speakerCount,
        uint taps,
        float* irs,
        float* gains,
        uint* channelIndices);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sk02_hrtf_process(IntPtr engine, float* input, float* output);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void sk02_hrtf_destroy(IntPtr engine);
}
