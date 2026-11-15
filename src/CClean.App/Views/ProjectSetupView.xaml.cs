using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Monitoring;
using CClean.Core.Projects;
using CClean.Core.Tools;

namespace CClean.App.Views;

public partial class ProjectSetupView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    private SpaceSurvey? discovery;
    private string? discoveryRoot;
    private Guid? discoveryRegistrationId;
    private long discoveryScopeRevision = 1;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource? pendingDiscovery;
    private string inventoryStatus = "尚未读取盘点摘要";
    private int discoveryGeneration;
    private readonly Dictionary<string, DateTimeOffset> lastCaptured = [];
    private readonly Dictionary<string, string> storedIndexTime = [];
    public event Action<string>? SpaceRequested;
    public ProjectSetupView(WorkspaceViewModel workspace)
    {
        InitializeComponent();
        this.workspace = workspace;
        Tool.ItemsSource = KnownTools.RegistrationLabels;
        Tool.SelectedIndex = 0;
        Tool.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(ToolChanged));
        ShowToolCapabilities();
        Registered.ItemsSource = workspace.Projects;
        RelatedRoot.ItemsSource = workspace.Projects;
        workspace.Changed += ShowScopeDetails;
        Unloaded += (_, _) =>
        {
            cancellation?.Cancel();
            if (cancellation is null) { discovery?.Dispose(); discovery = null; }
        };
    }
    public void CancelPendingOperations() => cancellation?.Cancel();
    public Task WaitForPendingOperationsAsync() => pendingDiscovery?.Task ?? Task.CompletedTask;
    public void ShowScope(string? root)
    {
        if (workspace.Stopping) return;
        if (string.IsNullOrWhiteSpace(root)) { ShowScopeDetails(); return; }
        discoveryGeneration++; cancellation?.Cancel();
        var registered = workspace.Projects.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
        Registered.SelectedItem = registered;
        if (registered is not null) { Registered.ScrollIntoView(registered); SelectRegistered(this, new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>())); }
        else
        {
            RootPath.Text = root; ProjectName.Text = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(root));
            Tool.Text = "通用工具";
            ScopeDetails.Text = $"未登记范围：{root}\n仅供查看和本次盘点；确认工具与路径后才登记观察。自定义范围不证明工具识别完整。";
            Feedback.Text = "正在准备新范围；请核对名称、完整路径和工具，再确认登记。";
        }
    }
    private void ShowScopeDetails()
    {
        if (workspace.Stopping) return;
        if (Registered.SelectedItem is not RegisteredProject selected) return;
        var project = workspace.Projects.FirstOrDefault(p => p.Id == selected.Id);
        if (project is null) { ScopeDetails.Text = "该监测范围已移除；原文件、历史和显式保护保留，不再计入当前汇总。"; return; }
        var monitor = workspace.Monitors.GetValueOrDefault(project.Id);
        var state = project.Paused ? "观察暂停，最后索引可能过期" : monitor is null ? "观察未建立或不可访问" : monitor.Revision == 0 ? "正在建立基线" :
            monitor.Snapshot.Gaps.Count > 0 || !monitor.HasContinuousObservation ? "部分观察，存在缺口；活动未知，清理保护" : "登记范围观察正常";
        if (monitor is { Revision: > 0 }) lastCaptured[project.ObservationKey] = monitor.Snapshot.CapturedAt;
        var last = lastCaptured.TryGetValue(project.ObservationKey, out var captured) ?
            $"{captured.ToLocalTime():yyyy-MM-dd HH:mm:ss}（本次运行最后捕获；暂停后可能过期）" :
            storedIndexTime.GetValueOrDefault(project.ObservationKey, "当前观察未建立；已有索引时间尚未读取，不代表没有历史");
        var linkedBy = workspace.Projects.Where(p => p.RelatedScopes.Any(s => s.ScopeId == project.Id && s.ScopeRevision == project.ScopeRevision)).Select(p => p.Name).ToArray();
        ScopeDetails.Text = $"{project.Root}\n卷：{System.IO.Path.GetPathRoot(project.Root)}；工具：{project.Tool}；配置修订 {project.Revision} / 范围 v{project.ScopeRevision}。\n" +
            $"依据：用户确认登记的物理位置；工具标签不证明来源、写入者、独占或删除资格。\n观察：{state}；最后有效索引：{last}。\n" +
            $"盘点：{inventoryStatus}。\n" +
            $"关联项目：{(linkedBy.Length == 0 ? "未确认共享关联" : string.Join("、", linkedBy))}；具体关联路径与依据见下方关联列表。";
    }
    private void OpenSpace(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(RootPath.Text)) SpaceRequested?.Invoke(RootPath.Text);
        else Feedback.Text = "先选择已登记范围或输入目录路径。";
    }
    private void CopyPath(object sender, RoutedEventArgs e)
    {
        try { if (!string.IsNullOrWhiteSpace(RootPath.Text)) Clipboard.SetText(RootPath.Text); }
        catch (Exception error) { Feedback.Text = "路径复制失败：" + error.Message; }
    }
    private async void Discover(object sender, RoutedEventArgs e)
    {
        if (workspace.Stopping || cancellation is not null) return;
        cancellation = new();
        var request = ++discoveryGeneration;
        var completion = pendingDiscovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DiscoverButton.IsEnabled = false;
        try
        {
            var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(RootPath.Text));
            var registration = workspace.Projects.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
            var scopeRevision = registration?.ScopeRevision ?? 1;
            var result = await Task.Run(() =>
            {
                if (discovery is null || !root.Equals(discoveryRoot, StringComparison.OrdinalIgnoreCase) ||
                    discoveryRegistrationId != registration?.Id || discoveryScopeRevision != scopeRevision)
                {
                    discovery?.Dispose(); discovery = new(root); discoveryRoot = root;
                    discoveryRegistrationId = registration?.Id; discoveryScopeRevision = scopeRevision;
                }
                return discovery.Next(cancellation.Token);
            });
            cancellation.Token.ThrowIfCancellationRequested();
            var current = workspace.Projects.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
            if (!root.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(RootPath.Text)), StringComparison.OrdinalIgnoreCase) ||
                current?.Id != registration?.Id || current?.ScopeRevision != registration?.ScopeRevision)
            {
                discovery?.Dispose(); discovery = null;
                Feedback.Text = "发现期间路径或登记范围已变化，旧结果未写入当前列表；请重新发现。";
                return;
            }
            Candidates.ItemsSource = result.Folders.Where(f => f.Project).OrderByDescending(f => f.Bytes).Take(200)
                .Select(f => new DiscoveryCandidate(f.Path, f.Bytes, f.Files, 0)).ToArray();
            Feedback.Text = $"{root}（范围 v{scopeRevision}）：已盘点 {result.Files} 个文件；{(result.HasMore ? "仍有未扫描范围，可继续" : "本轮扫描结束")}；跳过 {result.Skipped} 项。系统范围不列出，最多显示 200 个项目。";
        }
        catch (OperationCanceledException) { if (request == discoveryGeneration) Feedback.Text = "已取消；可以继续扫描剩余范围。"; }
        catch (Exception error) { if (request == discoveryGeneration) Feedback.Text = "发现失败：" + error.Message; }
        finally
        {
            cancellation.Dispose(); cancellation = null; DiscoverButton.IsEnabled = true;
            if (!IsLoaded) { discovery?.Dispose(); discovery = null; }
            completion.TrySetResult();
        }
    }
    private void Cancel(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private void Browse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择项目目录" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) { RootPath.Text = dialog.FolderName; ProjectName.Text = System.IO.Path.GetFileName(dialog.FolderName); }
    }
    private async void Register(object sender, RoutedEventArgs e)
    {
        RegisterButton.IsEnabled = false;
        try
        {
            await workspace.Register(ProjectName.Text, RootPath.Text, Tool.Text);
            Feedback.Text = "登记成功。正在建立基线；不会自动授权清理。";
        }
        catch (Exception error) { Feedback.Text = "登记失败：" + error.Message; }
        finally { RegisterButton.IsEnabled = true; }
    }
    private async void Pause(object sender, RoutedEventArgs e)
    {
        if (Registered.SelectedItem is not RegisteredProject project) { Feedback.Text = "请先选择一个已登记项目。"; return; }
        var button = (Button)sender;
        button.IsEnabled = false;
        try { await workspace.TogglePause(project); Feedback.Text = workspace.Status; }
        catch (Exception error) { Feedback.Text = error.Message; }
        finally { button.IsEnabled = true; }
    }
    private void SelectCandidate(object sender, SelectionChangedEventArgs e)
    {
        if (Candidates.SelectedItem is DiscoveryCandidate candidate) { RootPath.Text = candidate.Path; ProjectName.Text = System.IO.Path.GetFileName(candidate.Path); }
    }
    private void SelectRegistered(object sender, SelectionChangedEventArgs e)
    {
        if (workspace.Stopping) return;
        if (Registered.SelectedItem is not RegisteredProject project)
        {
            RelatedScopes.ItemsSource = null;
            ScopeDetails.Text = "尚未选择已登记范围。已移除范围只保留历史与显式保护，不再计入当前汇总。";
            return;
        }
        ProjectName.Text = project.Name; RootPath.Text = project.Root; Tool.Text = project.Tool;
        try
        {
            var summary = workspace.Store.LoadInventorySummary(project.Root, project.ScopeRevision);
            inventoryStatus = summary is null ? "尚无保存摘要" : $"读取时的历史摘要 {summary.CapturedAt.ToLocalTime():MM-dd HH:mm:ss}，需刷新；不代表当前观察";
            var change = workspace.Store.Changes(DateTimeOffset.MinValue, 1, project.ObservationKey, project.Root).FirstOrDefault();
            storedIndexTime[project.ObservationKey] = change is null ?
                "已有索引可能保留；当前历史窗口无法确认最后时间，未补造时间" :
                $"{change.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}（已保留索引事件时间；最后完整扫描时间未持久化）";
        }
        catch (Exception error) { inventoryStatus = "摘要读取失败：" + error.Message; }
        RelatedScopes.ItemsSource = project.RelatedScopes;
        RelatedStatus.Text = string.Join("\n", project.RelatedScopes.Select(reference =>
        {
            var target = workspace.Projects.FirstOrDefault(p => p.Id == reference.ScopeId);
            var current = target is not null && target.ScopeRevision == reference.ScopeRevision;
            return $"{reference.Path}：{(current ? "已确认关联" : "原根已调整/移除，关联待重新确认")}；依据：{reference.Evidence}";
        }));
        Feedback.Text = $"正在编辑 {project.Name}，配置修订 {project.Revision} / 范围 v{project.ScopeRevision}；调整路径会保留旧历史并重新建立观察。";
        ShowScopeDetails();
    }
    private async Task ChangeSelected(Button button, Func<RegisteredProject, Task> change)
    {
        if (Registered.SelectedItem is not RegisteredProject project) { Feedback.Text = "请先选择已登记范围。"; return; }
        button.IsEnabled = false;
        try
        {
            await change(project);
            Registered.SelectedItem = workspace.Projects.FirstOrDefault(p => p.Id == project.Id);
            Feedback.Text = workspace.Status;
        }
        catch (Exception error) { Feedback.Text = "范围操作未完成：" + error.Message; }
        finally { button.IsEnabled = true; }
    }
    private async void SaveRegistration(object sender, RoutedEventArgs e)
    {
        var name = ProjectName.Text; var root = RootPath.Text; var tool = Tool.Text;
        await ChangeSelected((Button)sender, p => workspace.UpdateRegistration(p, name, root, tool));
    }
    private async void RemoveRegistration(object sender, RoutedEventArgs e)
    {
        if (Registered.SelectedItem is not RegisteredProject project) { Feedback.Text = "请先选择已登记范围。"; return; }
        if (MessageBox.Show(Window.GetWindow(this), $"停止监测 {project.Root}（范围 v{project.ScopeRevision}）。\n不会删除文件；已有历史与显式保护保留，相关预览失效。",
            "移除监测", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await ChangeSelected((Button)sender, p => workspace.RemoveRegistration(p));
    }
    private void RelatedRootChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RelatedRoot.SelectedItem is RegisteredProject scope) RelatedPath.Text = scope.Root;
    }
    private async void LinkScope(object sender, RoutedEventArgs e)
    {
        if (RelatedRoot.SelectedItem is not RegisteredProject scope) { Feedback.Text = "请选择已登记的关联数据根。"; return; }
        var path = RelatedPath.Text; var evidence = RelatedEvidence.Text;
        await ChangeSelected((Button)sender, p => workspace.LinkScope(p, scope, path, evidence));
    }
    private async void UnlinkScope(object sender, RoutedEventArgs e)
    {
        if (RelatedScopes.SelectedItem is not ScopeReference reference) { Feedback.Text = "请选择要移除的关联；此操作保留数据根的观察。"; return; }
        await ChangeSelected((Button)sender, p => workspace.UnlinkScope(p, reference.ScopeId, reference.Path));
    }
    private void ToolChanged(object sender, TextChangedEventArgs e) => ShowToolCapabilities();
    private void ShowToolCapabilities()
    {
        try { ToolCapabilities.Text = KnownTools.Describe(Tool.Text).Summary; }
        catch (ArgumentException error) { ToolCapabilities.Text = error.Message; }
    }
}
