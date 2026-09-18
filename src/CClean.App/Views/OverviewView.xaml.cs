using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Monitoring;
using CClean.Core.Storage;

namespace CClean.App.Views;

public partial class OverviewView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    private SpaceSurvey? survey;
    private SpaceSurveyResult? result;
    private CancellationTokenSource? scanning;
    private bool registering;
    public event Func<Task>? CrashDumpPreviewRequested;
    public OverviewView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace;
        ScanRoot.Text = Path.GetPathRoot(Environment.SystemDirectory)!;
        Loaded += async (_, _) => await Update();
        Unloaded += (_, _) => { scanning?.Cancel(); if (scanning is null) { survey?.Dispose(); survey = null; } };
        workspace.Indexed += async () => { if (IsVisible) await Update(); };
    }
    private async void Refresh(object sender, RoutedEventArgs e) => await Update();
    private async Task Update()
    {
        try
        {
            var space = await Task.Run(() => VolumeSpaceReader.Read(Environment.SystemDirectory));
            DiskSummary.Text = $"{Path.GetPathRoot(Environment.SystemDirectory)} 可用 {space.UserAvailableBytes / 1073741824d:F1} GiB，共 {space.TotalBytes / 1073741824d:F1} GiB";
            DiskUsage.Value = space.TotalBytes == 0 ? 0 : 100d * (space.TotalBytes - space.UserAvailableBytes) / space.TotalBytes;
            var totals = await Task.Run(() => workspace.Store.Totals(Environment.SystemDirectory));
            Logical.Text = IndexedFile.FormatBytes(totals.Logical);
            Allocated.Text = IndexedFile.FormatBytes(totals.Allocated);
            Growth.Text = totals.Unknown > 0 ? "未知" : (totals.Growth > 0 ? "+" : "") + IndexedFile.FormatBytes(totals.Growth);
            Coverage.Text = $"仅已登记范围：{totals.Files} 个文件，{totals.Unknown} 个未知对象；共 {workspace.Projects.Count} 个登记根。不是全盘覆盖。";
        }
        catch (Exception error) { Coverage.Text = "统计不可用：" + error.Message; }
    }
    private void Browse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择要盘点的目录" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ScanRoot.Text = dialog.FolderName;
    }
    private async void Scan(object sender, RoutedEventArgs e) => await RunSurvey();
    private async Task RunSurvey()
    {
        if (scanning is not null) return;
        scanning = new(); var token = scanning.Token;
        ScanButton.IsEnabled = ScanRoot.IsEnabled = BrowseButton.IsEnabled = false;
        CancelButton.IsEnabled = true; ScanProgress.Visibility = Visibility.Visible;
        SetSelection();
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ScanRoot.Text));
            if (survey is null || !root.Equals(survey.Root, StringComparison.OrdinalIgnoreCase) || result?.HasMore != true)
            {
                survey?.Dispose(); survey = null; result = null; Results.ItemsSource = null;
                survey = await Task.Run(() => new SpaceSurvey(root), token);
            }
            var timer = Stopwatch.StartNew();
            do
            {
                ScanStatus.Text = "正在盘点文件大小与项目标记，可随时停止…";
                result = await Task.Run(() => survey.Next(token), token);
                ShowResults();
            } while (result.HasMore && timer.Elapsed < TimeSpan.FromSeconds(30) && !token.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            if (survey is not null) { result = survey.Snapshot(); ShowResults(); }
            ScanStatus.Text += " 已停止，可继续。";
        }
        catch (Exception error) { ScanStatus.Text = "盘点未完成：" + error.Message; }
        finally
        {
            scanning.Dispose(); scanning = null;
            ScanButton.IsEnabled = ScanRoot.IsEnabled = BrowseButton.IsEnabled = true;
            CancelButton.IsEnabled = false; ScanProgress.Visibility = Visibility.Collapsed;
            ScanLabel.Text = result?.HasMore == true ? "继续盘点" : "重新盘点";
            SetSelection();
            if (!IsLoaded) { survey?.Dispose(); survey = null; }
        }
    }
    private void Cancel(object sender, RoutedEventArgs e) => scanning?.Cancel();
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (Results is not null) ShowResults(); }
    private void ShowResults()
    {
        if (result is null) return;
        var versions = result.Folders.Where(f => f.Project).GroupBy(f => f.VersionFamily).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var rows = result.Folders.Where(f => !f.Path.Equals(survey?.Root, StringComparison.OrdinalIgnoreCase) || f.Project);
        if (Filter.SelectedIndex == 1) rows = rows.Where(f => f.Project);
        if (Filter.SelectedIndex == 2) rows = rows.Where(f => f.Category == "缓存 / 临时文件");
        Results.ItemsSource = rows.OrderByDescending(f => f.Bytes).Take(200)
            .Select(f => f with { Category = versions.Contains(f.VersionFamily) ? "疑似版本副本" : f.Category,
                Share = result.Bytes == 0 ? 0 : 100d * f.Bytes / result.Bytes }).ToArray();
        var state = result.Limited ? "达到目录上限，请选更小范围" : result.HasMore ? "部分结果 · 可继续" : "本轮结束";
        ScanStatus.Text = $"{state}：{result.Files:N0} 文件，已读 {IndexedFile.FormatBytes(result.Bytes)}，项目 {result.Folders.Count(f => f.Project)} 个；跳过 / 不可读 {result.Skipped:N0} 项。最多显示 200 行。";
        SetSelection();
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => SetSelection();
    private void SetSelection()
    {
        if (OpenFolderButton is null) return;
        var selected = Results.SelectedItem as SpaceFolder;
        OpenFolderButton.IsEnabled = selected is not null;
        DrillButton.IsEnabled = selected is not null && scanning is null;
        RegisterButton.IsEnabled = selected?.Project == true && scanning is null && !registering;
        if (selected is null) return;
        SelectedPath.Text = selected.Path;
        var siblings = result?.Folders.Where(f => f.Project && f.VersionFamily == selected.VersionFamily).ToArray() ?? [];
        Advice.Text = selected.Category == "缓存 / 临时文件"
            ? "这是缓存或临时文件位置，不是已批准删除清单。开发缓存清除后可能需要联网重建；系统临时文件请使用上方 Windows 清理入口。"
            : siblings.Length > 1
                ? $"发现 {siblings.Length} 个同目录下的相似版本名：{string.Join("、", siblings.Select(f => f.Name))}。仅名称线索，未验证内容相同；源码、配置、历史和未提交修改均保留。"
                : selected.Project ? "发现项目标记，可加入持续监测。盘点只读取元数据，不会删除源码、依赖或构建产物。"
                : "大小包含子目录，并非可释放空间；可深入盘点定位大目录。来源不明的文件不自动删除。";
    }
    private async void Drill(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not SpaceFolder selected || scanning is not null) return;
        ScanRoot.Text = selected.Path; await RunSurvey();
    }
    private async void Register(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not SpaceFolder { Project: true } selected || registering) return;
        registering = true; RegisterButton.IsEnabled = false;
        try { await workspace.Register(selected.Name, selected.Path, "通用工具"); Advice.Text = "已加入持续监测，正在建立基线；没有启用清理规则。"; }
        catch (Exception error) { Advice.Text = "未登记：" + error.Message; }
        finally { registering = false; RegisterButton.IsEnabled = scanning is null && Results.SelectedItem is SpaceFolder { Project: true }; }
    }
    private void OpenFolder(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is SpaceFolder selected)
            Launch(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                { UseShellExecute = true, ArgumentList = { selected.Path } });
    }
    private async void PreviewDumps(object sender, RoutedEventArgs e) { if (CrashDumpPreviewRequested is not null) await CrashDumpPreviewRequested(); }
    private void OpenStorage(object sender, RoutedEventArgs e) => Launch(new("ms-settings:storagesense") { UseShellExecute = true });
    private void OpenDiskCleanup(object sender, RoutedEventArgs e)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "cleanmgr.exe");
        if (!File.Exists(executable)) { OpenStorage(sender, e); return; }
        Launch(new(executable) { UseShellExecute = true, ArgumentList = { "/d", Path.GetPathRoot(Environment.SystemDirectory)![..1] } });
    }
    private void Launch(ProcessStartInfo info)
    {
        try { Process.Start(info)?.Dispose(); Advice.Text = "已打开系统工具，请在该窗口核对并确认操作。CClean 没有执行删除。"; }
        catch (Exception error) { Advice.Text = "无法打开：" + error.Message; }
    }
}
