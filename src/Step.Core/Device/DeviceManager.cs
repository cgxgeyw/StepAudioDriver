namespace Step.Core.Device;

using NAudio.CoreAudioApi;
using Step.Core.Models;

/// <summary>
/// 音频设备管理器
/// 枚举和管理 Windows 音频输出设备
/// </summary>
public class DeviceManager : IDisposable
{
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private AudioDevice? _selectedDevice;

    /// <summary>
    /// 设备改变事件
    /// </summary>
    public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    public DeviceManager()
    {
        _deviceEnumerator = new MMDeviceEnumerator();
    }

    /// <summary>
    /// 获取所有音频输出设备
    /// </summary>
    public List<AudioDevice> GetAudioDevices()
    {
        var devices = new List<AudioDevice>();

        try
        {
            var collection = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in collection)
            {
                var audioDevice = new AudioDevice
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    Description = device.DeviceFriendlyName,
                    IsDefault = device.ID == _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID
                };

                // 获取支持的格式
                audioDevice.SupportedFormats = GetSupportedFormats(device);

                devices.Add(audioDevice);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("获取音频设备列表失败", ex);
        }

        return devices;
    }

    /// <summary>
    /// 获取默认音频输出设备
    /// </summary>
    public AudioDevice? GetDefaultDevice()
    {
        try
        {
            var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var audioDevice = new AudioDevice
            {
                Id = device.ID,
                Name = device.FriendlyName,
                Description = device.DeviceFriendlyName,
                IsDefault = true,
                SupportedFormats = GetSupportedFormats(device)
            };

            return audioDevice;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("获取默认音频设备失败", ex);
        }
    }

    /// <summary>
    /// 选择音频设备
    /// </summary>
    public void SelectDevice(AudioDevice device)
    {
        _selectedDevice = device;
        DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(device));
    }

    /// <summary>
    /// 获取已选择的设备
    /// </summary>
    public AudioDevice? GetSelectedDevice() => _selectedDevice;

    /// <summary>
    /// 获取指定设备 ID 的设备
    /// </summary>
    public AudioDevice? GetDeviceById(string deviceId)
    {
        var devices = GetAudioDevices();
        return devices.FirstOrDefault(d => d.Id == deviceId);
    }

    /// <summary>
    /// 获取指定 MMDevice 支持的音频格式
    /// </summary>
    private List<AudioFormat> GetSupportedFormats(MMDevice device)
    {
        var formats = new List<AudioFormat>();

        try
        {
            // 标准 PCM 格式
            var standardFormats = new[]
            {
                (44100, 16), (44100, 24),
                (48000, 16), (48000, 24),
                (96000, 16), (96000, 24),
                (192000, 24)
            };

            foreach (var (sampleRate, bitDepth) in standardFormats)
            {
                // 尝试创建 WASAPI 客户端以验证格式支持
                // 简化：直接添加常见格式，实际应验证
                formats.Add(new AudioFormat
                {
                    SampleRate = sampleRate,
                    BitDepth = bitDepth,
                    Channels = 2
                });
            }
        }
        catch
        {
            // 如果无法获取确切格式，使用默认设置
            formats.Add(AudioFormat.StandardFormats.PCM_48000_24);
        }

        return formats;
    }

    public void Dispose()
    {
        _deviceEnumerator?.Dispose();
    }
}

/// <summary>
/// 设备改变事件参数
/// </summary>
public class DeviceChangedEventArgs : EventArgs
{
    public AudioDevice Device { get; }

    public DeviceChangedEventArgs(AudioDevice device)
    {
        Device = device;
    }
}
