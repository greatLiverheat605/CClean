using System.IO;
using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Cleanup;
using CClean.Core.Monitoring;
using CClean.Core.Projects;

namespace CClean.App.Views;

public partial class ProjectSpaceView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    private ProjectModule? project;
    private SpaceSurvey? survey;
    private CancellationTokenSource? work;
    private int generation;
    private string[] approvedCandidates = [];
    public event Action<string[]>? PreviewRequested;
    public ProjectSpaceView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace;
        Loaded += async (_, _) => { if (project is not null && survey is null) await RunSurvey(); };
        Unloaded += (_, _) => work?.Cancel();
    }
    public void ShowProject(ProjectModule selected)
    {
        generation++; work?.Cancel(); project = selected;
        if (work is null) { survey?.Dispose(); survey = null; }
        Locations.ItemsSource = null; approvedCandidates = []; PreviewButton.IsEnabled = false;
        Details.Text = "目录大小不等于可释放空间；父子目录不能相加。";
        Status.Text = "此项目尚未盘点，点击“盘点 / 继续”读取目录占用。";
        if (IsLoaded && work is null) _ = RunSurvey();
    }
    private async void Scan(object sender, RoutedEventArgs e) => await RunSurvey();
    private async Task RunSurvey()
    {
        if (work is not null || project is null) return;
        var selectedProject = project; var request = generation; work = new(); var token = work.Token;
        ScanButton.IsEnabled = InspectButton.IsEnabled = false;
        approvedCandidates = []; PreviewButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                if (survey is null || !survey.Root.Equals(selectedProject.Root, StringComparison.OrdinalIgnoreCase))
                { survey?.Dispose(); survey = new(selectedProject.Root); }
                return survey.Next(token);
            }, token);
            if (request != generation) return;
            Locations.ItemsSource = result.Folders.OrderByDescending(f => f.Bytes).Take(200).Select(f =>
                new Location(f.Path, f.Path.Equals(selectedProject.Root, StringComparison.OrdinalIgnoreCase) ? "整个项目（包含下方子目录）" : Path.GetRelativePath(selectedProject.Root, f.Path), f.Size,
                    workspace.Rules.Scopes.Any(r => r.Enabled && ProtectionPolicy.Contains(r.Root, f.Path)) ? "规则覆盖 · 待逐文件复核" :
                    new[] { "node_modules", "bin", "obj", ".cache", "__pycache__" }.Contains(f.Name, StringComparer.OrdinalIgnoreCase) ? "构建 / 依赖线索 · 默认保护" : "源码 / 其他 · 默认保护")).ToArray();
            Status.Text = $"{(result.HasMore ? "部分盘点，可继续" : result.Limited ? "达到目录上限，请缩小范围" : "本轮盘点结束")} · {result.Files:N0} 文件 · {result.Skipped} 项跳过。标记不是删除授权，最多显示 200 行。";
        }
        catch (OperationCanceledException) { if (request == generation) Status.Text = "已停止盘点，可继续。"; }
        catch (Exception error) { if (request == generation) Status.Text = "无法盘点此项目：" + error.Message; }
        finally
        {
            work.Dispose(); work = null; ScanButton.IsEnabled = true;
            if (request != generation) { survey?.Dispose(); survey = null; }
            InspectButton.IsEnabled = Locations.SelectedItem is not null;
            if (request != generation && IsLoaded) await RunSurvey();
        }
    }
    private void Stop(object sender, RoutedEventArgs e) => work?.Cancel();
    private void Selected(object sender, SelectionChangedEventArgs e)
    {
        approvedCandidates = []; PreviewButton.IsEnabled = false;
        InspectButton.IsEnabled = work is null && Locations.SelectedItem is Location;
        Details.Text = Locations.SelectedItem is Location row ? row.Path + "\n" + row.Marker : "选中一个位置，检查是否存在符合规则的文件。";
    }
    private async void Inspect(object sender, RoutedEventArgs e)
    {
        if (work is not null || Locations.SelectedItem is not Location location || project is null) return;
        var request = generation; work = new(); var token = work.Token;
        InspectButton.IsEnabled = ScanButton.IsEnabled = PreviewButton.IsEnabled = false; Locations.IsEnabled = false; approvedCandidates = [];
        try
        {
            var context = await workspace.CleanupContext();
            var result = await Task.Run(() =>
            {
                var snapshot = DirectoryScanner.Scan(location.Path, token, maxFiles: 1001);
                var paths = snapshot.Files.Values.Select(f => f.Path).ToArray();
                if (snapshot.Gaps.Count > 0) context = context with { Activity = context.Activity with {
                    Scopes = context.Activity.Scopes.Append(new(location.Path, false, false, "此位置扫描存在缺口")).ToArray() } };
                return CleanupPlanner.Preview(paths, context, token);
            }, token);
            if (request != generation) return;
            var stillCurrent = result.IsCurrent(await workspace.CleanupContext(), DateTimeOffset.UtcNow);
            if (request != generation) return;
            approvedCandidates = result.Truncated || !stillCurrent ? [] : result.Items.Where(i => i.Decision.Eligible).Select(i => i.Path).ToArray();
            var marker = approvedCandidates.Length > 0 ? $"可清理候选 {approvedCandidates.Length} 项 · 待确认" : "受保护 / 当前无可清理候选";
            var rows = ((IEnumerable<Location>)Locations.ItemsSource).Select(r => r.Path == location.Path ? r with { Marker = marker } : r).ToArray();
            Locations.ItemsSource = rows; Locations.SelectedItem = rows.First(r => r.Path == location.Path);
            // Selection clears prior candidates; only this completed inspection may publish a new set.
            approvedCandidates = result.Truncated || !stillCurrent ? [] : result.Items.Where(i => i.Decision.Eligible).Select(i => i.Path).ToArray();
            PreviewButton.IsEnabled = approvedCandidates.Length > 0;
            Details.Text = location.Path + "\n" + marker + (result.Truncated ? "；达到 1,000 项上限，请选择更小位置。" : "") +
                (!stillCurrent ? "；活动/保护状态有变化或扫描存在缺口，需重新复核。" : "") + "\n" +
                string.Join("；", result.Items.Where(i => !i.Decision.Eligible).GroupBy(i => i.Decision.Category).Select(g => $"{g.Key} {g.Count()} 项"));
        }
        catch (OperationCanceledException) { if (request == generation) Details.Text = "检查已取消，没有删除文件。"; }
        catch (Exception error) { if (request == generation) Details.Text = "检查失败：" + error.Message; }
        finally
        {
            work.Dispose(); work = null; Locations.IsEnabled = ScanButton.IsEnabled = true;
            InspectButton.IsEnabled = Locations.SelectedItem is not null;
            if (request != generation) { survey?.Dispose(); survey = null; if (IsLoaded) await RunSurvey(); }
        }
    }
    private void Preview(object sender, RoutedEventArgs e) { if (approvedCandidates.Length > 0) PreviewRequested?.Invoke(approvedCandidates); }
    private sealed record Location(string Path, string Relative, string Size, string Marker);
}
