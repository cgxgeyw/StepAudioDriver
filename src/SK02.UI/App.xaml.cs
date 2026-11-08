namespace SK02.UI;

using System.Windows;

/// <summary>
/// 应用程序启动类
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 配置应用级错误处理
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            MessageBox.Show(
                $"应用程序出现未捕获的异常:\n{args.ExceptionObject}",
                "严重错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (sender, args) =>
        {
            MessageBox.Show(
                $"UI 线程出现异常:\n{args.Exception.Message}",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            args.Handled = true;
        };
    }
}
