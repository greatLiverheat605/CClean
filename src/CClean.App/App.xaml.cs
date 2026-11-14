using System.Configuration;
using System.Data;
using System.Windows;
using CClean.App.Lifecycle;

namespace CClean.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private SingleInstance? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var data = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR") ??
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CClean");
        var restorePending = false;
        var start = SingleInstance.TryStart(data, Dispatcher,
            () => { if (MainWindow is CClean.App.MainWindow window) window.RestoreWindow(); else restorePending = true; },
            error => MessageBox.Show("单实例唤醒通道不可用：" + error.Message, "CClean", MessageBoxButton.OK, MessageBoxImage.Error));
        if (start.Status != SingleInstance.StartStatus.Primary)
        {
            if (start.Status == SingleInstance.StartStatus.Failed)
                MessageBox.Show("无法确认独占实例，未启动观察或清理：" + start.Error, "CClean", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(start.Status == SingleInstance.StartStatus.Failed ? 1 : 0);
            return;
        }
        instance = start.Instance;
        try
        {
            var window = new CClean.App.MainWindow();
            MainWindow = window; window.Show();
            if (restorePending) window.RestoreWindow();
        }
        catch (Exception error)
        {
            MessageBox.Show("窗口无法启动：" + error.Message, "CClean", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
