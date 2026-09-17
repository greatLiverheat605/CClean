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
    public event Action<string[]>? PreviewRequested;
    public FilesView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace;
        Loaded += async (_, _) =>
        {
            ProjectFilter.ItemsSource = new[] { new ProjectChoice(null, "全部项目") }.Concat(workspace.Projects.Select(p => new ProjectChoice(p.Id.ToString(), p.Name))).ToArray();
            ProjectFilter.DisplayMemberPath = "Name"; ProjectFilter.SelectedIndex = 0;
            await Update();
        };
        workspace.Indexed += async () => { if (IsVisible) await Update(); };
    }
    private async Task Update()
    {
        var search = SearchPath.Text;
        var project = (ProjectFilter.SelectedItem as ProjectChoice)?.Id;
        var sort = Sort.SelectedIndex switch { 1 => "growth", 2 => "modified", _ => "allocated" };
        bool? special = Classification.SelectedIndex switch { 1 => false, 2 => true, _ => null };
        var activity = workspace.Activity(); var rules = workspace.Rules.Scopes; var currentPage = page;
        var request = ++revision;
        try
        {
            var items = await Task.Run(() => workspace.Store.QueryFiles(search, currentPage, project, sort, special)
                .Select(f => f with { Exclusion = f.Snapshot is null ? "缺少对象元数据" : Describe(ProtectionPolicy.Classify(f.Snapshot, rules, activity, [workspace.DataDirectory], DateTimeOffset.UtcNow)) }).ToArray());
            if (request != revision) return;
            Files.ItemsSource = items; Feedback.Text = $"第 {currentPage + 1} 页，每页最多 200 项；分类基于索引，清理前仍须重新读取。筛选后按搜索 / 刷新生效。";
        }
        catch (Exception error) { Feedback.Text = "索引查询失败：" + error.Message; }
    }
    private int revision;
    private static string Describe(ProtectionDecision decision) => decision.Category + "：" + decision.Reason;
    private async void Search(object sender, RoutedEventArgs e) { page = 0; await Update(); }
    private async void Previous(object sender, RoutedEventArgs e) { page = Math.Max(0, page - 1); await Update(); }
    private async void Next(object sender, RoutedEventArgs e) { page++; await Update(); }
    private void Selected(object sender, SelectionChangedEventArgs e) => FullPath.Text = (Files.SelectedItem as IndexedFile)?.Path ?? "";
    private void Copy(object sender, RoutedEventArgs e) { if (FullPath.Text.Length > 0) Clipboard.SetText(FullPath.Text); }
    private void Preview(object sender, RoutedEventArgs e) => PreviewRequested?.Invoke(Files.SelectedItems.Cast<IndexedFile>().Select(f => f.Path).ToArray());
    public async Task ShowDirectory(string directory) { SearchPath.Text = directory; page = 0; await Update(); }
    private sealed record ProjectChoice(string? Id, string Name);
}
