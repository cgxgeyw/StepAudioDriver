using System.Windows;
using System.Windows.Input;
using Step.Core.Audio;

namespace Step.UI;

/// <summary>
/// 置顶的小型声场雷达：根据 7.1 输入电平显示声音方向。
/// </summary>
public partial class DirectionOverlayWindow : Window
{
    private const double Center = 85;
    private const double PlotRadius = 70;

    private double _smoothX;
    private double _smoothY;

    public DirectionOverlayWindow()
    {
        InitializeComponent();
    }

    public void UpdateLevels(float[] levels, int channels)
    {
        if (!IsVisible)
            return;

        if (channels <= 0 || levels.Length == 0)
        {
            Clear();
            return;
        }

        int count = Math.Min(channels, 8);
        var direction = SoundDirectionAnalyzer.Analyze(levels.AsSpan(0, count));

        bool hasSignal = direction.Level > 1e-4f && direction.Confidence > 0.02f;

        double targetX = hasSignal ? direction.X : 0;
        double targetY = hasSignal ? direction.Y : 0;

        // 平滑，避免方向点随每一个音频块抖动
        _smoothX = _smoothX * 0.65 + targetX * 0.35;
        _smoothY = _smoothY * 0.65 + targetY * 0.35;

        double magnitude = Math.Sqrt(_smoothX * _smoothX + _smoothY * _smoothY);
        double x = Center + _smoothX * PlotRadius;
        double y = Center - _smoothY * PlotRadius;

        DirectionLine.X2 = x;
        DirectionLine.Y2 = y;
        Canvas_SetLeft(DirectionDot, x - 6);
        Canvas_SetTop(DirectionDot, y - 6);

        double db = 20 * Math.Log10(Math.Max(direction.Level, 1e-6f));
        double opacity = hasSignal
            ? Math.Clamp((db + 52) / 46, 0.2, 1.0)
            : 0.15;

        DirectionDot.Opacity = opacity;
        DirectionLine.Opacity = opacity * 0.55;
        DirectionText.Opacity = hasSignal ? 0.95 : 0.45;

        if (!hasSignal)
        {
            DirectionText.Text = "—";
            return;
        }

        string label = DescribeDirection(direction.X, direction.Y, magnitude);
        DirectionText.Text = magnitude < 0.22 ? "中央 / 环绕" : $"声音 · {label}";
    }

    public void Clear()
    {
        _smoothX = 0;
        _smoothY = 0;

        DirectionLine.X2 = Center;
        DirectionLine.Y2 = Center;
        Canvas_SetLeft(DirectionDot, Center - 6);
        Canvas_SetTop(DirectionDot, Center - 6);
        DirectionDot.Opacity = 0.15;
        DirectionLine.Opacity = 0.15;
        DirectionText.Text = "—";
        DirectionText.Opacity = 0.45;
    }

    private static string DescribeDirection(float x, float y, double magnitude)
    {
        if (magnitude < 0.12)
            return "中央";

        double angle = Math.Atan2(x, y) * 180 / Math.PI;
        if (angle < 0)
            angle += 360;

        return angle switch
        {
            < 22.5 or >= 337.5 => "前",
            < 67.5 => "右前",
            < 112.5 => "右",
            < 157.5 => "右后",
            < 202.5 => "后",
            < 247.5 => "左后",
            < 292.5 => "左",
            _ => "左前"
        };
    }

    private static void Canvas_SetLeft(UIElement element, double value)
    {
        System.Windows.Controls.Canvas.SetLeft(element, value);
    }

    private static void Canvas_SetTop(UIElement element, double value)
    {
        System.Windows.Controls.Canvas.SetTop(element, value);
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CloseButton.IsMouseOver)
            return;

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch { /* 鼠标已抬起 */ }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
