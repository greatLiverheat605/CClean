using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace CClean.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly CancellationTokenSource closing = new();
    private readonly System.Windows.Threading.DispatcherTimer capacityTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool capacityReading;
    private bool dark;
    private ViewModels.WorkspaceViewModel? workspace;
    private Views.ProjectSetupView? projectSetup;
    private Views.OverviewView? overview;
    private Views.FilesView? files;
    private Views.TimelineView? timeline;
    private Views.CleanupPreviewView? cleanup;
    private Views.CleanupHistoryView? cleanupHistory;
    public MainWindow()
    {
        InitializeComponent();
        Navigation.SelectedIndex = 0;
        capacityTimer.Tick += async (_, _) => await ReadCapacity();
        capacityTimer.Start();
        FitWorkArea();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(FitWorkArea);
        Closing += async (_, e) =>
        {
            if (workspace?.CleanupRunning != true) return;
            e.Cancel = true; workspace.CancelCleanup();
            try { await workspace.CleanupTask!; } catch { }
            Close();
        };
    }
    private void FitWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height));
        if (!double.IsNaN(Left)) Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        if (!double.IsNaN(Top)) Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ReadCapacity();
        try
        {
            workspace = new();
            workspace.Changed += () => Status.Text = workspace.Status;
            projectSetup = new(workspace);
            overview = new(workspace); files = new(workspace); timeline = new(workspace);
            cleanup = new(workspace);
            cleanupHistory = new(workspace);
            overview.CrashDumpPreviewRequested += async () => { Navigation.SelectedIndex = 3; await cleanup.PreviewCrashDumps(); };
            files.PreviewRequested += async paths => { Navigation.SelectedIndex = 3; await cleanup.Preview(paths); };
            timeline.CurrentDirectoryRequested += async directory => { Navigation.SelectedIndex = 2; await files.ShowDirectory(directory); };
            if (Navigation.SelectedIndex == 0) PageContent.Content = overview;
            await workspace.Resume();
        }
        catch (Exception error) { Status.Text = "本地配置不可用：" + error.Message; }
    }
    private void OnClosed(object? sender, EventArgs e) { capacityTimer.Stop(); closing.Cancel(); workspace?.Dispose(); closing.Dispose(); }
    private async void Refresh(object sender, RoutedEventArgs e) => await ReadCapacity();
    private async Task ReadCapacity()
    {
        if (capacityReading || closing.IsCancellationRequested) return;
        capacityReading = true;
        try
        {
            var space = await Task.Run(() => Core.Storage.VolumeSpaceReader.Read(Environment.SystemDirectory), closing.Token);
            if (workspace is not null) await workspace.EnforceBudget();
            Capacity.Text = $"C: 可用 {space.UserAvailableBytes / 1073741824d:F1} GiB / {space.TotalBytes / 1073741824d:F1} GiB  ·  更新 {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status.Text = "容量读取失败：" + error.Message; }
        finally { capacityReading = false; }
    }

    private void Navigate(object sender, SelectionChangedEventArgs e)
    {
        if (PageContent is null || Navigation.SelectedItem is not ListBoxItem selected) return;
        var title = selected.Content.ToString()!;
        PageTitle.Text = title;
        if (title == "空间与快清" && overview is not null) { PageContent.Content = overview; return; }
        if (title == "开发时间线" && timeline is not null) { PageContent.Content = timeline; return; }
        if (title == "文件与空间" && files is not null) { PageContent.Content = files; return; }
        if (title == "清理中心" && cleanup is not null) { PageContent.Content = cleanup; return; }
        if (title == "清理记录" && cleanupHistory is not null) { PageContent.Content = cleanupHistory; return; }
        if (title == "项目与设置" && projectSetup is not null) { PageContent.Content = projectSetup; return; }
        PageContent.Content = new TextBlock { Text = title == "空间与快清" ? "尚未登记监测范围\n\n先登记一个项目或目录，建立当前文件基线后再查看增长。" : "此页面正在接入已验证的本地能力。当前未开放清理执行。",
            FontSize = 18, Margin = new Thickness(20) };
    }

    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        dark = !dark;
        var colors = dark ? new[] { "#171D25", "#222B36", "#F2F5F8", "#B9C5D2", "#8BB9F7", "#465568", "#142237" } :
            new[] { "#F5F6F8", "#FFFFFF", "#17212B", "#526170", "#245BA8", "#D5DCE4", "#FFFFFF" };
        var keys = new[] { "Surface", "Panel", "Ink", "Muted", "Accent", "Line", "OnAccent" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
}
