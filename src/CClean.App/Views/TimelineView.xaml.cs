using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Attribution;
using CClean.Core.History;

namespace CClean.App.Views;

public partial class TimelineView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    private TimelineNode? selected;
    private int revision;
    private bool querying, refreshPending;
    private Core.Projects.ProjectModule? scope;
    public async Task ShowProject(Core.Projects.ProjectModule project)
    {
        scope = project; revision++; selected = null; Nodes.ItemsSource = null;
        Graph.Load(new([], false, DateTimeOffset.UtcNow, []), [], [], DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);
        ProjectFilter.ItemsSource = new[] { new ProjectChoice(project.RegistrationId?.ToString() ?? "unregistered", project.Name) };
        ProjectFilter.DisplayMemberPath = "Name"; ProjectFilter.SelectedIndex = 0; ProjectFilter.IsEnabled = false;
        await Update();
    }
    public event Action<string>? CurrentDirectoryRequested;
    public TimelineView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace;
        Graph.Selected += Show;
        Loaded += async (_, _) =>
        {
            if (scope is not null) { await Update(); return; }
            var selectedId = (ProjectFilter.SelectedItem as ProjectChoice)?.Id;
            ProjectFilter.ItemsSource = new[] { new ProjectChoice(null, "全部项目") }.Concat(workspace.Projects.Select(p => new ProjectChoice(p.Id.ToString(), p.Name))).ToArray();
            ProjectFilter.DisplayMemberPath = "Name";
            ProjectFilter.SelectedItem = ((IEnumerable<ProjectChoice>)ProjectFilter.ItemsSource).FirstOrDefault(p => p.Id == selectedId) ?? ProjectFilter.Items[0];
            await Update();
        };
        workspace.Indexed += async () => { if (IsVisible) await Update(); };
    }
    private async void Filter(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await Update(); }
    private async void Refresh(object sender, RoutedEventArgs e) => await Update();
    private async Task Update()
    {
        if (querying) { refreshPending = true; return; }
        querying = true;
        var request = ++revision;
        var until = DateTimeOffset.UtcNow;
        var since = until - (TimeRange.SelectedIndex switch { 0 => TimeSpan.FromMinutes(15), 2 => TimeSpan.FromDays(1), 3 => TimeSpan.FromDays(7), _ => TimeSpan.FromHours(1) });
        var project = scope is null ? (ProjectFilter.SelectedItem as ProjectChoice)?.Id : scope.RegistrationId?.ToString() ?? "unregistered";
        var tool = ToolFilter.SelectedIndex == 0 ? null : ((ComboBoxItem)ToolFilter.SelectedItem).Content.ToString();
        EvidenceKind? evidence = EvidenceFilter.SelectedIndex switch { 1 => EvidenceKind.Context, 2 => EvidenceKind.Shared, 3 => EvidenceKind.Unknown, _ => null };
        var bucketSeconds = TimeRange.SelectedIndex >= 2 ? 3600 : 60;
        var sessions = workspace.Sessions.All; var projects = workspace.Projects.ToArray();
        var root = scope?.Root;
        Coverage.Text = "正在查询有界历史…";
        try
        {
            var result = await Task.Run(() =>
            {
                var changes = workspace.Store.Changes(since, 10000, project, root);
                return TimelineQuery.Build(changes, sessions, workspace.Store.Gaps(since, project), since, until, workspace.Store.EarliestHistory(project, root),
                    project, tool, evidence, bucketSeconds: bucketSeconds, inputTruncated: changes.Count == 10000);
            });
            if (request != revision || !IsVisible) return;
            Nodes.ItemsSource = result.Nodes; Graph.Load(result, projects, sessions, since, until);
            selected = null; Details.Text = result.Nodes.Count == 0 ? "所选时段没有观察到变化。请检查项目是否已登记、暂停或调整时间范围；离线历史不会自动补齐。" : "选择一个变化节点，查看依据和涉及文件。";
            Coverage.Text = $"{result.Nodes.Count} 个聚合节点；{result.Gaps.Count} 条覆盖缺口；最早保留 {result.EarliestAvailable.ToLocalTime():MM-dd HH:mm}。" +
                (result.Truncated ? "达到读取或 500 节点上限，请缩小时间范围。" : "详细历史最多 7 天；仅代表登记范围。") +
                (result.Gaps.Count > 0 ? " 最近缺口：" + result.Gaps[0].Reason : "");
            if (scope is { RegistrationId: null }) Coverage.Text = "此项目尚未登记监测，没有已记录的文件开发线；加入监测后开始记录。已有工具会话请查看“项目会话”，不会补造离线文件历史。";
        }
        catch (Exception error) { if (request == revision) Coverage.Text = "历史查询失败：" + error.Message; }
        finally
        {
            querying = false;
            if (refreshPending) { refreshPending = false; if (IsVisible) await Update(); }
        }
    }
    private void SelectNode(object sender, SelectionChangedEventArgs e) { if (Nodes.SelectedItem is TimelineNode node) Show(node); }
    private void Show(TimelineNode node)
    {
        selected = node; Graph.Select(node);
        Details.Text = $"{node.LocalTime} · {node.EvidenceLabel} · {node.Summary}\n依据：{node.Evidence.Reason}\n目录：{node.Directory}\n" +
            string.Join("\n", node.Files.Take(100).Select(f => $"{f.Path}：{f.Before?.ToString() ?? "未知"} → {f.After?.ToString() ?? "未知/消失"} B（{f.Kind}）")) +
            (node.Files.Count > 100 ? "\n仅列前 100 条观察，当前目录文件可单独查询。" : "") + "\n历史路径可能已变化；任何清理都需重新获取当前对象并预览。";
    }
    private void OpenFiles(object sender, RoutedEventArgs e)
    {
        if (selected is null) { Details.Text = "请先选择变化节点。"; return; }
        CurrentDirectoryRequested?.Invoke(selected.Directory);
    }
    private sealed record ProjectChoice(string? Id, string Name);
}
