using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Storage;
using CClean.Core.Cleanup;

namespace CClean.App.Views;

public partial class FilesView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    private int page;
    private Core.Projects.ProjectModule? scope;
    public async Task ShowProject(Core.Projects.ProjectModule project)
    {
        scope = project; revision++; page = 0; SearchPath.Text = ""; Files.ItemsSource = null;
        ProjectFilter.ItemsSource = new[] { new ProjectChoice(project.RegistrationId?.ToString() ?? "unregistered", project.Name) };
        ProjectFilter.DisplayMemberPath = "Name"; ProjectFilter.SelectedIndex = 0; ProjectFilter.IsEnabled = false;
        await Update();
    }
    public event Action<string[]>? PreviewRequested;
    public FilesView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace;
        Loaded += async (_, _) =>
        {
            if (scope is not null) { await Update(); return; }
            var selectedId = (ProjectFilter.SelectedItem as ProjectChoice)?.Id;
            ProjectFilter.ItemsSource = new[] { new ProjectChoice(null, "全部项目") }.Concat(workspace.Projects.Select(p => new ProjectChoice(p.Id.ToString(), p.Name))).ToArray();
            ProjectFilter.DisplayMemberPath = "Name";
            ProjectFilter.SelectedItem = ProjectFilter.Items.Cast<ProjectChoice>().FirstOrDefault(p => p.Id == selectedId) ?? ProjectFilter.Items[0];
            await Update();
        };
        workspace.Indexed += async () => { if (IsVisible) await Update(); };
    }
    private async Task Update()
    {
        var search = SearchPath.Text;
        var project = scope is null ? (ProjectFilter.SelectedItem as ProjectChoice)?.Id : scope.RegistrationId?.ToString() ?? "unregistered";
        var sort = Sort.SelectedIndex switch { 1 => "growth", 2 => "modified", _ => "allocated" };
        bool? special = Classification.SelectedIndex switch { 1 => false, 2 => true, _ => null };
        var activity = workspace.Activity(); var rules = workspace.Rules.Scopes; var currentPage = page;
        var root = scope?.Root;
        var request = ++revision;
        try
        {
            var items = await Task.Run(() => workspace.Store.QueryFiles(search, currentPage, project, sort, special, root)
                .Select(f => f with { DisplayPath = root is null ? f.Path : System.IO.Path.GetRelativePath(root, f.Path), Exclusion = f.Snapshot is null ? "缺少对象元数据" : Describe(ProtectionPolicy.Classify(f.Snapshot, rules, activity, [workspace.DataDirectory], DateTimeOffset.UtcNow)) }).ToArray());
            if (request != revision) return;
            var visible = items.Where(f => f.Snapshot?.Attributes.HasFlag(System.IO.FileAttributes.System) != true &&
                !Core.Monitoring.SpaceSurvey.IsSystemPath(f.Path)).ToArray();
            Files.ItemsSource = visible;
            PreviousButton.IsEnabled = currentPage > 0; NextButton.IsEnabled = items.Length == 200;
            Feedback.Text = scope is { RegistrationId: null } ? "此项目尚未登记监测，没有文件索引。可在上方加入监测；当前目录大小可在“清理位置”只读盘点。" : items.Length == 0 ? "此项目暂无匹配文件。请等待基线完成，或调整筛选。" : $"第 {currentPage + 1} 页，显示 {visible.Length} 项，隐藏系统对象 {items.Length - visible.Length} 项；分类基于索引，清理前仍须重新读取。";
        }
        catch (Exception error) { Feedback.Text = "索引查询失败：" + error.Message; }
    }
    private int revision;
    private async void FilterChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) { page = 0; await Update(); } }
    private static string Describe(ProtectionDecision decision) => decision.Category + "：" + decision.Reason;
    private async void Search(object sender, RoutedEventArgs e) { page = 0; await Update(); }
    private async void Previous(object sender, RoutedEventArgs e) { page = Math.Max(0, page - 1); await Update(); }
    private async void Next(object sender, RoutedEventArgs e) { page++; await Update(); }
    private void Selected(object sender, SelectionChangedEventArgs e)
    {
        FullPath.Text = (Files.SelectedItem as IndexedFile)?.Path ?? "";
        CopyButton.IsEnabled = FullPath.Text.Length > 0; PreviewButton.IsEnabled = Files.SelectedItems.Count > 0;
    }
    private void Copy(object sender, RoutedEventArgs e) { if (FullPath.Text.Length > 0) Clipboard.SetText(FullPath.Text); }
    private void Preview(object sender, RoutedEventArgs e) { if (Files.SelectedItems.Count > 0) PreviewRequested?.Invoke(Files.SelectedItems.Cast<IndexedFile>().Select(f => f.Path).ToArray()); }
    public async Task ShowDirectory(string directory) { SearchPath.Text = directory; page = 0; await Update(); }
    private sealed record ProjectChoice(string? Id, string Name);
}
