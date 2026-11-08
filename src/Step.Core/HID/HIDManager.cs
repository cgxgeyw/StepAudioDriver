namespace Step.Core.HID;

using HidSharp;

/// <summary>
/// USB HID 管理器
/// 处理 SK02 音量旋钮的 USB HID 读取 (HidSharp 2.6.x API)
/// </summary>
public class HIDManager : IDisposable
{
    // Fosi Audio SK02 VID/PID（实际值需要通过设备管理器确认）
    private const int FOSI_VENDOR_ID = 0x0D5C;
    private const int SK02_PRODUCT_ID = 0x0001;

    private HidDevice? _hidDevice;
    private HidStream? _hidStream;
    private Thread? _readThread;
    private bool _isListening;
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>
    /// 旋钮改变事件
    /// </summary>
    public event EventHandler<RotaryEncoderEventArgs>? RotaryChanged;

    /// <summary>
    /// 初始化 HID 管理器
    /// </summary>
    public void Initialize()
    {
        _hidDevice = FindSK02Device();

        if (_hidDevice == null)
        {
            throw new InvalidOperationException("未找到 SK02 HID 设备");
        }

        System.Diagnostics.Debug.WriteLine(
            $"找到 SK02 HID 设备: {_hidDevice.GetProductName()} " +
            $"(VID={_hidDevice.VendorID:X4} PID={_hidDevice.ProductID:X4})");
    }

    /// <summary>
    /// 启动 HID 监听
    /// </summary>
    public void StartListening()
    {
        if (_hidDevice == null)
            throw new InvalidOperationException("HID 设备未初始化");

        if (_isListening)
            return;

        try
        {
            // 打开 HID 流
            _hidStream = _hidDevice.Open();
            _hidStream.ReadTimeout = 100; // 100ms 超时

            _isListening = true;
            _cancellationTokenSource = new CancellationTokenSource();

            _readThread = new Thread(ReadHIDLoop)
            {
                IsBackground = true,
                Name = "SK02 HID Reader"
            };

            _readThread.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("打开 HID 流失败", ex);
        }
    }

    /// <summary>
    /// 停止 HID 监听
    /// </summary>
    public void StopListening()
    {
        _isListening = false;
        _cancellationTokenSource?.Cancel();
        _readThread?.Join(1000);

        _hidStream?.Close();
        _hidStream = null;
    }

    /// <summary>
    /// HID 读取循环
    /// </summary>
    private void ReadHIDLoop()
    {
        var buffer = new byte[64];

        while (_isListening && !(_cancellationTokenSource?.IsCancellationRequested ?? true))
        {
            try
            {
                int bytesRead = _hidStream!.Read(buffer);

                if (bytesRead > 0)
                {
                    ProcessHIDReport(buffer, bytesRead);
                }
            }
            catch (TimeoutException)
            {
                // 正常情况，没有旋钮输入时超时
                continue;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HID 读取错误: {ex.Message}");
                break;
            }
        }
    }

    /// <summary>
    /// 处理 HID 报告
    /// </summary>
    private void ProcessHIDReport(byte[] buffer, int length)
    {
        if (length < 2)
            return;

        // SK02 具体的报告格式需要抓包确认
        // 常见旋钮编码：第1字节=Report ID，第2字节=相对值
        byte data = buffer[1];
        int delta = ParseRotaryValue(data);

        if (delta != 0)
        {
            RotaryChanged?.Invoke(this, new RotaryEncoderEventArgs(delta));
        }
    }

    /// <summary>
    /// 解析旋钮值（相对编码）
    /// </summary>
    private int ParseRotaryValue(byte data)
    {
        // 有符号字节：0x01=顺时针, 0xFF=逆时针
        return (sbyte)data;
    }

    /// <summary>
    /// 查找 SK02 设备
    /// </summary>
    private HidDevice? FindSK02Device()
    {
        var deviceList = DeviceList.Local;

        // 1. 按 VID/PID 精确查找
        var device = deviceList.GetHidDeviceOrNull(
            vendorID: FOSI_VENDOR_ID,
            productID: SK02_PRODUCT_ID);

        if (device != null)
            return device;

        // 2. 按名称模糊查找
        foreach (var d in deviceList.GetHidDevices())
        {
            try
            {
                var productName = d.GetProductName();
                var manufacturer = d.GetManufacturer();

                if (productName?.Contains("SK02", StringComparison.OrdinalIgnoreCase) == true ||
                    productName?.Contains("Fosi", StringComparison.OrdinalIgnoreCase) == true ||
                    manufacturer?.Contains("Fosi", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return d;
                }
            }
            catch
            {
                // 部分设备读取名称可能失败，跳过
            }
        }

        return null;
    }

    /// <summary>
    /// 列出所有 HID 设备（调试用）
    /// </summary>
    public static IEnumerable<string> ListAllHIDDevices()
    {
        foreach (var d in DeviceList.Local.GetHidDevices())
        {
            string name = "Unknown";
            try { name = d.GetProductName() ?? "Unknown"; } catch { }
            yield return $"VID={d.VendorID:X4} PID={d.ProductID:X4} Name={name}";
        }
    }

    public bool IsListening => _isListening;

    public void Dispose()
    {
        StopListening();
        _cancellationTokenSource?.Dispose();
    }
}

/// <summary>
/// 旋钮编码器事件参数
/// </summary>
public class RotaryEncoderEventArgs : EventArgs
{
    /// <summary>正值=顺时针，负值=逆时针</summary>
    public int Delta { get; }

    public RotaryEncoderEventArgs(int delta) => Delta = delta;
}
