using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using NAudio.Wave;
using Step.Core.Audio;
using Step.Core.Audio.DSP;
using Step.Core.Audio.HRTF;
using Step.Core.Config;
using Step.Core.HID;
using Step.Core.Models;

namespace Step.UI;

/// <summary>
/// Step 音频驱动 主窗口
/// </summary>
public partial class MainWindow : Window
{
    private PresetManager? _presetManager;
    private RealTimeAudioProcessor? _audioProcessor;
    private HIDManager? _hidManager;

    private DispatcherTimer? _statusTimer;
    private DirectionalTestPlayer? _testTonePlayer;
    private bool _isStartingAudio;
    private bool _isRecoveringAudio;
    private bool _syncingAutoStart;
    private bool _rememberCloseChoice;
    private string? _closeAction;
    private bool _forceClose;
    private bool _closeDialogOpen;
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ToolStripMenuItem? _trayAudioItem;
    private int _recoveryAttempts;
    private DateTime _recoveryWindowStart = DateTime.UtcNow;
    private string _selectedDeviceId = string.Empty;
    private string _selectedDeviceName = string.Empty;
    private string _selectedCaptureDeviceId = string.Empty;
    private WaveFormat? _requestedOutputFormat;
    private string? _previousDefaultDeviceId;
    private string? _managedCaptureDeviceId;
    private bool _defaultDeviceManagedByApp;
    private int _bufferMilliseconds = 100;
    private float _volumeLevel = 1.0f;

    private readonly double[] _eqBands = new double[10];
    private readonly double[] _eqFrequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private readonly double[] _eqQValues = { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 };

    // 频段控制条（代码生成，保证 10 列上下严格对齐）
    private readonly Slider[] _eqSliders = new Slider[10];
    private readonly TextBox[] _eqFreqBoxes = new TextBox[10];
    private readonly TextBox[] _eqDbBoxes = new TextBox[10];
    private readonly TextBox[] _eqQBoxes = new TextBox[10];

    private double _lowShelfFrequency = 100;
    private double _lowShelfGain;
    private double _lowShelfQ = 0.7;
    private double _highShelfFrequency = 10000;
    private double _highShelfGain;
    private double _highShelfQ = 0.7;

    private TextBox? _lowShelfFreqBox;
    private TextBox? _lowShelfGainBox;
    private TextBox? _lowShelfQBox;
    private TextBox? _highShelfFreqBox;
    private TextBox? _highShelfGainBox;
    private TextBox? _highShelfQBox;

    // 曲线只用于显示，用同一套 EqualizerEngine 计算真实幅频响应
    private readonly EqualizerEngine _curveEngine = new();

    private bool _syncingBandUi;
    private int _dragBand = -1;
    private int _hoverBand = -1;
    private int _selectedBand = -1;
    private static readonly HashSet<string> MusicPresetIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "vocal_male", "vocal_female", "ballad_rnb", "rock_indie", "metal", "dance_latin", "symphonic", "acoustic_acg"
    };

    private string? _currentPresetId;
    private readonly List<(Grid Row, Border Track, Border Fill, TextBlock Label, TextBlock Db)> _channelMeters = new();
    private readonly float[] _meterLevels = new float[16];
    private DispatcherTimer? _meterTimer;
    private DirectionOverlayWindow? _directionOverlay;

    public MainWindow()
    {
        InitializeComponent();

        _curveEngine.SetSampleRate(48000);
        BuildBandStrip();
        BuildShelfStrip();
        BuildChannelMeters();
        UpdateCurveEngine();
        InitializePresetsAtStartup();

        Loaded += MainWindow_Loaded;
        InitializeTrayIcon();

        if (Application.Current != null)
            Application.Current.SessionEnding += (_, _) => _forceClose = true;

#if DEBUG
        // 仅调试构建：--ui-selftest 用程序方式跑一遍预设 + 拖动逻辑，便于无人值守验证
        if (Environment.GetCommandLineArgs().Any(a => a == "--ui-selftest"))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) => { timer.Stop(); RunUiSelfTest(); };
            timer.Start();
        }
        // 仅调试构建：--ui-locktest 选中内置预设，验证只读状态
        if (Environment.GetCommandLineArgs().Any(a => a == "--ui-locktest"))
        {
            var lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            lockTimer.Tick += (_, _) =>
            {
                lockTimer.Stop();
                var builtIn = _presetManager?.GetPreset("gaming");
                if (builtIn != null)
                    ApplyPreset(builtIn);
            };
            lockTimer.Start();
        }
        // 仅调试构建：--ui-highrate 选 384 kHz 并直接启动，验证高采样率链路
        if (Environment.GetCommandLineArgs().Any(a => a == "--ui-highrate"))
        {
            var rateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            rateTimer.Tick += async (_, _) =>
            {
                rateTimer.Stop();
                MainTabs.SelectedIndex = 3;
                OutputFormatComboBox.SelectedIndex = 4; // 384 kHz · 24-bit
                await StartAudioAsync();
            };
            rateTimer.Start();
        }

        // 仅调试构建：--ui-autoconfig 验证一键 7.1 配置（不改默认播放设备，避免打断当前音频）
        if (Environment.GetCommandLineArgs().Any(a => a == "--ui-autoconfig"))
        {
            var configTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            configTimer.Tick += (_, _) =>
            {
                configTimer.Stop();
                MainTabs.SelectedIndex = 3;
                ChkSetDefaultDevice.IsChecked = false;
                BtnAutoConfig_Click(this, new RoutedEventArgs());
            };
            configTimer.Start();
        }

        // 仅调试构建：--ui-scrolltest 模拟滚轮，验证均衡器页滚动
        if (Environment.GetCommandLineArgs().Any(a => a == "--ui-scrolltest"))
        {
            var scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            scrollTimer.Tick += (_, _) =>
            {
                scrollTimer.Stop();

                var wheelArgs = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -4000)
                {
                    RoutedEvent = UIElement.MouseWheelEvent
                };

                EqTab_PreviewMouseWheel(this, wheelArgs);
            };
            scrollTimer.Start();
        }

        // 仅调试构建：--ui-newpreset 自动打开新建预设对话框，便于截图验证
        if (Environment.GetCommandLineArgs().Any(a => a == "--ui-newpreset"))
        {
            var presetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            presetTimer.Tick += (_, _) =>
            {
                presetTimer.Stop();
                BtnAddPreset_Click(this, new RoutedEventArgs());
            };
            presetTimer.Start();
        }

#endif
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 深色标题栏，避免系统浅色边框和界面割裂
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            int enabled = 1;

            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));

            // Windows 11：圆角窗口
            int cornerPreference = 2;
            DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
        }
        catch
        {
            // 旧版 Windows 不支持时保持系统默认
        }
    }

    private void ChkDspBypass_Changed(object sender, RoutedEventArgs e)
    {
        if (_audioProcessor != null)
            _audioProcessor.DspBypass = ChkDspBypass.IsChecked == true;
    }

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingAutoStart)
            return;

        try
        {
            string? executablePath = Environment.ProcessPath;

            if (string.IsNullOrWhiteSpace(executablePath) ||
                !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("当前不是以 exe 形式运行，无法设置开机自启动。");
            }

            StartupManager.SetEnabled(ChkAutoStart.IsChecked == true, executablePath);
            Log($"开机自启动：{(ChkAutoStart.IsChecked == true ? "已启用" : "已关闭")}");
        }
        catch (Exception ex)
        {
            _syncingAutoStart = true;
            ChkAutoStart.IsChecked = !(ChkAutoStart.IsChecked == true);
            _syncingAutoStart = false;

            MessageBox.Show($"设置开机自启动失败：{ex.Message}", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ChkDirectionOverlay_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkDirectionOverlay.IsChecked == true)
            ShowDirectionOverlay();
        else
            CloseDirectionOverlay();
    }

    private void ShowDirectionOverlay()
    {
        if (_directionOverlay != null)
        {
            if (!_directionOverlay.IsVisible)
                _directionOverlay.Show();

            return;
        }

        var saved = UiSettingsStore.Load();
        var work = SystemParameters.WorkArea;
        double screenLeft = SystemParameters.VirtualScreenLeft;
        double screenTop = SystemParameters.VirtualScreenTop;
        double screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        double screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        const double width = 200;
        const double height = 232;

        double left = saved?.OverlayLeft ?? (work.Right - width - 28);
        double top = saved?.OverlayTop ?? (work.Bottom - height - 28);

        left = Math.Clamp(left, screenLeft, Math.Max(screenLeft, screenRight - width));
        top = Math.Clamp(top, screenTop, Math.Max(screenTop, screenBottom - height));

        var overlay = new DirectionOverlayWindow
        {
            Left = left,
            Top = top
        };

        overlay.Closed += DirectionOverlay_Closed;
        _directionOverlay = overlay;
        overlay.Show();
    }

    private void CloseDirectionOverlay()
    {
        var overlay = _directionOverlay;
        _directionOverlay = null;
        overlay?.Close();
    }

    private void DirectionOverlay_Closed(object? sender, EventArgs e)
    {
        _directionOverlay = null;

        if (ChkDirectionOverlay != null && ChkDirectionOverlay.IsChecked == true)
            ChkDirectionOverlay.IsChecked = false;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (WindowState == WindowState.Normal)
        {
            try { DragMove(); }
            catch { /* 鼠标已抬起等情况忽略 */ }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        RequestClose();
    }

    private void BtnExitApp_Click(object sender, RoutedEventArgs e)
    {
        _forceClose = true;
        Close();
    }

    private void BtnResetCloseChoice_Click(object sender, RoutedEventArgs e)
    {
        _rememberCloseChoice = false;
        _closeAction = null;

        try { UiSettingsStore.Save(CaptureUiSettings()); }
        catch { /* 忽略 */ }
    }

    private void RequestClose()
    {
        if (_forceClose)
        {
            Close();
            return;
        }

        if (_rememberCloseChoice &&
            (_closeAction == "minimize" || _closeAction == "exit"))
        {
            ApplyCloseChoice(_closeAction);
            return;
        }

        if (_closeDialogOpen)
            return;

        _closeDialogOpen = true;

        try
        {
            var dialog = new CloseChoiceDialog { Owner = this };

            if (dialog.ShowDialog() != true)
                return;

            if (dialog.Remember)
            {
                _rememberCloseChoice = true;
                _closeAction = dialog.Choice;

                try { UiSettingsStore.Save(CaptureUiSettings()); }
                catch { /* 忽略 */ }
            }

            ApplyCloseChoice(dialog.Choice);
        }
        finally
        {
            _closeDialogOpen = false;
        }
    }

    private void ApplyCloseChoice(string? choice)
    {
        if (choice == "minimize")
        {
            HideToTray();
            return;
        }

        _forceClose = true;
        Close();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/step.ico"))?.Stream;

            if (stream == null)
                return;

            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = new Drawing.Icon(stream),
                Text = "Step 音频驱动",
                Visible = true
            };

            var menu = new WinForms.ContextMenuStrip();

            var openItem = new WinForms.ToolStripMenuItem("打开主界面");
            openItem.Click += (_, _) => RestoreFromTray();

            _trayAudioItem = new WinForms.ToolStripMenuItem("启动音频");
            _trayAudioItem.Click += (_, _) => ToggleAudioFromTray();

            var exitItem = new WinForms.ToolStripMenuItem("退出");
            exitItem.Click += (_, _) =>
            {
                _forceClose = true;
                Close();
            };

            menu.Items.Add(openItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(_trayAudioItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(exitItem);

            menu.Opening += (_, _) =>
            {
                if (_trayAudioItem != null)
                    _trayAudioItem.Text = _audioProcessor == null ? "启动音频" : "停止音频";
            };

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        }
        catch (Exception ex)
        {
            Log($"托盘图标初始化失败：{ex}");
        }
    }

    private void HideToTray()
    {
        if (_trayIcon == null)
        {
            // 极端情况下托盘初始化失败，退回任务栏最小化，至少不会丢窗口
            WindowState = WindowState.Minimized;
            return;
        }

        Hide();
    }

    private void RestoreFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
    }

    private async void ToggleAudioFromTray()
    {
        if (_audioProcessor != null)
            StopAudio();
        else
            await StartAudioAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            _ = Dispatcher.BeginInvoke(new Action(RequestClose), DispatcherPriority.Normal);
        }

        base.OnClosing(e);
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeGlyph == null)
            return;

        MaximizeGlyph.Data = System.Windows.Media.Geometry.Parse(
            WindowState == WindowState.Maximized
                ? "M 2.5,0.5 L 11.5,0.5 L 11.5,9.5 M 0.5,2.5 L 9.5,2.5 L 9.5,11.5"
                : "M 0.5,0.5 L 11.5,0.5 L 11.5,11.5 L 0.5,11.5 Z");
    }

#if DEBUG
    private void RunUiSelfTest()
    {
        // 1) 应用一个可编辑预设（验证预设选中与参数同步）
        var preset = _presetManager?.GetPreset("custom");
        if (preset != null)
            ApplyPreset(preset);

        // 2) 按拖动路径改一段 EQ
        var (left, top, plotWidth, plotHeight) = GetPlotRegion();

        const double targetFrequency = 700;
        const double targetGain = 6;

        double x = FrequencyToX(targetFrequency, left, plotWidth);
        double y = GainToY(targetGain, top, plotHeight);

        UpdateBandFromDrag(4, new Point(x, y));

        var response = _curveEngine.GetMagnitudeResponseDb(new[] { (float)targetFrequency });

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "step-ui-selftest.txt"),
            $"preset={_currentPresetId} freq={_eqFrequencies[4]:F0} gain={_eqBands[4]:F1} " +
            $"box='{_eqDbBoxes[4].Text}' freqBox='{_eqFreqBoxes[4].Text}' " +
            $"response700={response[0]:F2}dB selected={_selectedBand}");
    }
#endif
    private void InitializePresetsAtStartup()
    {
        try
        {
            var presetPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StepAudioDriver",
                "default_presets.json");

            // 首次启动时迁移旧版本的预设文件
            var legacyPresetPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SK02AudioDriver",
                "default_presets.json");

            if (!System.IO.File.Exists(presetPath) && System.IO.File.Exists(legacyPresetPath))
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(presetPath)!);
                System.IO.File.Copy(legacyPresetPath, presetPath);
            }

            _presetManager = new PresetManager(presetPath);

            // 在窗口显示前同步读完预设，避免启动瞬间列表是空的
            Task.Run(() => _presetManager.LoadPresetsAsync()).GetAwaiter().GetResult();

            PopulatePresetCards();

            var initialPreset = _presetManager.GetPreset("custom")
                ?? _presetManager.GetAllPresets().FirstOrDefault(p => !p.IsBuiltIn);

            if (initialPreset != null)
                ApplyPreset(initialPreset);
            else
                UpdatePresetEditability();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"预设初始化失败: {ex.Message}");
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        BtnStartStop.IsEnabled = false;

        try
        {
            // 预设已在构造函数里同步加载，这里只处理设备和上次设置的恢复

            // 设备枚举放到后台，避免启动时 UI 线程被 COM/驱动探测卡住
            var devices = await Task.Run(EnumerateRenderDevices);

            PopulateCaptureDeviceList(devices);
            PopulateDeviceList(devices);
            PopulateOutputFormats();
            PopulateBufferModes();

            // VB-CABLE 状态检查
            CheckVBCableStatus(devices);

            // 恢复上次的预设、EQ、设备和格式
            ApplySavedUiSettings();

            // 先把曲线画出来，再去做可能耗时的 HID 枚举
            UpdateCurveEngine();
            DrawEQCurve();

            // Canvas 尺寸变化时重绘
            if (EQCurveCanvas != null)
            {
                EQCurveCanvas.SizeChanged += (s, e) => DrawEQCurve();
            }

            // HID 枚举在部分机器上会阻塞数秒，放到后台线程
            _ = Task.Run(TryInitializeHID);

            // 仅调试构建：--ui-overlay 用于无需音频的悬浮窗截图/验证
#if DEBUG
            if (Environment.GetCommandLineArgs().Any(a => a == "--ui-settings"))
            {
                _ = Dispatcher.BeginInvoke(
                    new Action(() => MainTabs.SelectedIndex = 3),
                    DispatcherPriority.Loaded);
            }

            if (Environment.GetCommandLineArgs().Any(a => a == "--ui-overlay"))
                ChkDirectionOverlay.IsChecked = true;

            if (Environment.GetCommandLineArgs().Any(a => a == "--ui-overlay-demo"))
            {
                ChkDirectionOverlay.IsChecked = true;

                var demoLevels = new float[8];
                int[] order = { 0, 1, 2, 4, 5, 6, 7 };
                int demoIndex = 0;
                var demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                demoTimer.Tick += (_, _) =>
                {
                    Array.Clear(demoLevels);
                    demoLevels[order[demoIndex % order.Length]] = 0.3f;
                    demoIndex++;
                    _directionOverlay?.UpdateLevels(demoLevels, 8);
                };
                demoTimer.Start();
            }
#endif

            _syncingAutoStart = true;
            ChkAutoStart.IsChecked = StartupManager.IsEnabled();
            _syncingAutoStart = false;

            bool autostart = Environment.GetCommandLineArgs().Any(a => a == "--autostart");
            bool skipAutoAudio = Environment.GetCommandLineArgs().Any(a => a == "--ui-no-audio");

            if (!skipAutoAudio)
                await StartAudioAsync();

            if (autostart)
            {
                if (_audioProcessor != null)
                    HideToTray();
                else
                    Show();
            }

            // 布局完成后再画一次，修正初始尺寸
            _ = Dispatcher.BeginInvoke(new Action(() => DrawEQCurve()), DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败: {ex.Message}\n\n{ex.StackTrace}",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnStartStop.IsEnabled = true;
        }
    }

    #region 音频引擎

    private enum StatusKind
    {
        Idle,
        Pending,
        Running,
        Error
    }

    private async void BtnStartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_audioProcessor != null)
        {
            StopAudio();
            return;
        }

        await StartAudioAsync();
    }

    private async Task StartAudioAsync()
    {
        if (_isStartingAudio)
            return;

        _isStartingAudio = true;
        BtnStartStop.IsEnabled = false;
        SetStatus("正在打开音频设备…", StatusKind.Pending);

        if (!_isRecoveringAudio)
        {
            _recoveryAttempts = 0;
            _recoveryWindowStart = DateTime.UtcNow;
        }

        RealTimeAudioProcessor? processor = null;

        try
        {
            var device = new AudioDevice
            {
                Id = _selectedDeviceId,
                Name = _selectedDeviceName,
                Description = _selectedDeviceName,
                IsDefault = string.IsNullOrEmpty(_selectedDeviceId)
            };

            processor = new RealTimeAudioProcessor(
                device,
                ResolveCaptureDeviceId(),
                _requestedOutputFormat,
                preferExclusiveOutput: true,
                bufferMilliseconds: _bufferMilliseconds);
            processor.ProcessingError += OnProcessingError;
            processor.PipelineStopped += OnPipelineStopped;

            var target = processor;

            // 打开 WASAPI 端点可能耗时几百毫秒，放到后台线程避免界面卡顿
            await Task.Run(() => target.Initialize());

            // 控件值只能在 UI 线程读取，参数在启动前推给处理器
            ApplyRuntimeSettings(target);
            target.Start();

            _audioProcessor = target;
            processor = null; // 所有权移交给字段

            StartMeterTimer();
            UpdateOutputFormatStatus();

            string channelText = target.CaptureFormat is { } captureFormat
                ? $"{captureFormat.Channels} 声道 · "
                : string.Empty;

            BtnStartStop.Content = "■  停止音频";
            SetStatus(
                $"运行中 · {channelText}{ShortName(target.CaptureDeviceName)} → {ShortName(target.OutputDeviceName)}",
                StatusKind.Running,
                target.GetPipelineDescription());

            // 程序接管了默认播放设备时，启动后把系统声音导到当前捕获的虚拟线
            ApplyManagedDefaultDevice();

            StartStatusTimer();
            UpdatePerformanceText();
            Log($"音频启动：捕获={target.CaptureDeviceName}，输出={target.OutputDeviceName}，格式={target.OutputFormat}");
        }
        catch (Exception ex)
        {
            Log($"音频启动失败：{ex}");
            processor?.Dispose();

            BtnStartStop.Content = "▶  启动音频";
            SetStatus($"启动失败：{ex.Message}", StatusKind.Error, ex.ToString());
            PerfText.Text = string.Empty;
        }
        finally
        {
            BtnStartStop.IsEnabled = true;
            _isStartingAudio = false;
        }
    }

    private void StopAudio()
    {
        var processor = _audioProcessor;
        _audioProcessor = null;

        StopMeterTimer();

        _statusTimer?.Stop();
        PerfText.Text = string.Empty;

        if (processor != null)
        {
            processor.ProcessingError -= OnProcessingError;
            processor.PipelineStopped -= OnPipelineStopped;

            try { processor.Stop(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"停止音频失败: {ex.Message}"); }

            try { processor.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"释放音频设备失败: {ex.Message}"); }
        }

        UpdateOutputFormatStatus();
        RestoreManagedDefaultDevice();

        BtnStartStop.Content = "▶  启动音频";
        SetStatus("音频未启动", StatusKind.Idle);
    }

    private string? GetSelectedOutputDeviceId()
    {
        return (DeviceComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
    }

    /// <summary>
    /// 启动音频时自动把 Windows 默认播放切到当前捕获的虚拟线，
    /// 这样游戏/音乐不用手动改输出就能进入处理链路。
    /// </summary>
    private void ApplyManagedDefaultDevice()
    {
        string? captureId = ResolveCaptureDeviceId();

        // 选“自动”时按实际虚拟线补一个 ID
        if (string.IsNullOrEmpty(captureId))
        {
            captureId = WindowsAudioEndpoint.FindRenderDeviceId("CABLE In 16ch")
                     ?? WindowsAudioEndpoint.FindRenderDeviceId("CABLE Input");
        }

        if (string.IsNullOrEmpty(captureId) || !WindowsAudioEndpoint.IsVirtualCable(captureId))
            return;

        _managedCaptureDeviceId = captureId;
        _previousDefaultDeviceId ??= WindowsAudioEndpoint.GetDefaultRenderDeviceId();

        if (WindowsAudioEndpoint.TrySetDefaultDevice(captureId, out _))
            _defaultDeviceManagedByApp = true;
    }

    /// <summary>
    /// 停止时：把默认播放设备还回去，否则系统声音会一直进虚拟线（其它设备没声音）
    /// </summary>
    private void RestoreManagedDefaultDevice()
    {
        if (!_defaultDeviceManagedByApp)
            return;

        string? target = _previousDefaultDeviceId;

        // 之前的默认设备如果也是虚拟线，恢复过去仍然没声音，改回本程序的输出设备
        if (target != null && WindowsAudioEndpoint.IsVirtualCable(target))
            target = GetSelectedOutputDeviceId();

        if (string.IsNullOrEmpty(target))
            return;

        WindowsAudioEndpoint.TrySetDefaultDevice(target, out _);
    }

    private void EnsureManagedDefaultDevice()
    {
        if (ChkSetDefaultDevice.IsChecked != true || _audioProcessor == null)
            return;

        string? captureId = _managedCaptureDeviceId;
        if (string.IsNullOrEmpty(captureId))
        {
            captureId = WindowsAudioEndpoint.FindRenderDeviceId("CABLE In 16ch")
                     ?? WindowsAudioEndpoint.FindRenderDeviceId("CABLE Input");
            _managedCaptureDeviceId = captureId;
        }

        if (string.IsNullOrEmpty(captureId) || !WindowsAudioEndpoint.IsVirtualCable(captureId))
            return;

        string? current = WindowsAudioEndpoint.GetDefaultRenderDeviceId();
        if (string.IsNullOrEmpty(current) ||
            string.Equals(current, captureId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (WindowsAudioEndpoint.TrySetDefaultDevice(captureId, out _))
        {
            _defaultDeviceManagedByApp = true;
            Log($"默认播放设备被改走（{current}），已重新切换到 {captureId}");
        }
    }

    /// <summary>
    /// 显示实际协商出来的输出格式（避免用假的下拉列表误导）
    /// </summary>
    private void UpdateOutputFormatStatus()
    {
        if (_audioProcessor?.OutputFormat is not { } format)
        {
            FormatStatusText.Text = "未启动";
            FormatDetailText.Text = string.Empty;
            return;
        }

        string kind = format.Encoding == WaveFormatEncoding.IeeeFloat ? "float" : "PCM";
        string mode = _audioProcessor.OutputIsExclusive ? "独占" : "共享";

        FormatStatusText.Text =
            $"{format.SampleRate / 1000.0:0.#} kHz · {format.BitsPerSample}-bit {kind} · {format.Channels}ch · {mode}";

        var capture = _audioProcessor.CaptureFormat;

        string outputDevice = ShortName(_audioProcessor.OutputDeviceName);

        string captureText = capture == null
            ? "自动跟随捕获格式"
            : $"捕获 {capture.SampleRate / 1000.0:0.#} kHz / {capture.BitsPerSample}-bit / {capture.Channels}ch " +
              $"→ {outputDevice} · {format.SampleRate / 1000.0:0.#} kHz / {format.BitsPerSample}-bit / {format.Channels}ch";

        if (capture is { Channels: <= 2 })
        {
            captureText += "\n2 声道：7.1 未启用（游戏输出需设为 CABLE In 16ch，并在 Windows 声音设置里配置为 7.1）";
        }

        FormatDetailText.Text = captureText;
    }

    /// <summary>
    /// 处理器运行期错误：优先自动重建链路，连续失败才停止。
    /// </summary>
    private void OnProcessingError(object? sender, ProcessingErrorEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _ = RecoverAudioAsync(e.Exception, "音频处理异常");
        }));
    }

    private void OnPipelineStopped(object? sender, PipelineStoppedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _ = RecoverAudioAsync(e.Exception, e.Reason);
        }));
    }

    private async Task RecoverAudioAsync(Exception? exception, string reason)
    {
        if (_audioProcessor == null || _isStartingAudio || _isRecoveringAudio)
            return;

        _isRecoveringAudio = true;

        try
        {
            var now = DateTime.UtcNow;

            if ((now - _recoveryWindowStart).TotalSeconds > 60)
            {
                _recoveryAttempts = 0;
                _recoveryWindowStart = now;
            }

            _recoveryAttempts++;
            Log($"音频中断：{reason}，尝试自动恢复 #{_recoveryAttempts}。{exception}");

            if (_recoveryAttempts > 3)
            {
                StopAudio();
                SetStatus($"音频多次中断（{reason}），已停止。请检查设备和驱动后重新启动。",
                    StatusKind.Error, exception?.ToString());
                return;
            }

            SetStatus($"音频中断（{reason}），正在自动恢复…", StatusKind.Pending, exception?.ToString());

            StopAudio();
            await Task.Delay(700);

            if (_audioProcessor == null)
                await StartAudioAsync();

            if (_audioProcessor == null)
                SetStatus($"自动恢复失败（{reason}）", StatusKind.Error, exception?.ToString());
        }
        finally
        {
            _isRecoveringAudio = false;
        }
    }

    private static void Log(string message)
    {
        try
        {
            var directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StepAudioDriver");

            System.IO.Directory.CreateDirectory(directory);

            var path = System.IO.Path.Combine(directory, "step.log");
            var file = new System.IO.FileInfo(path);

            if (file.Exists && file.Length > 2 * 1024 * 1024)
                System.IO.File.WriteAllText(path, string.Empty);

            System.IO.File.AppendAllText(path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响音频
        }
    }

    /// <summary>
    /// 把界面上的所有可听参数推给处理器
    /// </summary>
    private void ApplyRuntimeSettings(RealTimeAudioProcessor processor)
    {
        for (int i = 0; i < 10; i++)
        {
            processor.SetEQBandParameters(i, (float)_eqFrequencies[i], (float)_eqQValues[i]);
            processor.SetEQBandGain(i, (float)_eqBands[i]);
        }

        processor.SetLowShelf((float)_lowShelfFrequency, (float)_lowShelfGain, (float)_lowShelfQ);
        processor.SetHighShelf((float)_highShelfFrequency, (float)_highShelfGain, (float)_highShelfQ);

        processor.SetHrtfEnabled(ChkHrtf.IsChecked == true);
        processor.SetHRTFIntensity((float)SliderHRTF.Value);
        processor.SetSurroundWidth((float)SliderSurround.Value);
        processor.DspBypass = ChkDspBypass.IsChecked == true;

        // EQ 前按最大正增益预衰减，避免 EQ 后峰值顶到限幅器（+1dB 余量）
        double maxBoost = Math.Max(_lowShelfGain, _highShelfGain);
        for (int i = 0; i < _eqBands.Length; i++)
            maxBoost = Math.Max(maxBoost, _eqBands[i]);

        float preamp = (float)Math.Pow(10, -Math.Max(0, maxBoost + 1) / 20);
        processor.SetPreampGain(preamp);

        processor.SetVolume(_volumeLevel);
    }

    private void StartStatusTimer()
    {
        if (_statusTimer == null)
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusTimer.Tick += (s, e) => UpdatePerformanceText();
        }

        _statusTimer.Start();
    }

    private void UpdatePerformanceText()
    {
        var processor = _audioProcessor;
        if (processor == null)
        {
            PerfText.Text = string.Empty;
            return;
        }

        if ((DateTime.UtcNow - _recoveryWindowStart).TotalSeconds > 60)
            _recoveryAttempts = 0;

        // 端点停止事件偶尔会丢，这里再做一层兜底
        if (processor.IsRunning && !processor.IsCaptureActive && !_isRecoveringAudio)
            _ = RecoverAudioAsync(null, "捕获端点停止");

        EnsureManagedDefaultDevice();

        try
        {
            var report = processor.GetPerformanceReport();
            var buffer = processor.GetBufferStatus();

            PerfText.Text =
                $"延迟 {report.AverageDelayMs:F1} ms · CPU {report.CPUUsagePercent:F0}% · 缓冲 {buffer.FillPercentage:F0}%";
        }
        catch
        {
            // 统计失败不影响音频
        }
    }

    private void SetStatus(string text, StatusKind kind, string? tooltip = null)
    {
        StatusText.Text = text;
        StatusText.ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip;

        string dotBrushKey = kind switch
        {
            StatusKind.Running => "BrGood",
            StatusKind.Error => "BrWarn",
            StatusKind.Pending => "BrAccent",
            _ => "BrText3"
        };

        StatusDot.Fill = (SolidColorBrush)FindResource(dotBrushKey);

        StatusText.Foreground = kind switch
        {
            StatusKind.Running => (SolidColorBrush)FindResource("BrGood"),
            StatusKind.Error => (SolidColorBrush)FindResource("BrWarn"),
            StatusKind.Pending => (SolidColorBrush)FindResource("BrAccent"),
            _ => (SolidColorBrush)FindResource("BrText2")
        };
    }

    /// <summary>
    /// "CABLE Input (VB-Audio Virtual Cable)" → "CABLE Input"
    /// </summary>
    private static string ShortName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return "?";

        int index = deviceName.IndexOf(" (", StringComparison.Ordinal);
        return index > 0 ? deviceName[..index] : deviceName;
    }

    #endregion

    #region 声道电平

    private void BuildChannelMeters()
    {
        if (ChannelMetersPanel == null)
            return;

        var textMuted = (SolidColorBrush)FindResource("BrText3");
        var trackBrush = (SolidColorBrush)FindResource("BrTrack");
        var accent = (SolidColorBrush)FindResource("BrAccent");
        string[] labels = ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"];

        ChannelMetersPanel.Children.Clear();
        _channelMeters.Clear();

        for (int i = 0; i < labels.Length; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

            var label = new TextBlock
            {
                Text = labels[i],
                FontSize = 12.5,
                Foreground = textMuted,
                VerticalAlignment = VerticalAlignment.Center
            };

            var track = new Border
            {
                Height = 10,
                Background = trackBrush,
                CornerRadius = new CornerRadius(5),
                VerticalAlignment = VerticalAlignment.Center
            };

            var fill = new Border
            {
                Height = 10,
                Background = accent,
                CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0
            };

            track.Child = fill;
            Grid.SetColumn(track, 1);

            var db = new TextBlock
            {
                Text = "—",
                FontSize = 12,
                Foreground = textMuted,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(db, 2);

            row.Children.Add(label);
            row.Children.Add(track);
            row.Children.Add(db);

            ChannelMetersPanel.Children.Add(row);
            _channelMeters.Add((row, track, fill, label, db));
        }

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) => UpdateChannelMeters();
    }

    private void StartMeterTimer()
    {
        _meterTimer?.Start();
        UpdateChannelMeters();
    }

    private void StopMeterTimer()
    {
        _meterTimer?.Stop();
        _directionOverlay?.Clear();

        foreach (var meter in _channelMeters)
        {
            meter.Fill.Width = 0;
            meter.Db.Text = "—";
        }
    }

    private void UpdateChannelMeters()
    {
        var processor = _audioProcessor;

        if (processor == null)
        {
            StopMeterTimer();
            return;
        }

        processor.CopyChannelLevels(_meterLevels);
        int channels = processor.CaptureChannelCount;
        _directionOverlay?.UpdateLevels(_meterLevels, channels);
        var labels = GetChannelLabels(channels);

        for (int i = 0; i < _channelMeters.Count; i++)
        {
            var meter = _channelMeters[i];
            bool active = channels > 0 && i < channels;

            meter.Row.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

            if (!active)
                continue;

            meter.Label.Text = labels[i];

            float level = _meterLevels[i];
            double db = level > 1e-5f ? 20 * Math.Log10(level) : -100;
            double normalized = Math.Clamp((db + 60) / 60, 0, 1);
            double trackWidth = Math.Max(1, meter.Track.ActualWidth - 2);

            meter.Fill.Width = normalized * trackWidth;
            meter.Db.Text = level > 1e-5f ? db.ToString("0") : "—";
        }
    }

    private static string[] GetChannelLabels(int channels)
    {
        return channels switch
        {
            1 => ["M"],
            2 => ["FL", "FR"],
            4 => ["FL", "FR", "BL", "BR"],
            6 => ["FL", "FR", "FC", "LFE", "BL", "BR"],
            8 => ["FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR"],
            _ => Enumerable.Range(1, Math.Max(1, channels)).Select(i => $"CH{i}").ToArray()
        };
    }

    #endregion

    #region 预设卡片

    /// <summary>
    /// 均衡器页任意位置滚轮都滚动预设列表（列表较长，不要求指针停在左侧）
    /// </summary>
    private void EqTab_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PresetScrollViewer.ScrollableHeight <= 0)
            return;

        double offset = PresetScrollViewer.VerticalOffset - e.Delta / 3.0;

        PresetScrollViewer.ScrollToVerticalOffset(
            Math.Clamp(offset, 0, PresetScrollViewer.ScrollableHeight));

        e.Handled = true;
    }

    private void PopulatePresetCards()
    {
        if (_presetManager == null)
            return;

        PresetCardsPanel.Children.Clear();

        var presets = _presetManager.GetAllPresets().ToList();
        var musicPresets = presets.Where(p => MusicPresetIds.Contains(p.Id)).ToList();
        bool musicGroupAdded = false;

        foreach (var preset in presets)
        {
            if (MusicPresetIds.Contains(preset.Id))
            {
                if (!musicGroupAdded)
                {
                    PresetCardsPanel.Children.Add(CreateMusicGroupBox(musicPresets));
                    musicGroupAdded = true;
                }

                continue;
            }

            PresetCardsPanel.Children.Add(CreatePresetCard(preset));
        }
    }

    /// <summary>
    /// 用线框把音乐预设框在一起（不折叠，全部可见）
    /// </summary>
    private Border CreateMusicGroupBox(IReadOnlyList<Preset> musicPresets)
    {
        var borderBrush = (SolidColorBrush)FindResource("BrBorder");
        var textMuted = (SolidColorBrush)FindResource("BrText3");

        var frame = new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 8, 6, 6),
            Margin = new Thickness(0, 8, 0, 6)
        };

        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = "音乐",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = textMuted,
            Margin = new Thickness(6, 0, 0, 5)
        });

        foreach (var preset in musicPresets)
            panel.Children.Add(CreatePresetCard(preset));

        frame.Child = panel;
        return frame;
    }

    private Border CreatePresetCard(Preset preset)
    {
        bool isActive = preset.Id == _currentPresetId;

        var accent = (SolidColorBrush)FindResource("BrAccent");
        var textPrimary = (SolidColorBrush)FindResource("BrText1");
        var textMuted = (SolidColorBrush)FindResource("BrText3");
        var accentDim = (SolidColorBrush)FindResource("BrAccentDim");

        var card = new Border
        {
            Background = isActive ? accentDim : Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 3),
            Padding = new Thickness(12, 10, 12, 10),
            Cursor = Cursors.Hand
        };

        var stack = new StackPanel();

        var header = new Grid();

        header.Children.Add(new TextBlock
        {
            Text = preset.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = isActive ? accent : textPrimary,
            TextWrapping = TextWrapping.Wrap,
            Margin = preset.IsBuiltIn ? new Thickness(0, 0, 34, 0) : new Thickness(0)
        });

        if (preset.IsBuiltIn)
        {
            header.Children.Add(new TextBlock
            {
                Text = "只读",
                FontSize = 10,
                Foreground = textMuted,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        stack.Children.Add(header);

        if (!string.IsNullOrWhiteSpace(preset.Description))
        {
            stack.Children.Add(new TextBlock
            {
                Text = preset.Description,
                FontSize = 11.5,
                Foreground = textMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }

        card.Child = stack;

        card.MouseEnter += (_, _) =>
        {
            if (preset.Id != _currentPresetId)
                card.Background = (SolidColorBrush)FindResource("BrHover");
        };

        card.MouseLeave += (_, _) =>
        {
            card.Background = preset.Id == _currentPresetId
                ? accentDim
                : Brushes.Transparent;
        };

        card.MouseLeftButtonDown += (_, _) => ApplyPreset(preset);

        return card;
    }

    private void ApplyPreset(Preset preset)
    {
        try
        {
            _currentPresetId = preset.Id;
            _presetManager?.SelectPreset(preset.Id);

            _syncingBandUi = true;

            // 预设自己的频率/Q，而不是沿用上一个预设
            for (int i = 0; i < 10; i++)
            {
                if (preset.EQFrequencies is { Length: 10 })
                    _eqFrequencies[i] = Math.Clamp(preset.EQFrequencies[i], 20, 20000);

                if (preset.EQQValues is { Length: 10 })
                    _eqQValues[i] = Math.Clamp(preset.EQQValues[i], 0.5, 10);

                if (_eqFreqBoxes[i] != null)
                    _eqFreqBoxes[i].Text = _eqFrequencies[i].ToString("0");

                if (_eqQBoxes[i] != null)
                    _eqQBoxes[i].Text = _eqQValues[i].ToString("0.0");
            }

            // BassBoost 是低频倍率：折进 32/64Hz 两个低架，保证实时链路真的生效
            float bassBoost = Math.Clamp(preset.BassBoost, 0.5f, 2.0f);

            for (int i = 0; i < 10; i++)
            {
                double gain = i < 2 ? preset.EQBands[i] * bassBoost : preset.EQBands[i];
                _eqBands[i] = Math.Clamp(gain, -12, 12);

                if (_eqSliders[i] != null)
                    _eqSliders[i].Value = _eqBands[i];

                if (_eqDbBoxes[i] != null)
                    _eqDbBoxes[i].Text = _eqBands[i].ToString("0.0");
            }

            _lowShelfFrequency = Math.Clamp(preset.LowShelfFrequency, 20, 20000);
            _lowShelfGain = Math.Clamp(preset.LowShelfGain, -12, 12);
            _lowShelfQ = Math.Clamp(preset.LowShelfQ, 0.4, 4);
            _highShelfFrequency = Math.Clamp(preset.HighShelfFrequency, 20, 20000);
            _highShelfGain = Math.Clamp(preset.HighShelfGain, -12, 12);
            _highShelfQ = Math.Clamp(preset.HighShelfQ, 0.4, 4);

            if (_lowShelfFreqBox != null) _lowShelfFreqBox.Text = _lowShelfFrequency.ToString("0");
            if (_lowShelfGainBox != null) _lowShelfGainBox.Text = _lowShelfGain.ToString("0.0");
            if (_lowShelfQBox != null) _lowShelfQBox.Text = _lowShelfQ.ToString("0.0");
            if (_highShelfFreqBox != null) _highShelfFreqBox.Text = _highShelfFrequency.ToString("0");
            if (_highShelfGainBox != null) _highShelfGainBox.Text = _highShelfGain.ToString("0.0");
            if (_highShelfQBox != null) _highShelfQBox.Text = _highShelfQ.ToString("0.0");

            _syncingBandUi = false;

            SliderHRTF.Value = Math.Clamp(preset.HRTFIntensity, 0.5, 2.0);
            SliderSurround.Value = Math.Clamp(preset.SurroundWidth, 60, 180);

            PresetNameText.Text = preset.Name;

            UpdateCurveEngine();

            if (_audioProcessor != null)
                ApplyRuntimeSettings(_audioProcessor);

            PopulatePresetCards();
            DrawEQCurve();
            UpdatePresetEditability();
        }
        catch (Exception ex)
        {
            _syncingBandUi = false;
            MessageBox.Show($"应用预设失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 当前预设是否允许编辑：只有“自定义”和用户新建的预设可编辑
    /// </summary>
    private bool IsCurrentPresetEditable()
    {
        if (_presetManager == null || string.IsNullOrEmpty(_currentPresetId))
            return true;

        var preset = _presetManager.GetPreset(_currentPresetId);
        return preset == null || !preset.IsBuiltIn;
    }

    /// <summary>
    /// 根据当前预设切换 EQ 控件可用状态
    /// </summary>
    private void UpdatePresetEditability()
    {
        var preset = string.IsNullOrEmpty(_currentPresetId)
            ? null
            : _presetManager?.GetPreset(_currentPresetId);

        bool editable = preset == null || !preset.IsBuiltIn;

        BandStrip.IsEnabled = editable;
        BandStrip.Opacity = editable ? 1.0 : 0.4;
        ShelfStrip.IsEnabled = editable;
        ShelfStrip.Opacity = editable ? 1.0 : 0.4;

        EQCurveCanvas.IsHitTestVisible = editable;
        EQCurveCanvas.Cursor = editable ? Cursors.Cross : Cursors.Arrow;
        EQCurveCanvas.Opacity = editable ? 1.0 : 0.6;

        BtnSaveEQ.IsEnabled = editable && preset != null;
        BtnResetEQ.IsEnabled = editable;

        LockHint.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;

        CurveHint.Text = editable
            ? "曲线为 10 段 + 搁架总响应；拖动控制点调整该段频率/增益"
            : "内置预设只读，切换到「自定义」后可调整";
    }

    #endregion

    #region 均衡器

    /// <summary>
    /// 生成 10 列频段控制条：频率 / 垂直滑块 / 增益 / Q 严格同列对齐
    /// </summary>
    private void BuildBandStrip()
    {
        BandStrip.Children.Clear();
        BandStrip.ColumnDefinitions.Clear();
        BandStrip.RowDefinitions.Clear();

        for (int i = 0; i < 10; i++)
            BandStrip.ColumnDefinitions.Add(new ColumnDefinition());

        BandStrip.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        BandStrip.RowDefinitions.Add(new RowDefinition { Height = new GridLength(150) });
        BandStrip.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        BandStrip.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

        var numericStyle = (Style)FindResource("NumericBox");
        var sliderStyle = (Style)FindResource("VSlider");
        var textMuted = (SolidColorBrush)FindResource("BrText3");

        for (int i = 0; i < 10; i++)
        {
            int band = i;

            var freqBox = new TextBox
            {
                Style = numericStyle,
                Text = _eqFrequencies[i].ToString("0"),
                Width = 72,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "中心频率 (Hz)"
            };
            freqBox.LostFocus += (_, _) => CommitFrequency(band);
            freqBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                CommitFrequency(band);
                Keyboard.ClearFocus();
            };

            var slider = new Slider
            {
                Style = sliderStyle,
                Orientation = Orientation.Vertical,
                Minimum = -12,
                Maximum = 12,
                Value = _eqBands[i],
                Width = 28,
                Margin = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                ToolTip = "增益 (dB)"
            };
            slider.ValueChanged += (_, e) => EQSlider_Changed(band, e.NewValue);

            var dbBox = new TextBox
            {
                Style = numericStyle,
                Text = _eqBands[i].ToString("0.0"),
                Width = 72,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "增益 (dB)"
            };
            dbBox.LostFocus += (_, _) => CommitGain(band);
            dbBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                CommitGain(band);
                Keyboard.ClearFocus();
            };

            var qBox = new TextBox
            {
                Style = numericStyle,
                Text = _eqQValues[i].ToString("0.0"),
                Width = 58,
                Height = 26,
                FontSize = 12,
                Foreground = textMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip = "Q 值（0.5–10，每段独立）"
            };
            qBox.LostFocus += (_, _) => CommitQ(band);
            qBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                CommitQ(band);
                Keyboard.ClearFocus();
            };

            Grid.SetRow(freqBox, 0);
            Grid.SetColumn(freqBox, i);
            Grid.SetRow(slider, 1);
            Grid.SetColumn(slider, i);
            Grid.SetRow(dbBox, 2);
            Grid.SetColumn(dbBox, i);
            Grid.SetRow(qBox, 3);
            Grid.SetColumn(qBox, i);

            BandStrip.Children.Add(freqBox);
            BandStrip.Children.Add(slider);
            BandStrip.Children.Add(dbBox);
            BandStrip.Children.Add(qBox);

            _eqFreqBoxes[i] = freqBox;
            _eqSliders[i] = slider;
            _eqDbBoxes[i] = dbBox;
            _eqQBoxes[i] = qBox;
        }
    }

    private void BuildShelfStrip()
    {
        ShelfStrip.Children.Clear();
        ShelfStrip.ColumnDefinitions.Clear();

        for (int i = 0; i < 2; i++)
            ShelfStrip.ColumnDefinitions.Add(new ColumnDefinition());

        var numericStyle = (Style)FindResource("NumericBox");
        var textMuted = (SolidColorBrush)FindResource("BrText3");

        for (int shelf = 0; shelf < 2; shelf++)
        {
            bool low = shelf == 0;

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 0)
            };

            panel.Children.Add(new TextBlock
            {
                Text = low ? "低频搁架" : "高频搁架",
                Width = 62,
                FontSize = 11.5,
                Foreground = textMuted,
                VerticalAlignment = VerticalAlignment.Center
            });

            TextBox MakeBox(double value, double width, string tooltip)
            {
                var box = new TextBox
                {
                    Style = numericStyle,
                    Text = value.ToString("0.0"),
                    Width = width,
                    Height = 26,
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                    ToolTip = tooltip
                };
                box.LostFocus += (_, _) => CommitShelf(low);
                box.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    CommitShelf(low);
                    Keyboard.ClearFocus();
                };
                return box;
            }

            var freqBox = MakeBox(
                low ? _lowShelfFrequency : _highShelfFrequency,
                62,
                "搁架频率 (Hz)");

            panel.Children.Add(freqBox);
            panel.Children.Add(UnitLabel("Hz", textMuted));

            var gainBox = MakeBox(
                low ? _lowShelfGain : _highShelfGain,
                52,
                "搁架增益 (dB)");

            panel.Children.Add(gainBox);
            panel.Children.Add(UnitLabel("dB", textMuted));

            var qBox = MakeBox(
                low ? _lowShelfQ : _highShelfQ,
                48,
                "搁架 Q（独立于各频段）");

            panel.Children.Add(qBox);
            panel.Children.Add(UnitLabel("Q", textMuted));

            Grid.SetColumn(panel, shelf);
            ShelfStrip.Children.Add(panel);

            if (low)
            {
                _lowShelfFreqBox = freqBox;
                _lowShelfGainBox = gainBox;
                _lowShelfQBox = qBox;
            }
            else
            {
                _highShelfFreqBox = freqBox;
                _highShelfGainBox = gainBox;
                _highShelfQBox = qBox;
            }
        }
    }

    private static TextBlock UnitLabel(string text, SolidColorBrush brush)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
    }

    private void CommitShelf(bool low)
    {
        var freqBox = low ? _lowShelfFreqBox : _highShelfFreqBox;
        var gainBox = low ? _lowShelfGainBox : _highShelfGainBox;
        var qBox = low ? _lowShelfQBox : _highShelfQBox;

        if (freqBox == null || gainBox == null || qBox == null)
            return;

        if (!double.TryParse(freqBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double frequency))
            frequency = low ? _lowShelfFrequency : _highShelfFrequency;

        if (!double.TryParse(gainBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double gain))
            gain = low ? _lowShelfGain : _highShelfGain;

        if (!double.TryParse(qBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double q))
            q = low ? _lowShelfQ : _highShelfQ;

        frequency = Math.Clamp(frequency, 20, 20000);
        gain = Math.Clamp(gain, -12, 12);
        q = Math.Clamp(q, 0.4, 4);

        freqBox.Text = frequency.ToString("0");
        gainBox.Text = gain.ToString("0.0");
        qBox.Text = q.ToString("0.0");

        if (low)
        {
            _lowShelfFrequency = frequency;
            _lowShelfGain = gain;
            _lowShelfQ = q;
            _audioProcessor?.SetLowShelf((float)frequency, (float)gain, (float)q);
        }
        else
        {
            _highShelfFrequency = frequency;
            _highShelfGain = gain;
            _highShelfQ = q;
            _audioProcessor?.SetHighShelf((float)frequency, (float)gain, (float)q);
        }

        UpdateCurveEngine();
        DrawEQCurve();
    }

    /// <summary>
    /// 把当前 10 段参数同步到用于画曲线的引擎
    /// </summary>
    private void UpdateCurveEngine()
    {
        var gains = new float[10];
        var freqs = new float[10];
        var qs = new float[10];

        for (int i = 0; i < 10; i++)
        {
            gains[i] = (float)_eqBands[i];
            freqs[i] = (float)_eqFrequencies[i];
            qs[i] = (float)_eqQValues[i];
        }

        _curveEngine.SetAllBands(gains, freqs, qs);
        _curveEngine.SetLowShelf((float)_lowShelfFrequency, (float)_lowShelfGain, (float)_lowShelfQ);
        _curveEngine.SetHighShelf((float)_highShelfFrequency, (float)_highShelfGain, (float)_highShelfQ);
    }

    private void CommitFrequency(int band)
    {
        if (!IsCurrentPresetEditable())
            return;

        var box = _eqFreqBoxes[band];
        if (box == null) return;

        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double freq))
        {
            box.Text = _eqFrequencies[band].ToString("0");
            return;
        }

        double lower = band == 0 ? 20 : _eqFrequencies[band - 1] * 1.03;
        double upper = band == 9 ? 20000 : _eqFrequencies[band + 1] / 1.03;

        freq = Math.Clamp(freq, Math.Max(20, lower), Math.Min(20000, upper));
        _eqFrequencies[band] = freq;
        box.Text = freq.ToString("0");

        _audioProcessor?.SetEQBandParameters(band, (float)freq, (float)_eqQValues[band]);
        UpdateCurveEngine();
        DrawEQCurve();
    }

    private void CommitGain(int band)
    {
        var box = _eqDbBoxes[band];
        if (box == null) return;

        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double gain))
        {
            box.Text = _eqBands[band].ToString("0.0");
            return;
        }

        ApplyGain(band, gain);
    }

    private void CommitQ(int band)
    {
        if (!IsCurrentPresetEditable())
            return;

        var box = _eqQBoxes[band];
        if (box == null) return;

        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double q))
        {
            box.Text = _eqQValues[band].ToString("0.0");
            return;
        }

        q = Math.Clamp(q, 0.5, 10);
        _eqQValues[band] = q;
        box.Text = q.ToString("0.0");

        _audioProcessor?.SetEQBandParameters(band, (float)_eqFrequencies[band], (float)q);
        UpdateCurveEngine();
        DrawEQCurve();
    }

    private void ApplyGain(int band, double gain)
    {
        if (!IsCurrentPresetEditable())
            return;

        gain = Math.Clamp(gain, -12, 12);
        _eqBands[band] = gain;

        _syncingBandUi = true;

        if (_eqSliders[band] != null)
            _eqSliders[band].Value = gain;

        if (_eqDbBoxes[band] != null)
            _eqDbBoxes[band].Text = gain.ToString("0.0");

        _syncingBandUi = false;

        _audioProcessor?.SetEQBandGain(band, (float)gain);
        UpdateCurveEngine();
        DrawEQCurve();
    }

    private void EQSlider_Changed(int band, double value)
    {
        if (_syncingBandUi)
            return;

        if (!IsCurrentPresetEditable())
            return;

        _eqBands[band] = Math.Clamp(value, -12, 12);

        if (_eqDbBoxes[band] != null)
            _eqDbBoxes[band].Text = _eqBands[band].ToString("0.0");

        _audioProcessor?.SetEQBandGain(band, (float)_eqBands[band]);
        UpdateCurveEngine();
        DrawEQCurve();
    }

    /// <summary>
    /// 拖动曲线时一次性同步滑块、输入框和音频参数
    /// </summary>
    private void SyncBandFromDrag(int band)
    {
        _syncingBandUi = true;

        if (_eqSliders[band] != null)
            _eqSliders[band].Value = _eqBands[band];

        if (_eqDbBoxes[band] != null)
            _eqDbBoxes[band].Text = _eqBands[band].ToString("0.0");

        if (_eqFreqBoxes[band] != null)
            _eqFreqBoxes[band].Text = _eqFrequencies[band].ToString("0");

        _syncingBandUi = false;

        _audioProcessor?.SetEQBandParameters(band, (float)_eqFrequencies[band], (float)_eqQValues[band]);
        _audioProcessor?.SetEQBandGain(band, (float)_eqBands[band]);
    }

    private const double MinPlotFreq = 20;
    private const double MaxPlotFreq = 20000;
    private const double PlotDbRange = 18;

    private (double Left, double Top, double Width, double Height) GetPlotRegion()
    {
        double width = EQCurveCanvas.ActualWidth;
        double height = EQCurveCanvas.ActualHeight;

        const double left = 42;
        const double right = 14;
        const double top = 16;
        const double bottom = 26;

        return (left, top,
            Math.Max(10, width - left - right),
            Math.Max(10, height - top - bottom));
    }

    private static double FrequencyToX(double frequency, double left, double width)
    {
        double logMin = Math.Log10(MinPlotFreq);
        double logMax = Math.Log10(MaxPlotFreq);
        return left + (Math.Log10(frequency) - logMin) / (logMax - logMin) * width;
    }

    private static double XToFrequency(double x, double left, double width)
    {
        double logMin = Math.Log10(MinPlotFreq);
        double logMax = Math.Log10(MaxPlotFreq);
        double t = Math.Clamp((x - left) / width, 0, 1);
        return Math.Pow(10, logMin + t * (logMax - logMin));
    }

    private static double GainToY(double gainDb, double top, double height)
    {
        return top + (PlotDbRange - gainDb) / (PlotDbRange * 2) * height;
    }

    private static double YToGain(double y, double top, double height)
    {
        double t = Math.Clamp((y - top) / height, 0, 1);
        return PlotDbRange - t * PlotDbRange * 2;
    }

    /// <summary>
    /// 该频点的总响应（10 段 + 两个搁架的叠加），控制点画在这里才能落在曲线上。
    /// </summary>
    private double TotalResponseAt(double frequency)
    {
        var response = _curveEngine.GetMagnitudeResponseDb(new[] { (float)Math.Clamp(frequency, 1, 20000) });
        return response[0];
    }

    private void DrawEQCurve()
    {
        if (EQCurveCanvas == null) return;

        var canvas = EQCurveCanvas;
        canvas.Children.Clear();

        if (canvas.ActualWidth < 80 || canvas.ActualHeight < 60) return;

        var (left, top, plotWidth, plotHeight) = GetPlotRegion();

        var border = (SolidColorBrush)FindResource("BrBorder");
        var textMuted = (SolidColorBrush)FindResource("BrText3");
        var accent = (SolidColorBrush)FindResource("BrAccent");
        var surface = (SolidColorBrush)FindResource("BrSurface");

        // dB 参考线
        foreach (var db in new[] { -12.0, -6.0, 0.0, 6.0, 12.0 })
        {
            double y = GainToY(db, top, plotHeight);

            canvas.Children.Add(new Line
            {
                X1 = left,
                X2 = left + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = border,
                StrokeThickness = db == 0 ? 1 : 0.75,
                Opacity = db == 0 ? 0.9 : 0.55
            });

            var label = new TextBlock
            {
                Text = db.ToString("+0;-0;0"),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 10.5,
                Foreground = textMuted
            };

            Canvas.SetLeft(label, 8);
            Canvas.SetTop(label, y - 7);
            canvas.Children.Add(label);
        }

        // 频率参考线
        var ticks = new (double Freq, string Label)[]
        {
            (31.5, "31"), (63, "63"), (125, "125"), (250, "250"), (500, "500"),
            (1000, "1k"), (2000, "2k"), (4000, "4k"), (8000, "8k"), (16000, "16k")
        };

        foreach (var (freq, label) in ticks)
        {
            double x = FrequencyToX(freq, left, plotWidth);

            canvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = top,
                Y2 = top + plotHeight,
                Stroke = border,
                StrokeThickness = 0.75,
                Opacity = 0.45
            });

            var text = new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 10,
                Foreground = textMuted
            };

            Canvas.SetLeft(text, x - 10);
            Canvas.SetTop(text, top + plotHeight + 7);
            canvas.Children.Add(text);
        }

        // 响应曲线：与音频处理使用同一套 biquad 系数
        int sampleCount = Math.Max(180, (int)plotWidth);
        var frequencies = new float[sampleCount];

        double logMinFreq = Math.Log10(MinPlotFreq);
        double logMaxFreq = Math.Log10(MaxPlotFreq);

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / (sampleCount - 1);
            frequencies[i] = (float)Math.Pow(10, logMinFreq + t * (logMaxFreq - logMinFreq));
        }

        var response = _curveEngine.GetMagnitudeResponseDb(frequencies);

        var curvePoints = new PointCollection(sampleCount);

        for (int i = 0; i < sampleCount; i++)
        {
            double gain = Math.Clamp(response[i], -PlotDbRange, PlotDbRange);
            curvePoints.Add(new Point(
                left + (double)i / (sampleCount - 1) * plotWidth,
                GainToY(gain, top, plotHeight)));
        }

        // 曲线下方填充
        var fillFigure = new PathFigure
        {
            StartPoint = new Point(left, GainToY(0, top, plotHeight)),
            IsClosed = true,
            IsFilled = true
        };

        fillFigure.Segments.Add(new PolyLineSegment(curvePoints, true));
        fillFigure.Segments.Add(new LineSegment(
            new Point(left + plotWidth, GainToY(0, top, plotHeight)), false));

        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = new PathGeometry { Figures = { fillFigure } },
            Fill = new SolidColorBrush(Color.FromArgb(30, accent.Color.R, accent.Color.G, accent.Color.B)),
            IsHitTestVisible = false
        });

        canvas.Children.Add(new Polyline
        {
            Points = curvePoints,
            Stroke = accent,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        });

        // 控制点
        for (int i = 0; i < 10; i++)
        {
            double x = FrequencyToX(_eqFrequencies[i], left, plotWidth);
            double y = GainToY(
                Math.Clamp(TotalResponseAt(_eqFrequencies[i]), -PlotDbRange, PlotDbRange),
                top,
                plotHeight);

            bool active = i == _selectedBand;
            bool hover = i == _hoverBand;
            double size = active ? 13 : 10;

            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = active || hover ? accent : surface,
                Stroke = accent,
                StrokeThickness = active ? 2.5 : 1.6,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(dot, x - size / 2);
            Canvas.SetTop(dot, y - size / 2);
            canvas.Children.Add(dot);
        }

        // 选中频段读数
        if (_selectedBand >= 0 && _selectedBand < 10)
        {
            var chip = new Border
            {
                Background = (SolidColorBrush)FindResource("BrElevated"),
                BorderBrush = (SolidColorBrush)FindResource("BrBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 3, 8, 3),
                IsHitTestVisible = false
            };

            chip.Child = new TextBlock
            {
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 11.5,
                Foreground = (SolidColorBrush)FindResource("BrText2"),
                Text = $"{FormatFrequency(_eqFrequencies[_selectedBand])}  " +
                       $"{_eqBands[_selectedBand]:+0.0;-0.0;0.0} dB  Q {_eqQValues[_selectedBand]:0.0}"
            };

            Canvas.SetRight(chip, 4);
            Canvas.SetTop(chip, 2);
            canvas.Children.Add(chip);
        }
    }

    private static string FormatFrequency(double frequency)
    {
        return frequency >= 1000
            ? $"{frequency / 1000:0.##} kHz"
            : $"{frequency:0} Hz";
    }

    private int FindBandAt(Point position)
    {
        if (EQCurveCanvas == null)
            return -1;

        var (left, top, plotWidth, plotHeight) = GetPlotRegion();

        for (int i = 0; i < 10; i++)
        {
            double x = FrequencyToX(_eqFrequencies[i], left, plotWidth);
            double y = GainToY(
                Math.Clamp(TotalResponseAt(_eqFrequencies[i]), -PlotDbRange, PlotDbRange),
                top,
                plotHeight);

            double dx = position.X - x;
            double dy = position.Y - y;

            if (dx * dx + dy * dy <= 18 * 18)
                return i;
        }

        return -1;
    }

    private void EQCurve_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(EQCurveCanvas);
        int band = FindBandAt(position);

        if (band < 0)
            return;

        _selectedBand = band;

        if (e.ClickCount == 2)
        {
            ApplyGain(band, 0);
            return;
        }

        _dragBand = band;
        EQCurveCanvas.CaptureMouse();
        DrawEQCurve();
        e.Handled = true;
    }

    private void EQCurve_MouseMove(object sender, MouseEventArgs e)
    {
        if (EQCurveCanvas == null)
            return;

        var position = e.GetPosition(EQCurveCanvas);

        if (_dragBand >= 0)
        {
            UpdateBandFromDrag(_dragBand, position);
            e.Handled = true;
            return;
        }

        int hover = FindBandAt(position);

        if (hover == _hoverBand)
            return;

        _hoverBand = hover;
        EQCurveCanvas.Cursor = hover >= 0 ? Cursors.SizeAll : Cursors.Cross;
        DrawEQCurve();
    }

    /// <summary>
    /// 按指针位置更新指定频段的频率与增益（拖动曲线时调用）
    /// </summary>
    private void UpdateBandFromDrag(int band, Point position)
    {
        if (!IsCurrentPresetEditable())
            return;

        var (left, top, plotWidth, plotHeight) = GetPlotRegion();

        double desiredTotal = Math.Clamp(YToGain(position.Y, top, plotHeight), -12, 12);
        double currentTotal = TotalResponseAt(_eqFrequencies[band]);
        double gain = Math.Clamp(_eqBands[band] + (desiredTotal - currentTotal), -12, 12);
        double frequency = XToFrequency(position.X, left, plotWidth);

        double lower = band == 0 ? 20 : _eqFrequencies[band - 1] * 1.03;
        double upper = band == 9 ? 20000 : _eqFrequencies[band + 1] / 1.03;

        _selectedBand = band;
        _eqBands[band] = gain;
        _eqFrequencies[band] = Math.Clamp(frequency, Math.Max(20, lower), Math.Min(20000, upper));

        SyncBandFromDrag(band);
        UpdateCurveEngine();
        DrawEQCurve();
    }

    private void EQCurve_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragBand < 0)
            return;

        _dragBand = -1;

        if (EQCurveCanvas.IsMouseCaptured)
            EQCurveCanvas.ReleaseMouseCapture();

        e.Handled = true;
    }

    private void EQCurve_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_hoverBand == -1)
            return;

        _hoverBand = -1;
        DrawEQCurve();
    }

    #endregion


    #region 扬声器示意图

    private void SpeakerCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawSpeakerDiagram();
    }

    private void SpeakerCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SpeakerDiagramCanvas == null) return;

        var position = e.GetPosition(SpeakerDiagramCanvas);
        double width = SpeakerDiagramCanvas.ActualWidth;
        double height = SpeakerDiagramCanvas.ActualHeight;

        double cx = width / 2;
        double cy = height / 2;

        // 计算点击位置相对听者的方位：0 = 正前，正值向右，±180 = 正后
        double dx = position.X - cx;
        double dy = position.Y - cy;
        double azimuth = Math.Atan2(dx, -dy) * 180 / Math.PI;

        // 播放对应方向的 HRTF 测试音
        PlayDirectionalTestSound(azimuth);

        // 视觉反馈：在点击位置显示波纹效果
        var ripple = new Ellipse
        {
            Width = 20,
            Height = 20,
            Fill = Brushes.Transparent,
            Stroke = (SolidColorBrush)FindResource("BrAccent"),
            StrokeThickness = 2,
            Opacity = 1.0
        };
        Canvas.SetLeft(ripple, position.X - 10);
        Canvas.SetTop(ripple, position.Y - 10);
        SpeakerDiagramCanvas.Children.Add(ripple);

        // 动画：波纹扩散并淡出
        var storyboard = new System.Windows.Media.Animation.Storyboard();

        var scaleXAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 3.0,
            Duration = TimeSpan.FromMilliseconds(600)
        };
        var scaleYAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 3.0,
            Duration = TimeSpan.FromMilliseconds(600)
        };
        var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(600)
        };

        ripple.RenderTransform = new ScaleTransform(1, 1);
        ripple.RenderTransformOrigin = new Point(0.5, 0.5);

        System.Windows.Media.Animation.Storyboard.SetTarget(scaleXAnim, ripple);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleXAnim,
            new PropertyPath("RenderTransform.ScaleX"));
        System.Windows.Media.Animation.Storyboard.SetTarget(scaleYAnim, ripple);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleYAnim,
            new PropertyPath("RenderTransform.ScaleY"));
        System.Windows.Media.Animation.Storyboard.SetTarget(fadeAnim, ripple);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeAnim,
            new PropertyPath("Opacity"));

        storyboard.Children.Add(scaleXAnim);
        storyboard.Children.Add(scaleYAnim);
        storyboard.Children.Add(fadeAnim);

        storyboard.Completed += (s, args) => SpeakerDiagramCanvas.Children.Remove(ripple);
        storyboard.Begin();
    }

    private void PlayDirectionalTestSound(double azimuth)
    {
        try
        {
            if (_audioProcessor != null)
            {
                // 主链路在跑：把测试音混进同一条输出，避免抢设备
                _audioProcessor.PlayTestTone((float)azimuth);
            }
            else
            {
                // 主链路没启动：单独开一个轻量输出
                _testTonePlayer ??= new DirectionalTestPlayer();

                var burst = DirectionalTestTone.Generate(48000);
                var stereo = DirectionalTestTone.RenderBinaural(burst, (float)azimuth, 48000);
                _testTonePlayer.Play(stereo, 48000);
            }

            string channel = GetChannelName(azimuth);
            Title = $"Step 音频驱动 · {channel} {azimuth:F0}°";

            var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
            resetTimer.Tick += (_, _) =>
            {
                resetTimer.Stop();
                Title = "Step 音频驱动";
            };
            resetTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"试听失败：{ex.Message}\n\n" +
                "如果输出设备被其它程序独占，请先点右上角“启动音频”让本程序接管输出，再点击试听。",
                "方向试听", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string GetChannelName(double azimuth)
    {
        double angle = ((azimuth % 360) + 360) % 360; // 0 = 正前，90 = 右，180 = 后，270 = 左

        return angle switch
        {
            < 22.5 => "正前方",
            < 67.5 => "右前方",
            < 112.5 => "右侧",
            < 157.5 => "右后方",
            < 202.5 => "正后方",
            < 247.5 => "左后方",
            < 292.5 => "左侧",
            < 337.5 => "左前方",
            _ => "正前方"
        };
    }

    private void DrawSpeakerDiagram()
    {
        if (SpeakerDiagramCanvas == null) return;

        SpeakerDiagramCanvas.Children.Clear();

        double width = SpeakerDiagramCanvas.ActualWidth;
        double height = SpeakerDiagramCanvas.ActualHeight;

        if (width < 50 || height < 50) return;

        double cx = width / 2;
        double cy = height / 2;
        double radius = Math.Min(width, height) * 0.35;

        var borderBrush = (SolidColorBrush)FindResource("BrBorder");
        var borderStrong = (SolidColorBrush)FindResource("BrBorderStrong");
        var accentBrush = (SolidColorBrush)FindResource("BrAccent");
        var textMuted = (SolidColorBrush)FindResource("BrText3");
        var textSecondary = (SolidColorBrush)FindResource("BrText2");

        // 背景圆环与十字轴
        foreach (var factor in new[] { 1.0, 0.66, 0.33 })
        {
            double r = radius * factor;
            var ring = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Stroke = borderBrush,
                StrokeThickness = 1,
                Opacity = factor == 1.0 ? 0.7 : 0.35
            };
            Canvas.SetLeft(ring, cx - r);
            Canvas.SetTop(ring, cy - r);
            SpeakerDiagramCanvas.Children.Add(ring);
        }

        SpeakerDiagramCanvas.Children.Add(new Line
        {
            X1 = cx - radius - 16, X2 = cx + radius + 16, Y1 = cy, Y2 = cy,
            Stroke = borderBrush, StrokeThickness = 1, Opacity = 0.35
        });

        SpeakerDiagramCanvas.Children.Add(new Line
        {
            X1 = cx, X2 = cx, Y1 = cy - radius - 16, Y2 = cy + radius + 16,
            Stroke = borderBrush, StrokeThickness = 1, Opacity = 0.2
        });

        // 绘制听者位置（中心）
        var listener = new Ellipse
        {
            Width = 26, Height = 26,
            Fill = accentBrush,
            Stroke = (SolidColorBrush)FindResource("BrBase"),
            StrokeThickness = 2
        };
        Canvas.SetLeft(listener, cx - 13);
        Canvas.SetTop(listener, cy - 13);
        SpeakerDiagramCanvas.Children.Add(listener);

        var listenerLabel = new TextBlock
        {
            Text = "听者",
            FontSize = 11,
            Foreground = textSecondary
        };
        Canvas.SetLeft(listenerLabel, cx - 15);
        Canvas.SetTop(listenerLabel, cy + 20);
        SpeakerDiagramCanvas.Children.Add(listenerLabel);

        // 7.1 扬声器位置 (角度)
        var speakers = new[]
        {
            ("FC", 0),     // 前中
            ("FL", -30),   // 前左
            ("FR", 30),    // 前右
            ("SL", -90),   // 侧左
            ("SR", 90),    // 侧右
            ("BL", -135),  // 后左
            ("BR", 135),   // 后右
            ("LFE", 180)   // 低频 (后)
        };

        var speakerBrush = (SolidColorBrush)FindResource("BrElevated");

        foreach (var (label, angle) in speakers)
        {
            double rad = angle * Math.PI / 180.0;
            double x = cx + radius * Math.Sin(rad);
            double y = cy - radius * Math.Cos(rad);

            // 连接线
            var line = new Line
            {
                X1 = cx, Y1 = cy,
                X2 = x, Y2 = y,
                Stroke = borderBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Opacity = 0.45
            };
            SpeakerDiagramCanvas.Children.Add(line);

            // 扬声器图标
            bool isSide = label.StartsWith("S");
            bool isLfe = label == "LFE";
            double size = isLfe ? 14 : 20;

            var speaker = new Border
            {
                Width = size,
                Height = size,
                Background = isSide ? accentBrush : speakerBrush,
                BorderBrush = isSide ? accentBrush : borderStrong,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            };
            Canvas.SetLeft(speaker, x - size / 2);
            Canvas.SetTop(speaker, y - size / 2);
            SpeakerDiagramCanvas.Children.Add(speaker);

            // 标签
            var text = new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = isSide ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isSide ? accentBrush : textSecondary
            };
            double offsetX = angle == 0 ? -7 : (angle > 0 ? 14 : -22);
            double offsetY = Math.Abs(angle) == 180 ? 12 : (Math.Abs(angle) < 90 ? -22 : 14);
            Canvas.SetLeft(text, x + offsetX);
            Canvas.SetTop(text, y + offsetY);
            SpeakerDiagramCanvas.Children.Add(text);
        }
    }

    #endregion

    #region HRTF / 环绕声控制

    private void SliderHRTF_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LabelHRTF != null)
            LabelHRTF.Text = $"{e.NewValue:F1}x";

        _audioProcessor?.SetHRTFIntensity((float)e.NewValue);
    }

    private void ChkHrtf_Changed(object sender, RoutedEventArgs e)
    {
        _audioProcessor?.SetHrtfEnabled(ChkHrtf.IsChecked == true);
    }

    private void SliderSurround_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LabelSurround != null)
            LabelSurround.Text = $"{e.NewValue:F0}°";

        _audioProcessor?.SetSurroundWidth((float)e.NewValue);
    }

    #endregion

    #region 设备管理

    /// <summary>
    /// 捕获设备下拉：只列 VB-Cable 的渲染端点（游戏可以输出到这些端点）
    /// </summary>
    private sealed record RenderDevice(string Id, string Name);

    private static List<RenderDevice> EnumerateRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();

        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new RenderDevice(d.ID, d.FriendlyName))
            .ToList();
    }

    private void PopulateCaptureDeviceList(IReadOnlyList<RenderDevice> devices)
    {
        try
        {
            CaptureDeviceComboBox.Items.Clear();
            CaptureDeviceComboBox.Items.Add(new ComboBoxItem
            {
                Content = "自动（CABLE Input）",
                Tag = string.Empty
            });

            int preferredIndex = 0;
            int itemIndex = 1;

            foreach (var device in devices)
            {
                if (device.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                    device.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
                {
                    CaptureDeviceComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = device.Name,
                        Tag = device.Id
                    });

                    // 优先选 16ch 虚拟线；具体是否 8 声道由端点配置决定
                    if (device.Name.Contains("16ch", StringComparison.OrdinalIgnoreCase))
                        preferredIndex = itemIndex;

                    itemIndex++;
                }
            }

            CaptureDeviceComboBox.SelectedIndex = preferredIndex;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"枚举捕获设备失败: {ex.Message}");
        }
    }

    private void CaptureDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CaptureDeviceComboBox.SelectedItem is ComboBoxItem item)
        {
            _selectedCaptureDeviceId = item.Tag?.ToString() ?? string.Empty;
        }

        if (_audioProcessor != null)
        {
            StatusText.ToolTip = "捕获设备已改变，停止后重新启动音频即可生效";
        }
    }

    private string? ResolveCaptureDeviceId()
    {
        return string.IsNullOrWhiteSpace(_selectedCaptureDeviceId) ? null : _selectedCaptureDeviceId;
    }

    private async void BtnGameMode_Click(object sender, RoutedEventArgs e)
    {
        string? deviceId = WindowsAudioEndpoint.FindRenderDeviceId("CABLE In 16ch");

        if (deviceId == null)
        {
            AutoConfigStatusText.Text = "未找到 CABLE In 16ch，请确认 VB-CABLE 已安装。";
            return;
        }

        if (_audioProcessor != null)
            StopAudio();

        if (!WindowsAudioEndpoint.TrySetChannelFormat(deviceId, 8, 48000, out string error))
        {
            AutoConfigStatusText.Text = $"游戏模式配置失败：{error}";
            return;
        }

        _previousDefaultDeviceId ??= WindowsAudioEndpoint.GetDefaultRenderDeviceId();
        _defaultDeviceManagedByApp = WindowsAudioEndpoint.TrySetDefaultDevice(deviceId, out _);

        SelectCaptureDevice(deviceId);

        var gaming = _presetManager?.GetPreset("gaming");
        if (gaming != null)
            ApplyPreset(gaming);

        ChkHrtf.IsChecked = true;

        AutoConfigStatusText.Text = "✓ 游戏模式：8 声道 / 游戏预设 / 虚拟环绕已开";

        await StartAudioAsync();
    }

    /// <summary>
    /// 一键把 CABLE In 16ch 配置成 7.1，并把默认播放切过去
    /// </summary>
    private async void BtnAutoConfig_Click(object sender, RoutedEventArgs e)
    {
        string? deviceId = WindowsAudioEndpoint.FindRenderDeviceId("CABLE In 16ch");

        if (deviceId == null)
        {
            AutoConfigStatusText.Text = "未找到 CABLE In 16ch，请确认 VB-CABLE 已安装。";
            return;
        }

        bool wasRunning = _audioProcessor != null;

        if (_audioProcessor != null)
            StopAudio();

        if (!WindowsAudioEndpoint.TrySetChannelFormat(deviceId, 8, 48000, out string error))
        {
            AutoConfigStatusText.Text =
                $"配置失败：{error}\n" +
                "如果游戏正占用该设备，请先关闭游戏或把游戏输出切到别的设备再试。";
            return;
        }

        string extra = string.Empty;

        if (ChkSetDefaultDevice.IsChecked == true)
        {
            // 记住原来的默认设备，方便停止/恢复时还原
            _previousDefaultDeviceId ??= WindowsAudioEndpoint.GetDefaultRenderDeviceId();

            bool switched = WindowsAudioEndpoint.TrySetDefaultDevice(deviceId, out string defaultError);
            _defaultDeviceManagedByApp = switched;

            extra = switched
                ? "，已设为默认播放设备"
                : $"，但设为默认失败：{defaultError}";
        }
        else
        {
            _defaultDeviceManagedByApp = false;
        }

        SelectCaptureDevice(deviceId);

        int? channels = WindowsAudioEndpoint.GetMixFormatChannels(deviceId);

        AutoConfigStatusText.Text =
            $"✓ 已配置为 {channels?.ToString() ?? "?"} 声道{extra}。";

        // 原来在放就自动重启，不用再手动点一次启动
        if (wasRunning)
            await StartAudioAsync();
    }

    private async void BtnRestoreStereo_Click(object sender, RoutedEventArgs e)
    {
        string? deviceId = WindowsAudioEndpoint.FindRenderDeviceId("CABLE In 16ch");

        if (deviceId == null)
        {
            AutoConfigStatusText.Text = "未找到 CABLE In 16ch。";
            return;
        }

        bool wasRunning = _audioProcessor != null;

        if (_audioProcessor != null)
            StopAudio();

        if (WindowsAudioEndpoint.TrySetChannelFormat(deviceId, 2, 48000, out string error))
        {
            string restoreNote = string.Empty;

            if (_previousDefaultDeviceId != null)
            {
                restoreNote = WindowsAudioEndpoint.TrySetDefaultDevice(_previousDefaultDeviceId, out _)
                    ? "，并已还原默认播放设备"
                    : "，但默认播放设备还原失败";

                _previousDefaultDeviceId = null;
            }

            AutoConfigStatusText.Text = $"已恢复为 2 声道{restoreNote}。";

            if (wasRunning)
                await StartAudioAsync();
        }
        else
        {
            AutoConfigStatusText.Text = $"恢复失败：{error}";
        }
    }

    /// <summary>
    /// 在捕获设备下拉里选中指定 ID
    /// </summary>
    private void SelectCaptureDevice(string deviceId)
    {
        foreach (var item in CaptureDeviceComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), deviceId, StringComparison.OrdinalIgnoreCase))
            {
                CaptureDeviceComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void PopulateOutputFormats()
    {
        OutputFormatComboBox.Items.Clear();
        OutputFormatComboBox.Items.Add(new ComboBoxItem { Content = "自动（跟随捕获 48 kHz）", Tag = string.Empty });
        OutputFormatComboBox.Items.Add(new ComboBoxItem { Content = "48 kHz · 32-bit float", Tag = "48000_32f" });
        OutputFormatComboBox.Items.Add(new ComboBoxItem { Content = "96 kHz · 24-bit", Tag = "96000_24" });
        OutputFormatComboBox.Items.Add(new ComboBoxItem { Content = "192 kHz · 24-bit", Tag = "192000_24" });
        OutputFormatComboBox.Items.Add(new ComboBoxItem { Content = "384 kHz · 24-bit", Tag = "384000_24" });
        OutputFormatComboBox.SelectedIndex = 0;
    }

    private void PopulateBufferModes()
    {
        BufferModeComboBox.Items.Clear();
        BufferModeComboBox.Items.Add(new ComboBoxItem { Content = "稳定（100 ms，推荐）", Tag = 100 });
        BufferModeComboBox.Items.Add(new ComboBoxItem { Content = "平衡（60 ms）", Tag = 60 });
        BufferModeComboBox.Items.Add(new ComboBoxItem { Content = "低延迟（40 ms）", Tag = 40 });
        BufferModeComboBox.SelectedIndex = 0;
    }

    private void BufferModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BufferModeComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int milliseconds))
        {
            _bufferMilliseconds = milliseconds;
        }

        if (_audioProcessor != null)
            StatusText.ToolTip = "缓冲档位已改变，停止后重新启动音频即可生效";
    }

    private void OutputFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)    {
        string? tag = (OutputFormatComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

        _requestedOutputFormat = tag switch
        {
            "48000_32f" => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2),
            "96000_24" => new WaveFormatExtensible(96000, 24, 2),
            "192000_24" => new WaveFormatExtensible(192000, 24, 2),
            "384000_24" => new WaveFormatExtensible(384000, 24, 2),
            _ => null
        };

        if (_audioProcessor != null)
            StatusText.ToolTip = "输出格式已改变，停止后重新启动音频即可生效";
    }

    private void ApplySavedUiSettings()
    {
        var saved = UiSettingsStore.Load();
        if (saved == null)
            return;

        Preset? restoredPreset = null;

        if (!string.IsNullOrEmpty(saved.PresetId) &&
            _presetManager?.GetPreset(saved.PresetId) is { } preset)
        {
            restoredPreset = preset;
            ApplyPreset(preset);
        }

        // 内置预设的 EQ 以新出厂值为准；自定义预设才恢复上次未保存的编辑
        bool restoreEq = restoredPreset == null || !restoredPreset.IsBuiltIn;

        if (restoreEq)
        {
            _syncingBandUi = true;

            for (int i = 0; i < 10; i++)
            {
                if (saved.EqBands is { Length: 10 })
                    _eqBands[i] = Math.Clamp(saved.EqBands[i], -12, 12);

                if (saved.EqFrequencies is { Length: 10 })
                    _eqFrequencies[i] = saved.EqFrequencies[i];

                if (saved.EqQValues is { Length: 10 })
                    _eqQValues[i] = Math.Clamp(saved.EqQValues[i], 0.5, 10);

                if (_eqSliders[i] != null)
                    _eqSliders[i].Value = _eqBands[i];

                if (_eqDbBoxes[i] != null)
                    _eqDbBoxes[i].Text = _eqBands[i].ToString("0.0");

                if (_eqFreqBoxes[i] != null)
                    _eqFreqBoxes[i].Text = _eqFrequencies[i].ToString("0");

                if (_eqQBoxes[i] != null)
                    _eqQBoxes[i].Text = _eqQValues[i].ToString("0.0");
            }

            if (saved.LowShelfFrequency.HasValue) _lowShelfFrequency = Math.Clamp(saved.LowShelfFrequency.Value, 20, 20000);
            if (saved.LowShelfGain.HasValue) _lowShelfGain = Math.Clamp(saved.LowShelfGain.Value, -12, 12);
            if (saved.LowShelfQ.HasValue) _lowShelfQ = Math.Clamp(saved.LowShelfQ.Value, 0.4, 4);
            if (saved.HighShelfFrequency.HasValue) _highShelfFrequency = Math.Clamp(saved.HighShelfFrequency.Value, 20, 20000);
            if (saved.HighShelfGain.HasValue) _highShelfGain = Math.Clamp(saved.HighShelfGain.Value, -12, 12);
            if (saved.HighShelfQ.HasValue) _highShelfQ = Math.Clamp(saved.HighShelfQ.Value, 0.4, 4);

            if (_lowShelfFreqBox != null) _lowShelfFreqBox.Text = _lowShelfFrequency.ToString("0");
            if (_lowShelfGainBox != null) _lowShelfGainBox.Text = _lowShelfGain.ToString("0.0");
            if (_lowShelfQBox != null) _lowShelfQBox.Text = _lowShelfQ.ToString("0.0");
            if (_highShelfFreqBox != null) _highShelfFreqBox.Text = _highShelfFrequency.ToString("0");
            if (_highShelfGainBox != null) _highShelfGainBox.Text = _highShelfGain.ToString("0.0");
            if (_highShelfQBox != null) _highShelfQBox.Text = _highShelfQ.ToString("0.0");

            _syncingBandUi = false;
            UpdateCurveEngine();
            DrawEQCurve();
        }

        if (saved.HrtfEnabled.HasValue)
            ChkHrtf.IsChecked = saved.HrtfEnabled.Value;

        if (restoreEq)
        {
            if (saved.HrtfIntensity.HasValue)
                SliderHRTF.Value = Math.Clamp(saved.HrtfIntensity.Value, 0.5, 2.0);

            if (saved.SurroundWidth.HasValue)
                SliderSurround.Value = Math.Clamp(saved.SurroundWidth.Value, 60, 180);
        }

        if (saved.DspBypass.HasValue)
            ChkDspBypass.IsChecked = saved.DspBypass.Value;

        if (saved.OverlayVisible.HasValue)
            ChkDirectionOverlay.IsChecked = saved.OverlayVisible.Value;

        if (saved.CloseActionRemembered == true &&
            (saved.CloseAction == "minimize" || saved.CloseAction == "exit"))
        {
            _rememberCloseChoice = true;
            _closeAction = saved.CloseAction;
        }

        if (saved.SetDefaultDevice.HasValue)
            ChkSetDefaultDevice.IsChecked = saved.SetDefaultDevice.Value;

        SelectComboItemByTag(CaptureDeviceComboBox, saved.CaptureDeviceId);
        SelectComboItemByTag(DeviceComboBox, saved.OutputDeviceId);
        SelectComboItemByTag(OutputFormatComboBox, saved.OutputFormatTag);
        SelectComboItemByTag(BufferModeComboBox, saved.BufferMilliseconds?.ToString());

        UpdatePresetEditability();
    }

    private static bool SelectComboItemByTag(ComboBox combo, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return true;
            }
        }

        return false;
    }

    private UiSettings CaptureUiSettings()
    {
        return new UiSettings
        {
            PresetId = _currentPresetId,
            EqBands = _eqBands.ToArray(),
            EqFrequencies = _eqFrequencies.ToArray(),
            EqQValues = _eqQValues.ToArray(),
            HrtfEnabled = ChkHrtf.IsChecked == true,
            HrtfIntensity = SliderHRTF.Value,
            SurroundWidth = SliderSurround.Value,
            DspBypass = ChkDspBypass.IsChecked == true,
            SetDefaultDevice = ChkSetDefaultDevice.IsChecked == true,
            LowShelfFrequency = _lowShelfFrequency,
            LowShelfGain = _lowShelfGain,
            LowShelfQ = _lowShelfQ,
            HighShelfFrequency = _highShelfFrequency,
            HighShelfGain = _highShelfGain,
            HighShelfQ = _highShelfQ,
            OverlayVisible = ChkDirectionOverlay.IsChecked == true,
            OverlayLeft = _directionOverlay?.Left,
            OverlayTop = _directionOverlay?.Top,
            CloseAction = _closeAction,
            CloseActionRemembered = _rememberCloseChoice,
            CaptureDeviceId = (CaptureDeviceComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            OutputDeviceId = (DeviceComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            OutputFormatTag = (OutputFormatComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            BufferMilliseconds = _bufferMilliseconds
        };
    }

    private async Task UpdateOutputCapabilityAsync(string deviceId)
    {
        if (OutputCapabilityText == null || string.IsNullOrEmpty(deviceId))
        {
            if (OutputCapabilityText != null)
                OutputCapabilityText.Text = string.Empty;
            return;
        }

        string text = await Task.Run(() => DescribeOutputCapability(deviceId));

        // 选择可能在探测期间又变了
        if (string.Equals(
                (DeviceComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                deviceId,
                StringComparison.OrdinalIgnoreCase))
        {
            OutputCapabilityText.Text = text;
        }
    }

    private static string DescribeOutputCapability(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);
            var mix = device.AudioClient.MixFormat;
            string name = device.FriendlyName;

            // 不再对设备调用 IsFormatSupported(Exclusive)：
            // N9V2 的 FlowAudioDevice.sys 在独占查询/初始化时可能触发内核 bugcheck。
            if (name.Contains("N9V2", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("N9 Ultra", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ATK N9", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ATS3031", StringComparison.OrdinalIgnoreCase))
            {
                return $"仅共享 · 2ch / 48 kHz（端点 {mix.Channels}ch）";
            }

            if (name.Contains("SK02", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Fosi", StringComparison.OrdinalIgnoreCase))
            {
                return $"独占可用 · 48 kHz（端点 {mix.Channels}ch）";
            }

            return $"端点 {mix.Channels}ch / {mix.SampleRate / 1000} kHz";
        }
        catch (Exception ex)
        {
            return $"无法读取：{ex.Message}";
        }
    }

    private void PopulateDeviceList(IReadOnlyList<RenderDevice> devices)
    {
        try
        {
            DeviceComboBox.Items.Clear();

            foreach (var device in devices)
            {
                DeviceComboBox.Items.Add(new ComboBoxItem
                {
                    Content = device.Name,
                    Tag = device.Id
                });
            }

            if (DeviceComboBox.Items.Count > 0)
            {
                // 默认选中 SK02，其次是第一个设备
                int preferredIndex = 0;

                for (int i = 0; i < DeviceComboBox.Items.Count; i++)
                {
                    if (DeviceComboBox.Items[i] is ComboBoxItem item &&
                        item.Content?.ToString() is string name &&
                        (name.Contains("SK02", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Fosi", StringComparison.OrdinalIgnoreCase)))
                    {
                        preferredIndex = i;
                        break;
                    }
                }

                DeviceComboBox.SelectedIndex = preferredIndex;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"枚举音频设备失败: {ex.Message}", "警告",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)    {
        if (DeviceComboBox.SelectedItem is ComboBoxItem item)
        {
            _selectedDeviceName = item.Content?.ToString() ?? string.Empty;
            _selectedDeviceId = item.Tag?.ToString() ?? string.Empty;
            _ = UpdateOutputCapabilityAsync(_selectedDeviceId);
        }

        // 运行中切换设备需要重新打开 WASAPI 端点，下次启动生效
        if (_audioProcessor != null)
        {
            StatusText.ToolTip = "输出设备已改变，停止后重新启动音频即可生效";
        }
    }

    private void CheckVBCableStatus(IReadOnlyList<RenderDevice> devices)
    {
        try
        {
            bool hasCable = devices.Any(d =>
                d.Name.Contains("VB-CABLE", StringComparison.OrdinalIgnoreCase) ||
                d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));

            var sk02 = devices.FirstOrDefault(d =>
                d.Name.Contains("SK02", StringComparison.OrdinalIgnoreCase) ||
                d.Name.Contains("Fosi", StringComparison.OrdinalIgnoreCase));

            if (VBCableStatusText != null)
            {
                VBCableStatusText.Text = hasCable
                    ? "✓ 已检测到 VB-CABLE"
                    : "未检测到 VB-CABLE";

                VBCableStatusText.Foreground = hasCable
                    ? (SolidColorBrush)FindResource("BrGood")
                    : (SolidColorBrush)FindResource("BrText3");
            }

            if (DeviceStatusText != null)
            {
                DeviceStatusText.Text = sk02 != null
                    ? $"✓ {sk02.Name}"
                    : "未检测到 SK02";

                DeviceStatusText.Foreground = sk02 != null
                    ? (SolidColorBrush)FindResource("BrGood")
                    : (SolidColorBrush)FindResource("BrText3");
            }
        }
        catch (Exception ex)
        {
            if (VBCableStatusText != null)
            {
                VBCableStatusText.Text = $"音频设备检测失败: {ex.Message}";
                VBCableStatusText.Foreground = (SolidColorBrush)FindResource("BrText3");
            }
        }
    }

    #endregion

    #region HID 设备

    private void TryInitializeHID()
    {
        try
        {
            _hidManager = new HIDManager();
            _hidManager.Initialize();
            _hidManager.RotaryChanged += OnRotaryChanged;
            _hidManager.StartListening();
        }
        catch (Exception ex)
        {
            // HID 初始化失败非致命错误
            System.Diagnostics.Debug.WriteLine($"HID 初始化失败: {ex.Message}");
        }
    }

    private void OnRotaryChanged(object? sender, RotaryEncoderEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _volumeLevel = Math.Clamp(_volumeLevel + e.Delta * 0.02f, 0f, 1f);
            _audioProcessor?.SetVolume(_volumeLevel);
            PerfText.Text = $"音量 {_volumeLevel * 100:F0}%";
            System.Diagnostics.Debug.WriteLine($"旋钮变化: Delta={e.Delta} / 音量={_volumeLevel:F2}");
        });
    }

    #endregion

    #region EQ 按钮

    private void BtnResetEQ_Click(object sender, RoutedEventArgs e)
    {
        if (!IsCurrentPresetEditable())
            return;

        _syncingBandUi = true;

        for (int i = 0; i < 10; i++)
        {
            _eqBands[i] = 0;

            if (_eqSliders[i] != null)
                _eqSliders[i].Value = 0;

            if (_eqDbBoxes[i] != null)
                _eqDbBoxes[i].Text = "0.0";

            _audioProcessor?.SetEQBandGain(i, 0f);
        }

        _lowShelfGain = 0;
        _highShelfGain = 0;

        if (_lowShelfGainBox != null) _lowShelfGainBox.Text = "0.0";
        if (_highShelfGainBox != null) _highShelfGainBox.Text = "0.0";

        _audioProcessor?.SetLowShelf((float)_lowShelfFrequency, 0f, (float)_lowShelfQ);
        _audioProcessor?.SetHighShelf((float)_highShelfFrequency, 0f, (float)_highShelfQ);

        _syncingBandUi = false;

        UpdateCurveEngine();
        DrawEQCurve();
    }

    private async void BtnAddPreset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new NewPresetDialog { Owner = this };

            if (dialog.ShowDialog() != true)
                return;

            var newPreset = new Preset
            {
                Id = Guid.NewGuid().ToString(),
                Name = dialog.PresetName,
                Description = dialog.PresetDescription,
                EQBands = _eqBands.Select(v => (float)v).ToArray(),
                EQFrequencies = _eqFrequencies.Select(v => (float)v).ToArray(),
                EQQValues = _eqQValues.Select(v => (float)v).ToArray(),
                LowShelfFrequency = (float)_lowShelfFrequency,
                LowShelfGain = (float)_lowShelfGain,
                LowShelfQ = (float)_lowShelfQ,
                HighShelfFrequency = (float)_highShelfFrequency,
                HighShelfGain = (float)_highShelfGain,
                HighShelfQ = (float)_highShelfQ,
                HRTFIntensity = (float)SliderHRTF.Value,
                SurroundWidth = (int)SliderSurround.Value,
                BassBoost = 1.0f
            };

            _presetManager?.AddCustomPreset(newPreset);
            await _presetManager!.SavePresetsAsync();

            // 新建的预设直接选中，方便继续调整
            ApplyPreset(newPreset);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"创建预设失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnSaveEQ_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!IsCurrentPresetEditable())
            {
                MessageBox.Show("内置预设是只读的。请切换到「自定义」或新建一个预设后再保存。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var currentPreset = _presetManager?.GetCurrentPreset();
            if (currentPreset == null)
            {
                MessageBox.Show("请先选择一个预设", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            currentPreset.EQBands = _eqBands.Select(v => (float)v).ToArray();
            currentPreset.EQFrequencies = _eqFrequencies.Select(v => (float)v).ToArray();
            currentPreset.EQQValues = _eqQValues.Select(v => (float)v).ToArray();
            currentPreset.LowShelfFrequency = (float)_lowShelfFrequency;
            currentPreset.LowShelfGain = (float)_lowShelfGain;
            currentPreset.LowShelfQ = (float)_lowShelfQ;
            currentPreset.HighShelfFrequency = (float)_highShelfFrequency;
            currentPreset.HighShelfGain = (float)_highShelfGain;
            currentPreset.HighShelfQ = (float)_highShelfQ;
            currentPreset.BassBoost = 1.0f;
            _presetManager?.UpdatePreset(currentPreset.Id, currentPreset);
            await _presetManager!.SavePresetsAsync();

            MessageBox.Show("EQ 设置已保存", "成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        Log("应用关闭");
        try { UiSettingsStore.Save(CaptureUiSettings()); }
        catch { /* 忽略 */ }

        CloseDirectionOverlay();
        _statusTimer?.Stop();
        StopAudio();
        _testTonePlayer?.Dispose();
        _hidManager?.Dispose();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        base.OnClosed(e);
    }
}
