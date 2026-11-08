using System.Windows;
using System.Windows.Input;

namespace Step.UI;

public partial class CloseChoiceDialog : Window
{
    public string Choice { get; private set; } = "exit";
    public bool Remember { get; private set; }

    public CloseChoiceDialog()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch { /* 鼠标已抬起 */ }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        Choice = "minimize";
        Remember = ChkRemember.IsChecked == true;
        DialogResult = true;
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        Choice = "exit";
        Remember = ChkRemember.IsChecked == true;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            DialogResult = false;
    }
}
