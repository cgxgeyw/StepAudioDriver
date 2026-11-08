namespace Step.Core.Audio;

using NAudio.CoreAudioApi;
using NAudio.Wave;
using Step.Core.Models;

/// <summary>
/// WASAPI 实时音频处理器
/// 处理系统音频流，应用 HRTF、EQ 等处理
/// </summary>
public class WASAPIAudioProcessor : IDisposable
{
    private IWavePlayer? _wavePlayer;
    private IWaveProvider? _waveSource;  // NAudio IWaveProvider (CSCore IWaveSource 不兼容 .NET 8)
    private readonly AudioEngine _audioEngine;
    private readonly AudioDevice _device;

    private bool _isProcessing;
    private Thread? _processingThread;
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>
    /// 音频处理开始事件
    /// </summary>
    public event EventHandler<EventArgs>? ProcessingStarted;

    /// <summary>
    /// 音频处理停止事件
    /// </summary>
    public event EventHandler<EventArgs>? ProcessingStopped;

    /// <summary>
    /// 处理错误事件
    /// </summary>
    public event EventHandler<ExceptionEventArgs>? ProcessingError;

    public WASAPIAudioProcessor(AudioEngine audioEngine, AudioDevice device)
    {
        _audioEngine = audioEngine;
        _device = device;
    }

    /// <summary>
    /// 初始化音频处理
    /// </summary>
    public void Initialize()
    {
        try
        {
            // 获取 WASAPI 设备
            var enumerator = new MMDeviceEnumerator();
            var mmDevice = enumerator.GetDevice(_device.Id);

            if (mmDevice == null)
            {
                throw new InvalidOperationException($"无法找到设备: {_device.Name}");
            }

            // 创建 WASAPI 客户端
            // 注意：实际实现需要使用正确的音频接口
            // 这里简化为框架展示

            System.Diagnostics.Debug.WriteLine($"初始化音频设备: {_device.Name}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("初始化 WASAPI 处理器失败", ex);
        }
    }

    /// <summary>
    /// 启动实时处理
    /// </summary>
    public void Start()
    {
        if (_isProcessing)
        {
            return;
        }

        try
        {
            _isProcessing = true;
            _cancellationTokenSource = new CancellationTokenSource();

            // 启动处理线程
            _processingThread = new Thread(ProcessingLoop)
            {
                IsBackground = true,
                Name = "WASAPI Audio Processing Thread",
                Priority = ThreadPriority.AboveNormal
            };

            _processingThread.Start();
            ProcessingStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _isProcessing = false;
            ProcessingError?.Invoke(this, new ExceptionEventArgs(ex));
        }
    }

    /// <summary>
    /// 停止实时处理
    /// </summary>
    public void Stop()
    {
        if (!_isProcessing)
        {
            return;
        }

        _isProcessing = false;
        _cancellationTokenSource?.Cancel();

        if (_processingThread?.IsAlive == true)
        {
            _processingThread.Join(1000);
        }

        ProcessingStopped?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 音频处理循环
    /// </summary>
    private void ProcessingLoop()
    {
        try
        {
            const int blockSize = 256;
            var audioBuffer = new float[blockSize * 2]; // 立体声

            while (_isProcessing && _cancellationTokenSource?.Token.IsCancellationRequested == false)
            {
                try
                {
                    // 获取音频数据 (这是简化的示意)
                    // 实际实现需要从 WASAPI 读取真实音频数据

                    // 处理音频
                    var processedAudio = _audioEngine.ProcessAudio(audioBuffer);

                    // 输出到设备
                    // (实际实现需要写回 WASAPI)

                    // 小延迟以避免忙轮循
                    Thread.Sleep(1);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"处理循环错误: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            ProcessingError?.Invoke(this, new ExceptionEventArgs(ex));
        }
    }

    /// <summary>
    /// 获取处理性能统计
    /// </summary>
    public ProcessingStats GetStats()
    {
        return new ProcessingStats
        {
            IsProcessing = _isProcessing,
            ThreadName = _processingThread?.Name ?? "Unknown",
            ThreadPriority = _processingThread?.Priority ?? ThreadPriority.Normal
        };
    }

    public void Dispose()
    {
        Stop();
        _wavePlayer?.Dispose();
        _cancellationTokenSource?.Dispose();
    }
}

/// <summary>
/// 异常事件参数
/// </summary>
public class ExceptionEventArgs : EventArgs
{
    public Exception Exception { get; }

    public ExceptionEventArgs(Exception exception)
    {
        Exception = exception;
    }
}

/// <summary>
/// 处理统计信息
/// </summary>
public class ProcessingStats
{
    public bool IsProcessing { get; set; }
    public string ThreadName { get; set; } = string.Empty;
    public ThreadPriority ThreadPriority { get; set; }
    public float CPUUsagePercent { get; set; }
    public int BufferSize { get; set; }
    public int SampleRate { get; set; }
}
