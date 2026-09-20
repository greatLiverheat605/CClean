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
    private readonly FilesView files;
    private readonly TimelineView timeline;
    private readonly SessionsView sessions;
    private readonly ProjectSpaceView space;
    private readonly ProjectComparisonView comparison = new();
    private readonly AppPreferences preferences;
    private readonly HashSet<string> discoveredRoots = new(StringComparer.OrdinalIgnoreCase);
    private ProjectDiscovery? discovery;
    private bool discoveryFinished;
    private CancellationTokenSource? discovering;
    private int summaryRevision;
    private string cleanupSummary = "尚未复核";
    private ProjectModule? selected;
    private int preferredSection;
    private bool opening;
    private int generation;
    public event Action<string[]>? PreviewRequested;
    public ProjectWorkbenchView(WorkspaceViewModel workspace, AppPreferences preferences)
    {
        InitializeComponent(); this.workspace = workspace; this.preferences = preferences;
        DiscoveryRoot.Text = preferences.DiscoveryRoot; ComparisonContent.Content = comparison;
        files = new(workspace); timeline = new(workspace); sessions = new(workspace); space = new(workspace);
        FilesContent.Content = files; TimelineContent.Content = timeline; SessionsContent.Content = sessions; SpaceContent.Content = space;
        files.PreviewRequested += paths => PreviewRequested?.Invoke(paths);
        space.PreviewRequested += paths => PreviewRequested?.Invoke(paths);
        space.ReviewChanged += async summary => { cleanupSummary = summary; if (selected is not null) await RefreshSummary(selected); };
        timeline.CurrentDirectoryRequested += async directory => { Sections.SelectedIndex = 1; await files.ShowDirectory(directory); };
        workspace.SessionsDiscovered += RefreshModules;
        workspace.Projects.CollectionChanged += (_, _) => RefreshModules();
        Loaded += (_, _) => RefreshModules();
        Unloaded += (_, _) => discovering?.Cancel();
        workspace.Indexed += async () => { if (IsVisible && selected is not null) await RefreshSummary(selected); };
    }
    public void ShowSection(int section)
    {
        preferredSection = section;
        if (selected is not null) { ModulesPanel.Visibility = Visibility.Collapsed; DetailPanel.Visibility = Visibility.Visible; Sections.SelectedIndex = section; }
        else Back(this, new());
    }
    public void ShowModules() => Back(this, new());
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (workspace is not null) RefreshModules(); }
    private async void Refresh(object sender, RoutedEventArgs e) { await workspace.DiscoverSessions(); RefreshModules(); }
    private void RefreshModules()
    {
        var query = Search.Text.Trim();
        var all = ProjectCatalog.Build(workspace.Projects, workspace.DiscoveredSessions, discoveredRoots);
        var matches = all.Where(p => (p.Name + " " + p.Root).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        Modules.ItemsSource = matches;
        var current = all.FirstOrDefault(p => p.Root.Equals(selected?.Root, StringComparison.OrdinalIgnoreCase));
        if (current is not null && selected is not null &&
            (current.RegistrationId != selected.RegistrationId || current.Paused != selected.Paused || current.HasMonitoredDescendants != selected.HasMonitoredDescendants || current.Kind != selected.Kind)) _ = OpenProject(current);
        ModuleStatus.Text = $"{all.Count} 个项目 / 工具工作目录 · {all.Count(p => p.RegistrationId.HasValue)} 个已纳入监测范围。" +
            (all.Count == 0 ? "正在自动发现，也可到“项目与设置”登记目录。" : matches.Length == 0 ? "没有匹配项目，请调整搜索。" : "点击项目模块进入详情；名称相同的项目按路径区分。");
    }
    private async void OpenModule(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is ProjectModule project) await OpenProject(project);
    }
    private async Task OpenProject(ProjectModule project)
    {
        var request = ++generation; selected = project; opening = true;
        ProjectTitle.Text = project.Name; ProjectPath.Text = project.Root; ProjectPath.ToolTip = project.Root;
        ProjectStatus.Text = project.Status + " · " + project.SessionLabel +
            (project.RegistrationId is null ? project.HasMonitoredDescendants ? "。只有已登记子目录记录文件历史；整个项目支持只读盘点与版本比较。" : "。尚未建立文件历史，可加入监测；清理位置支持只读盘点。" : "。下方页面仅展示当前项目，清理需逐文件复核。");
        RegisterButton.IsEnabled = project.RegistrationId is null && !project.HasMonitoredDescendants;
        ModulesPanel.Visibility = Visibility.Collapsed; DetailPanel.Visibility = Visibility.Visible;
        space.ShowProject(project); sessions.ShowProject(project); comparison.ShowProject(project);
        if (project.HasMonitoredDescendants) ProjectStatus.Text += " 此目录下已有单独登记范围，请打开对应项目查看历史；不重复登记整个父目录。";
        try { await Task.WhenAll(timeline.ShowProject(project), files.ShowProject(project), RefreshSummary(project)); }
        finally { if (request == generation) { opening = false; Sections.SelectedIndex = preferredSection; } }
    }
    private void Back(object sender, RoutedEventArgs e) { DetailPanel.Visibility = Visibility.Collapsed; ModulesPanel.Visibility = Visibility.Visible; RefreshModules(); }
    private void SectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!opening && ReferenceEquals(e.Source, Sections)) preferredSection = Sections.SelectedIndex; }
    private async void Register(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        var project = selected; RegisterButton.IsEnabled = false;
        try
        {
            await workspace.Register(project.Name, project.Root, project.Tool);
            if (selected == project) await OpenProject(ProjectCatalog.Build(workspace.Projects, workspace.DiscoveredSessions, discoveredRoots).Single(p => p.Root.Equals(project.Root, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception error) { if (selected == project) { ProjectStatus.Text = "未登记：" + error.Message; RegisterButton.IsEnabled = true; } }
    }
    private async Task RefreshSummary(ProjectModule project)
    {
        var request = ++summaryRevision;
        if (project.RegistrationId is null)
        {
            ProjectSummary.Text = $"文件索引：{(project.HasMonitoredDescendants ? "仅子目录已登记" : "未建立")}  ·  当前占用：请在清理位置盘点\n清理候选：{cleanupSummary}";
            return;
        }
        try
        {
            var totals = await Task.Run(() => workspace.Store.ProjectTotals(project.RegistrationId.Value.ToString(), project.Root));
            if (selected?.Root != project.Root || request != summaryRevision) return;
            var monitor = workspace.Monitors.GetValueOrDefault(project.RegistrationId.Value);
            var coverage = project.Paused ? "监测暂停，索引可能过期" : monitor is null || monitor.Revision == 0 ? "正在建立基线或范围不可访问" :
                monitor.Snapshot.Gaps.Count > 0 ? "部分覆盖，存在扫描缺口" : !monitor.HasContinuousObservation ?
                "已有索引，实时观察不完整；清理保持保护" : "已取得登记范围当前索引";
            ProjectSummary.Text = $"已索引 {totals.Files:N0} 文件  ·  逻辑 {IndexedFile.FormatBytes(totals.Logical)}  ·  分配估算 {IndexedFile.FormatBytes(totals.Allocated)}  ·  现存文件增长 {IndexedFile.FormatBytes(totals.Growth)}" +
                $"\n{coverage}；未知对象 {totals.Unknown:N0} 项。清理候选：{cleanupSummary}。";
        }
        catch (Exception error) { if (request == summaryRevision) ProjectSummary.Text = "项目概览暂不可用：" + error.Message; }
    }
    private void ChooseDiscoveryRoot(object sender, RoutedEventArgs e)
    {
        if (discovering is not null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择存放项目的父目录" };
        if (Directory.Exists(DiscoveryRoot.Text)) dialog.InitialDirectory = DiscoveryRoot.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) DiscoveryRoot.Text = dialog.FolderName;
    }
    private void StopDiscovery(object sender, RoutedEventArgs e) => discovering?.Cancel();
    private async void DiscoverProjects(object sender, RoutedEventArgs e)
    {
        if (discovering is not null) return;
        discovering = new(); var token = discovering.Token;
        DiscoverButton.IsEnabled = DiscoveryRoot.IsEnabled = false;
        DiscoveryStatus.Text = "正在只读发现项目，可停止后继续。";
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DiscoveryRoot.Text));
            var result = await Task.Run(() =>
            {
                if (discovery is null || discoveryFinished || !discovery.Root.Equals(root, StringComparison.OrdinalIgnoreCase))
                { discovery?.Dispose(); discovery = new(root); discoveryFinished = false; }
                return discovery.Next(token);
            }, token);
            token.ThrowIfCancellationRequested();
            discoveryFinished = !result.HasMore;
            foreach (var found in result.Roots) discoveredRoots.Add(found);
            DiscoveryStatus.Text = $"本范围找到 {result.Roots.Count} 个项目，已检查 {result.Inspected:N0} 项，跳过 {result.Skipped:N0} 项。" +
                (result.Limited ? "达到上限，请选择更小范围。" : result.HasMore ? "部分结果，点击“继续发现”扫描剩余范围。" : "本轮发现结束。候选已加入项目卡片；下次点击将重新扫描本范围。");
            preferences.DiscoveryRoot = root; preferences.Save(workspace.DataDirectory); RefreshModules();
        }
        catch (OperationCanceledException) { DiscoveryStatus.Text = "已停止，保留本轮已展示候选；可以继续发现。"; }
        catch (Exception error) { DiscoveryStatus.Text = "项目发现失败：" + error.Message; }
        finally
        {
            discovering.Dispose(); discovering = null; DiscoverButton.IsEnabled = DiscoveryRoot.IsEnabled = true;
            DiscoverButton.Content = discovery is null ? "开始发现" : discoveryFinished ? "重新发现" : "继续发现";
        }
    }

}
