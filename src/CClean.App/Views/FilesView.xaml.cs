using System.IO;
using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Storage;
using CClean.Core.Cleanup;
using CClean.Core.Monitoring;

namespace CClean.App.Views;

public partial class FilesView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    private readonly bool readOnly;
    private readonly HashSet<CancellationTokenSource> querying = [];
    private readonly HashSet<Task> pendingReads = [];
    private int page, revision;
    private string? directoryRoot;
    private Core.Projects.ProjectModule? scope;
    public event Action<string[]>? PreviewRequested;
    public FilesView(WorkspaceViewModel workspace, bool readOnly = false)
    {
        InitializeComponent(); this.workspace = workspace; this.readOnly = readOnly;
        PreviewButton.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) =>
        {
            if (workspace.Stopping) return;
            if (scope is not null || directoryRoot is not null) { await Update(); return; }
            var selectedId = (ProjectFilter.SelectedItem as ProjectChoice)?.Id;
            ProjectFilter.ItemsSource = new[] { new ProjectChoice(null, "全部项目") }.Concat(workspace.Projects.Select(p => new ProjectChoice(p.ObservationKey, p.Name))).ToArray();
            ProjectFilter.SelectedItem = ProjectFilter.Items.Cast<ProjectChoice>().FirstOrDefault(p => p.Id == selectedId) ?? ProjectFilter.Items[0];
            await Update();
        };
        Unloaded += (_, _) => CancelPendingOperations();
        workspace.Indexed += async () => { if (IsVisible && !workspace.Stopping) await Update(); };
        workspace.Projects.CollectionChanged += async (_, _) => { if (IsVisible && !workspace.Stopping) await Update(); };
    }
    public void CancelPendingOperations()
    {
        revision++;
        foreach (var cancellation in querying) cancellation.Cancel();
    }
    public async Task WaitForPendingOperationsAsync()
    {
        while (pendingReads.Count > 0) await Task.WhenAll(pendingReads.ToArray());
    }
    public async Task ShowProject(Core.Projects.ProjectModule project)
    {
        if (workspace.Stopping) return;
        CancelPendingOperations(); directoryRoot = null; scope = project; page = 0; SearchPath.Text = ""; Files.ItemsSource = null;
        RootDescription.Text = "当前项目：" + project.Root + "\n只读索引，可能过期；用途与当前清理资格分别核验。";
        ProjectFilter.ItemsSource = new[] { new ProjectChoice(project.ObservationKey ?? "unregistered", project.Name) };
        ProjectFilter.SelectedIndex = 0; ProjectFilter.IsEnabled = false;
        await Update();
    }
    public async Task ShowDirectory(string directory)
    {
        if (workspace.Stopping) return;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        CancelPendingOperations(); scope = null; directoryRoot = root; page = 0; SearchPath.Text = ""; Files.ItemsSource = null;
        RootDescription.Text = "当前目录：" + root + "\n只查看此目录边界内已登记的文件索引，不扫描未登记目录，不包含相似前缀的其他目录。";
        ProjectFilter.ItemsSource = new[] { new ProjectChoice(null, "当前目录内的已登记索引") };
        ProjectFilter.SelectedIndex = 0; ProjectFilter.IsEnabled = false;
        await Update();
    }
    private async Task Update()
    {
        if (workspace.Stopping) return;
        foreach (var older in querying) older.Cancel();
        using var cancellation = new CancellationTokenSource(); querying.Add(cancellation);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); pendingReads.Add(completion.Task);
        var request = ++revision;
        try
        {
            var search = SearchPath.Text;
            var project = directoryRoot is not null ? null : scope is null ? (ProjectFilter.SelectedItem as ProjectChoice)?.Id :
                scope.RegistrationId is Guid id ? workspace.CurrentObservationKey(id) ?? "removed" : "unregistered";
            var sort = Sort.SelectedIndex switch { 1 => "growth", 2 => "modified", _ => "allocated" };
            bool? special = Classification.SelectedIndex switch { 1 => false, 2 => true, _ => null };
            var activity = workspace.Activity(); var rules = workspace.Rules.Scopes; var currentPage = page;
            var root = directoryRoot ?? scope?.Root; var currentKeys = workspace.CurrentObservationKeys;
            var selectedPath = (Files.SelectedItem as IndexedFile)?.Path;
            Feedback.Text = "正在读取当前范围的有界索引；每页最多 200 项。";
            var items = await Task.Run(() => workspace.Store.QueryFiles(search, currentPage, project, sort, special, root, currentKeys)
                .Select(f => f with { DisplayPath = root is null ? f.Path : Path.GetRelativePath(root, f.Path),
                    Exclusion = f.Snapshot is null ? "缺少对象元数据" : Describe(ProtectionPolicy.Classify(f.Snapshot, rules, activity, [workspace.DataDirectory], DateTimeOffset.UtcNow)) }).ToArray(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (workspace.Stopping || request != revision || !currentKeys.SequenceEqual(workspace.CurrentObservationKeys)) return;
            var visible = items.Where(f => f.Snapshot?.Attributes.HasFlag(FileAttributes.System) != true && !SpaceSurvey.IsSystemPath(f.Path)).ToArray();
            Files.ItemsSource = visible;
            Files.SelectedItem = visible.FirstOrDefault(f => f.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            PreviousButton.IsEnabled = currentPage > 0; NextButton.IsEnabled = items.Length == 200;
            var monitored = root is null || workspace.Projects.Any(p => ProtectionPolicy.Contains(p.Root, root) || ProtectionPolicy.Contains(root, p.Root));
            Feedback.Text = !monitored || scope is { RegistrationId: null } ? "此范围没有已登记的文件索引。可返回工作台做只读盘点，或到管理范围确认监测；没有索引不代表目录为空。" :
                items.Length == 0 ? "当前范围无匹配索引记录；可能未完成基线、筛选不匹配或确实为空，请核对覆盖状态。" :
                $"第 {currentPage + 1} 页，显示 {visible.Length} 项，隐藏系统对象 {items.Length - visible.Length} 项。索引可能过期；查看和分类不授予删除资格。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (request == revision && !workspace.Stopping) Feedback.Text = "索引查询失败：" + error.Message; }
        finally
        {
            querying.Remove(cancellation); pendingReads.Remove(completion.Task); completion.TrySetResult();
        }
    }
    private async void FilterChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded && !workspace.Stopping) { page = 0; await Update(); } }
    private static string Describe(ProtectionDecision decision) => decision.Category + "：" + decision.Reason;
    private async void Search(object sender, RoutedEventArgs e) { page = 0; await Update(); }
    private async void Previous(object sender, RoutedEventArgs e) { page = Math.Max(0, page - 1); await Update(); }
    private async void Next(object sender, RoutedEventArgs e) { page++; await Update(); }
    private void Selected(object sender, SelectionChangedEventArgs e)
    {
        var selected = Files.SelectedItem as IndexedFile;
        FullPath.Text = selected?.Path ?? "";
        CopyButton.IsEnabled = FullPath.Text.Length > 0; PreviewButton.IsEnabled = !readOnly && Files.SelectedItems.Count > 0;
        FileExplanation.Text = selected is null ? "选中文件查看用途与影响。目录名称、索引大小和普通文件分类不证明可删除。" :
            ContentClassification.Describe(selected.Path).Summary + "\n索引提示：" + selected.Exclusion + "；当前文件状态须重新读取，未生成批准清单。";
    }
    private void Copy(object sender, RoutedEventArgs e)
    {
        try { if (FullPath.Text.Length > 0) Clipboard.SetText(FullPath.Text); }
        catch (Exception error) { Feedback.Text = "路径复制失败：" + error.Message; }
    }
    private void Preview(object sender, RoutedEventArgs e)
    {
        if (!readOnly && !workspace.Stopping && Files.SelectedItems.Count > 0)
            PreviewRequested?.Invoke(Files.SelectedItems.Cast<IndexedFile>().Select(f => f.Path).ToArray());
    }
    private sealed record ProjectChoice(string? Id, string Name);
}
