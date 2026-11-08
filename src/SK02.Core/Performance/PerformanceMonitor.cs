namespace SK02.Core.Performance;

using System.Diagnostics;

/// <summary>
/// 性能监控工具
///
/// 音频线程只调用 StartMeasure / StopMeasure（Stopwatch，极轻量）。
/// CPU 采样由 UI 线程定期调用 <see cref="UpdateCPUUsage"/> 触发，
/// 两次采样之间做差分，得到的是「当前」CPU 占用而不是进程生命周期平均值。
/// </summary>
public class PerformanceMonitor
{
    private const int SampleWindow = 60;

    private readonly Stopwatch _stopwatch = new();
    private readonly object _sync = new();
    private readonly Queue<float> _delaySamples = new(SampleWindow);

    private Process? _process;
    private float _cpuPercent;

    private long _lastCpuTimestamp;
    private TimeSpan _lastCpuTime;
    private bool _hasCpuBaseline;

    public PerformanceMonitor()
    {
        try
        {
            _process = Process.GetCurrentProcess();
        }
        catch
        {
            // 无法获取进程信息时，CPU 相关指标返回 0
        }
    }

    /// <summary>
    /// 开始计时（音频线程）
    /// </summary>
    public void StartMeasure()
    {
        _stopwatch.Restart();
    }

    /// <summary>
    /// 结束计时并记录处理耗时（音频线程）
    /// </summary>
    public float StopMeasure()
    {
        _stopwatch.Stop();
        float delayMs = (float)_stopwatch.Elapsed.TotalMilliseconds;

        lock (_sync)
        {
            _delaySamples.Enqueue(delayMs);
            if (_delaySamples.Count > SampleWindow)
            {
                _delaySamples.Dequeue();
            }
        }

        return delayMs;
    }

    /// <summary>
    /// 刷新 CPU 占用采样（建议 UI 每秒调用一次）
    /// </summary>
    public void UpdateCPUUsage()
    {
        if (_process == null)
            return;

        try
        {
            long now = Stopwatch.GetTimestamp();

            if (!_hasCpuBaseline)
            {
                _process.Refresh();
                _lastCpuTime = _process.TotalProcessorTime;
                _lastCpuTimestamp = now;
                _hasCpuBaseline = true;
                return;
            }

            double elapsedSeconds = (now - _lastCpuTimestamp) / (double)Stopwatch.Frequency;

            // 采样间隔太短时差值噪声很大，直接跳过
            if (elapsedSeconds < 0.5)
                return;

            _process.Refresh();
            TimeSpan totalProcessorTime = _process.TotalProcessorTime;
            double cpuSeconds = (totalProcessorTime - _lastCpuTime).TotalSeconds;

            _cpuPercent = (float)Math.Clamp(
                cpuSeconds / elapsedSeconds / Environment.ProcessorCount * 100.0,
                0.0, 100.0);

            _lastCpuTime = totalProcessorTime;
            _lastCpuTimestamp = now;
        }
        catch
        {
            // 忽略采样错误
        }
    }

    /// <summary>
    /// 平均处理延迟 (ms)
    /// </summary>
    public float GetAverageDelay()
    {
        lock (_sync)
        {
            return _delaySamples.Count == 0 ? 0 : _delaySamples.Average();
        }
    }

    /// <summary>
    /// 最大处理延迟 (ms)
    /// </summary>
    public float GetMaxDelay()
    {
        lock (_sync)
        {
            return _delaySamples.Count == 0 ? 0 : _delaySamples.Max();
        }
    }

    /// <summary>
    /// 最小处理延迟 (ms)
    /// </summary>
    public float GetMinDelay()
    {
        lock (_sync)
        {
            return _delaySamples.Count == 0 ? 0 : _delaySamples.Min();
        }
    }

    /// <summary>
    /// 最近一次采样得到的 CPU 占用百分比
    /// </summary>
    public float GetCPUUsagePercent() => _cpuPercent;

    /// <summary>
    /// 内存占用 (MB)
    /// </summary>
    public float GetMemoryUsageMB()
    {
        try
        {
            if (_process == null)
                return 0;

            _process.Refresh();
            return (float)(_process.WorkingSet64 / (1024.0 * 1024.0));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 完整性能报告（会顺带刷新 CPU 采样）
    /// </summary>
    public PerformanceReport GetReport()
    {
        UpdateCPUUsage();

        lock (_sync)
        {
            return new PerformanceReport
            {
                AverageDelayMs = _delaySamples.Count == 0 ? 0 : _delaySamples.Average(),
                MaxDelayMs = _delaySamples.Count == 0 ? 0 : _delaySamples.Max(),
                MinDelayMs = _delaySamples.Count == 0 ? 0 : _delaySamples.Min(),
                CPUUsagePercent = _cpuPercent,
                MemoryUsageMB = GetMemoryUsageMB(),
                SampleCount = _delaySamples.Count
            };
        }
    }

    /// <summary>
    /// 清空采样
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _delaySamples.Clear();
        }

        _cpuPercent = 0;
        _hasCpuBaseline = false;
    }
}

/// <summary>
/// 性能报告
/// </summary>
public class PerformanceReport
{
    /// <summary>平均处理延迟 (ms)</summary>
    public float AverageDelayMs { get; set; }

    /// <summary>最大处理延迟 (ms)</summary>
    public float MaxDelayMs { get; set; }

    /// <summary>最小处理延迟 (ms)</summary>
    public float MinDelayMs { get; set; }

    /// <summary>CPU 使用率 (%)</summary>
    public float CPUUsagePercent { get; set; }

    /// <summary>内存占用 (MB)</summary>
    public float MemoryUsageMB { get; set; }

    /// <summary>样本数量</summary>
    public int SampleCount { get; set; }

    /// <summary>
    /// 获取性能评级
    /// </summary>
    public string GetPerformanceRating()
    {
        if (AverageDelayMs < 5 && CPUUsagePercent < 5)
            return "⭐⭐⭐⭐⭐ 优秀";
        if (AverageDelayMs < 10 && CPUUsagePercent < 10)
            return "⭐⭐⭐⭐ 很好";
        if (AverageDelayMs < 20 && CPUUsagePercent < 15)
            return "⭐⭐⭐ 良好";
        if (AverageDelayMs < 50 && CPUUsagePercent < 25)
            return "⭐⭐ 一般";
        return "⭐ 需优化";
    }

    public override string ToString()
    {
        return $@"性能报告:
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
处理延迟:   {AverageDelayMs:F2} ms (avg) / {MaxDelayMs:F2} ms (max) / {MinDelayMs:F2} ms (min)
CPU 使用率: {CPUUsagePercent:F1}%
内存占用:   {MemoryUsageMB:F1} MB
评级:       {GetPerformanceRating()}
采样数:     {SampleCount}
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━";
    }
}
