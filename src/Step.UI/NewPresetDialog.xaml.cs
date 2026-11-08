using System.Windows;
using System.Windows.Input;

namespace Step.UI;

/// <summary>
/// 与主界面同风格的新建预设对话框（自绘标题栏，不用系统弹框）
/// </summary>
public partial class NewPresetDialog : Window
{
    public NewPresetDialog()
    {
        InitializeComponent();

        NameBox.Text = "新预设";

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };

        UpdateCreateState();
    }

    public string PresetName => NameBox.Text.Trim();

    public string PresetDescription => DescBox.Text.Trim();

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateCreateState();
    }

    private void UpdateCreateState()
    {
        if (BtnCreate != null)
            BtnCreate.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch { /* ignore */ }
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NameBox.Text))
            DialogResult = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            return;
        }

        // 描述框里的回车用于换行
        if (e.Key == Key.Enter && !DescBox.IsKeyboardFocusWithin)
        {
            BtnCreate_Click(this, new RoutedEventArgs());
            return;
        }

        base.OnKeyDown(e);
    }
}
