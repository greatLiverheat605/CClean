using System.IO;
using System.Windows;
using CClean.Core.Storage;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Projects;

namespace CClean.App.Views;

public partial class ProjectWorkbenchView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    private readonly TimelineView timeline;
    private readonly SessionsView sessions;
    private readonly ProjectComparisonView comparison = new();
    private readonly AppPreferences preferences;
    private readonly HashSet<string> discoveredRoots = new(StringComparer.OrdinalIgnoreCase);
    private ProjectDiscovery? discovery;
    private bool discoveryFinished;
    private bool loadedDiscoverySummary;
    private Guid? discoveryRegistrationId;
    private long discoveryScopeRevision = 1;
    private CancellationTokenSource? discovering;
    private TaskCompletionSource? pendingDiscovery;
    private readonly HashSet<Task> pendingReads = [];
    private int summaryRevision;
    private ProjectModule? selected;
    private int preferredSection;
    private bool opening;
    private int generation;
    public event Action<string>? SpaceRequested;
    public event Action<string?>? ManageScopeRequested;
    public ProjectWorkbenchView(WorkspaceViewModel workspace, AppPreferences preferences, bool activityWorkspace = false)
    {
        InitializeComponent(); this.workspace = workspace; this.preferences = preferences;
        preferredSection = activityWorkspace ? 0 : 4;
        TimelineTab.Visibility = SessionsTab.Visibility = activityWorkspace ? Visibility.Visible : Visibility.Collapsed;
        ComparisonTab.Visibility = activityWorkspace ? Visibility.Collapsed : Visibility.Visible;
        if (activityWorkspace)
        {
            WorkspaceHeading.Text = "选择开发活动范围";
            WorkspaceHelp.Text = "选择项目，查看已记录的开发时间线与工具会话。历史与当前活动分别呈现；未登记、暂停和观察缺口不会当成无活动。";
        }
        DiscoveryRoot.Text = preferences.DiscoveryRoot; ComparisonContent.Content = comparison;
        timeline = new(workspace); sessions = new(workspace);
        TimelineContent.Content = timeline; SessionsContent.Content = sessions;
        timeline.CurrentDirectoryRequested += directory => SpaceRequested?.Invoke(directory);
        workspace.SessionsDiscovered += RefreshModules;
        workspace.Projects.CollectionChanged += (_, _) => RefreshModules();
        Loaded += (_, _) => { LoadDiscoverySummary(); RefreshModules(); };
        Unloaded += (_, _) => discovering?.Cancel();
        workspace.Indexed += async () => { if (!workspace.Stopping && IsVisible && selected is not null) await RefreshSummary(selected); };
    }
    public void ShowSection(int section)
    {
        if (workspace.Stopping) return;
        preferredSection = section;
        if (selected is not null) { ModulesPanel.Visibility = Visibility.Collapsed; DetailPanel.Visibility = Visibility.Visible; Sections.SelectedIndex = section; }
        else Back(this, new());
    }
    public void ShowModules() => Back(this, new());
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (workspace is not null) RefreshModules(); }
    private async void Refresh(object sender, RoutedEventArgs e) { if (workspace.Stopping) return; await workspace.DiscoverSessions(); RefreshModules(); }
    private void RefreshModules()
    {
        if (workspace.Stopping) return;
        var query = Search.Text.Trim();
        var all = ProjectCatalog.Build(workspace.Projects, workspace.DiscoveredSessions, discoveredRoots);
        var matches = all.Where(p => (p.Name + " " + p.Root).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        Modules.ItemsSource = matches;
        var current = selected?.RegistrationId is Guid id ? all.FirstOrDefault(p => p.RegistrationId == id) :
            all.FirstOrDefault(p => p.Root.Equals(selected?.Root, StringComparison.OrdinalIgnoreCase));
        if (selected is not null && current is null)
        {
            selected = null; generation++; summaryRevision++;
            DetailPanel.Visibility = Visibility.Collapsed; ModulesPanel.Visibility = Visibility.Visible;
        }
        if (current is not null && selected is not null &&
            (current.RegistrationId != selected.RegistrationId || current.Root != selected.Root || current.Revision != selected.Revision ||
                current.Paused != selected.Paused || current.HasMonitoredDescendants != selected.HasMonitoredDescendants || current.Kind != selected.Kind)) _ = OpenProject(current);
        ModuleStatus.Text = $"{all.Count} 个项目 / 工具工作目录 · {all.Count(p => p.RegistrationId.HasValue)} 个已纳入监测范围。" +
            (all.Count == 0 ? "正在发现已支持的本机会话，也可到“管理范围”登记目录；这不代表找齐全部 Agent。" : matches.Length == 0 ? "没有匹配项目，请调整搜索。" : "点击项目模块进入详情；名称相同的项目按路径区分。");
    }
    private long DiscoveryScopeRevision(string root) => workspace.Projects.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase))?.ScopeRevision ?? 1;
    private void LoadDiscoverySummary()
    {
        if (workspace.Stopping || loadedDiscoverySummary) return;
        loadedDiscoverySummary = true;
        if (string.IsNullOrWhiteSpace(preferences.DiscoveryRoot)) return;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(preferences.DiscoveryRoot));
            var summary = workspace.Store.LoadDiscoverySummary(root, DiscoveryScopeRevision(root));
            if (summary is null) return;
            foreach (var candidate in summary.Roots) discoveredRoots.Add(candidate);
            DiscoveryStatus.Text = $"过期发现摘要 {summary.CapturedAt.ToLocalTime():MM-dd HH:mm:ss}，范围 v{summary.ScopeRevision}：{root}；保存 {summary.Roots.Count} 个候选。" +
                (summary.RootsTruncated ? "保存候选已截断；" : "") + "需重新发现，不代表当前覆盖，未自动开始监测。";
            DiscoverButton.Content = "重新发现";
        }
        catch (Exception error) { DiscoveryStatus.Text = "发现摘要读取失败：" + error.Message; }
    }
    private async void OpenModule(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is ProjectModule project) await OpenProject(project);
    }
    private async Task OpenProject(ProjectModule project)
    {
        if (workspace.Stopping) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); pendingReads.Add(completion.Task);
        var request = ++generation; selected = project; opening = true;
        try
        {
        ProjectTitle.Text = project.Name; ProjectPath.Text = project.Root; ProjectPath.ToolTip = project.Root;
        ProjectStatus.Text = project.Status + " · " + project.SessionLabel +
            (project.RegistrationId is null ? project.HasMonitoredDescendants ? "。只有已登记子目录记录文件历史；整个项目支持只读盘点与版本比较。" : "。尚未建立文件历史，可到管理范围登记。" : "。下方页面仅展示当前项目；用途与空间请进入统一工作台。");
        if (project.RelatedScopes.Length > 0) ProjectStatus.Text += "\n已确认的位置关联（不代表独占或写入者）：" +
            string.Join("；", project.RelatedScopes.Take(4).Select(s => $"{s.Name} · {s.Status}")) +
            (project.RelatedScopes.Length > 4 ? "；完整清单见范围管理。" : "。");
        RegisterButton.IsEnabled = true;
        ModulesPanel.Visibility = Visibility.Collapsed; DetailPanel.Visibility = Visibility.Visible;
        sessions.ShowProject(project); comparison.ShowProject(project);
        if (project.HasMonitoredDescendants) ProjectStatus.Text += " 此目录下已有单独登记范围，请打开对应项目查看历史；不重复登记整个父目录。";
        await Task.WhenAll(timeline.ShowProject(project), RefreshSummary(project));
        }
        finally
        {
            if (!workspace.Stopping && request == generation) { opening = false; Sections.SelectedIndex = preferredSection; }
            pendingReads.Remove(completion.Task); completion.TrySetResult();
        }
    }
    private void Back(object sender, RoutedEventArgs e) { if (workspace.Stopping) return; DetailPanel.Visibility = Visibility.Collapsed; ModulesPanel.Visibility = Visibility.Visible; RefreshModules(); }
    private void SectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!opening && ReferenceEquals(e.Source, Sections)) preferredSection = Sections.SelectedIndex; }
    public void CancelPendingOperations()
    {
        generation++; summaryRevision++;
        discovering?.Cancel(); comparison.CancelPendingOperations(); timeline.CancelPendingOperations();
    }
    public async Task WaitForPendingOperationsAsync()
    {
        while (true)
        {
            var pending = pendingReads.Append(pendingDiscovery?.Task ?? Task.CompletedTask)
                .Append(timeline.WaitForPendingOperationsAsync()).Append(comparison.WaitForPendingOperationsAsync())
                .Where(task => !task.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }
    private void OpenSpace(object sender, RoutedEventArgs e) { if (!workspace.Stopping && selected is not null) SpaceRequested?.Invoke(selected.Root); }
    private void CopyPath(object sender, RoutedEventArgs e)
    {
        try { if (selected is not null) Clipboard.SetText(selected.Root); }
        catch (Exception error) { ProjectStatus.Text = "路径复制失败：" + error.Message; }
    }
    private void Register(object sender, RoutedEventArgs e) { if (!workspace.Stopping && selected is not null) ManageScopeRequested?.Invoke(selected.Root); }
    private async Task RefreshSummary(ProjectModule project)
    {
        if (workspace.Stopping) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); pendingReads.Add(completion.Task);
        try { await ReadSummary(project); }
        finally { pendingReads.Remove(completion.Task); completion.TrySetResult(); }
    }
    private async Task ReadSummary(ProjectModule project)
    {
        if (workspace.Stopping) return;
        var request = ++summaryRevision;
        if (project.RegistrationId is null)
        {
            ProjectSummary.Text = $"文件索引：{(project.HasMonitoredDescendants ? "仅子目录已登记" : "未建立")}  ·  当前占用：请在空间与清理只读盘点\n当前清理资格未核验；版本相似不代表可以删除。";
            return;
        }
        try
        {
            var observationKey = workspace.CurrentObservationKey(project.RegistrationId.Value) ?? "removed";
            var totals = await Task.Run(() => workspace.Store.ProjectTotals(observationKey, project.Root));
            if (workspace.Stopping || selected?.Root != project.Root || request != summaryRevision) return;
            var monitor = workspace.Monitors.GetValueOrDefault(project.RegistrationId.Value);
            var coverage = project.Paused ? "监测暂停，索引可能过期" : monitor is null || monitor.Revision == 0 ? "正在建立基线或范围不可访问" :
                monitor.Snapshot.Gaps.Count > 0 ? "部分覆盖，存在扫描缺口" : !monitor.HasContinuousObservation ?
                "已有索引，实时观察不完整；清理保持保护" : "已取得登记范围当前索引";
            ProjectSummary.Text = $"已索引 {totals.Files:N0} 文件  ·  逻辑 {IndexedFile.FormatBytes(totals.Logical)}  ·  分配估算 {IndexedFile.FormatBytes(totals.Allocated)}  ·  现存文件增长 {IndexedFile.FormatBytes(totals.Growth)}" +
                $"\n{coverage}；未知对象 {totals.Unknown:N0} 项。清理资格需独立核验，版本比较只读。";
        }
        catch (Exception error) { if (!workspace.Stopping && request == summaryRevision) ProjectSummary.Text = "项目概览暂不可用：" + error.Message; }
    }
    private void ChooseDiscoveryRoot(object sender, RoutedEventArgs e)
    {
        if (workspace.Stopping || discovering is not null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择存放项目的父目录" };
        if (Directory.Exists(DiscoveryRoot.Text)) dialog.InitialDirectory = DiscoveryRoot.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) DiscoveryRoot.Text = dialog.FolderName;
    }
    private void StopDiscovery(object sender, RoutedEventArgs e) => discovering?.Cancel();
    private async void DiscoverProjects(object sender, RoutedEventArgs e)
    {
        if (workspace.Stopping || discovering is not null) return;
        discovering = new(); var token = discovering.Token;
        var completion = pendingDiscovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DiscoverButton.IsEnabled = DiscoveryRoot.IsEnabled = false;
        DiscoveryStatus.Text = "正在只读发现项目，可停止后继续。";
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DiscoveryRoot.Text));
            var registration = workspace.Projects.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
            var scopeRevision = registration?.ScopeRevision ?? 1;
            var result = await Task.Run(() =>
            {
                if (discovery is null || discoveryFinished || !discovery.Root.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    discoveryRegistrationId != registration?.Id || discoveryScopeRevision != scopeRevision)
                {
                    discovery?.Dispose(); discovery = new(root); discoveryFinished = false;
                    discoveryRegistrationId = registration?.Id; discoveryScopeRevision = scopeRevision;
                }
                return discovery.Next(token);
            }, token);
            token.ThrowIfCancellationRequested();
            if (workspace.Stopping) return;
            var beforePublish = workspace.Projects.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
            if (beforePublish?.Id != registration?.Id || beforePublish?.ScopeRevision != registration?.ScopeRevision)
            {
                discoveryFinished = true;
                DiscoveryStatus.Text = "发现期间登记范围已修订或移除，旧结果未写入当前项目列表；请重新发现。";
                return;
            }
            discoveryFinished = !result.HasMore;
            discoveredRoots.Clear();
            foreach (var found in result.Roots) discoveredRoots.Add(found);
            DiscoveryStatus.Text = $"{root}（发现范围 v{scopeRevision}）：找到 {result.Roots.Count} 个项目，已检查 {result.Inspected:N0} 项，跳过 {result.Skipped:N0} 项。" +
                (result.Limited ? "达到上限，请选择更小范围。" : result.HasMore ? "部分结果，点击“继续发现”扫描剩余范围。" : "本轮发现结束。候选已加入项目卡片；下次点击将重新扫描本范围。");
            RefreshModules();
            if (workspace.Stopping) return;
            if (!await Task.Run(() => workspace.Store.SaveDiscoverySummary(root, scopeRevision, result)))
                DiscoveryStatus.Text += " 本次候选已显示，但摘要保存失败，重启不会沿用。";
            token.ThrowIfCancellationRequested();
            if (workspace.Stopping) return;
            var current = workspace.Projects.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
            if (current?.Id != registration?.Id || current?.ScopeRevision != registration?.ScopeRevision)
            {
                discoveryFinished = true;
                DiscoveryStatus.Text += " 发现期间登记范围已调整/移除；以上仅为旧范围摘要，下次重新发现。";
            }
            preferences.DiscoveryRoot = root; preferences.Save(workspace.DataDirectory);
        }
        catch (OperationCanceledException) { if (!workspace.Stopping) DiscoveryStatus.Text = "已停止，保留本轮已展示候选；可以继续发现。"; }
        catch (Exception error) { if (!workspace.Stopping) DiscoveryStatus.Text = "项目发现失败：" + error.Message; }
        finally
        {
            discovering.Dispose(); discovering = null; DiscoverButton.IsEnabled = DiscoveryRoot.IsEnabled = true;
            DiscoverButton.Content = discovery is null ? "开始发现" : discoveryFinished ? "重新发现" : "继续发现";
            completion.TrySetResult();
        }
    }

}
