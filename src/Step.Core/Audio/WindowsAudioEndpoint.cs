namespace Step.Core.Audio;

using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

/// <summary>
/// 修改 Windows 音频端点设置（不需要管理员权限）。
///
/// - 把虚拟声卡端点切成 7.1（8 声道）
/// - 把指定设备设为默认播放设备
///
/// 内部使用 Windows 的 IPolicyConfig（系统“声音设置”用的同一套接口）。
/// </summary>
public static class WindowsAudioEndpoint
{
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// 按名称片段查找播放设备 ID
    /// </summary>
    public static string? FindRenderDeviceId(string nameFragment)
    {
        using var enumerator = new MMDeviceEnumerator();

        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
            ?.ID;
    }

    /// <summary>
    /// 读取设备当前的混音声道数（用来验证设置是否生效）
    /// </summary>
    public static int? GetMixFormatChannels(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(deviceId).AudioClient.MixFormat.Channels;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 设置端点声道数（例如 8 = 7.1，6 = 5.1，2 = 立体声）
    /// </summary>
    public static bool TrySetChannelFormat(string deviceId, int channels, int sampleRate, out string error)
    {
        error = string.Empty;

        try
        {
            var policy = (IPolicyConfig)new CPolicyConfigClient();

            // 端点用 16-bit PCM，混音用 32-bit float（虚拟声卡对这两组组合最稳）
            var endpoint = BuildFormat(sampleRate, channels, 16, PcmSubFormat);
            var mix = BuildFormat(sampleRate, channels, 32, FloatSubFormat);

            IntPtr endpointPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEXTENSIBLE>());
            IntPtr mixPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEXTENSIBLE>());

            try
            {
                Marshal.StructureToPtr(endpoint, endpointPtr, false);
                Marshal.StructureToPtr(mix, mixPtr, false);

                int result = policy.SetDeviceFormat(deviceId, endpointPtr, mixPtr);

                if (result != 0)
                {
                    error = DescribeError(result);
                    return false;
                }

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(endpointPtr);
                Marshal.FreeHGlobal(mixPtr);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 读取设备友好名
    /// </summary>
    public static string? GetFriendlyName(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(deviceId).FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 是否 VB-CABLE 之类的虚拟线
    /// </summary>
    public static bool IsVirtualCable(string deviceId)
    {
        string? name = GetFriendlyName(deviceId);

        return name != null &&
               (name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 读取当前默认播放设备 ID（用于恢复）
    /// </summary>
    public static string? GetDefaultRenderDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把指定设备设为默认播放设备（控制台 / 多媒体 / 通信三个角色）
    /// </summary>
    public static bool TrySetDefaultDevice(string deviceId, out string error)
    {
        error = string.Empty;

        try
        {
            var policy = (IPolicyConfig)new CPolicyConfigClient();

            for (int role = 0; role <= 2; role++)
            {
                int result = policy.SetDefaultEndpoint(deviceId, role);

                if (result != 0)
                {
                    error = DescribeError(result);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string DescribeError(int hresult)
    {
        return hresult switch
        {
            unchecked((int)0x88890008) => "设备不支持该格式（0x88890008）",
            unchecked((int)0x8889000A) => "设备正被其它程序独占（0x8889000A）",
            _ => $"Windows 返回 0x{hresult:X8}"
        };
    }

    private static WAVEFORMATEXTENSIBLE BuildFormat(int sampleRate, int channels, int bits, Guid subFormat)
    {
        return new WAVEFORMATEXTENSIBLE
        {
            Format = new WAVEFORMATEX
            {
                wFormatTag = 0xFFFE,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = (ushort)bits,
                nBlockAlign = (ushort)(channels * bits / 8),
                nAvgBytesPerSec = (uint)(sampleRate * channels * bits / 8),
                cbSize = 22
            },
            wValidBitsPerSample = (ushort)bits,
            dwChannelMask = channels switch
            {
                2 => 0x3,
                4 => 0x33,
                6 => 0x3F,
                8 => 0x63F,
                16 => 0xFFFF,
                _ => 0x3
            },
            SubFormat = subFormat
        };
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class CPolicyConfigClient
    {
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, out IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceName, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pointer;
        public int intValue;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }
}
