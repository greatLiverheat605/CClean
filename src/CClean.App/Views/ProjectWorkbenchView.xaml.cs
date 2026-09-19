using System.Windows;
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
    private ProjectModule? selected;
    private int preferredSection;
    private bool opening;
    private int generation;
    public event Action<string[]>? PreviewRequested;
    public ProjectWorkbenchView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace;
        files = new(workspace); timeline = new(workspace); sessions = new(workspace); space = new(workspace);
        FilesContent.Content = files; TimelineContent.Content = timeline; SessionsContent.Content = sessions; SpaceContent.Content = space;
        files.PreviewRequested += paths => PreviewRequested?.Invoke(paths);
        space.PreviewRequested += paths => PreviewRequested?.Invoke(paths);
        timeline.CurrentDirectoryRequested += async directory => { Sections.SelectedIndex = 1; await files.ShowDirectory(directory); };
        workspace.SessionsDiscovered += RefreshModules;
        workspace.Projects.CollectionChanged += (_, _) => RefreshModules();
        Loaded += (_, _) => RefreshModules();
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
        var all = ProjectCatalog.Build(workspace.Projects, workspace.DiscoveredSessions);
        var matches = all.Where(p => (p.Name + " " + p.Root).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        Modules.ItemsSource = matches;
        var current = all.FirstOrDefault(p => p.Root.Equals(selected?.Root, StringComparison.OrdinalIgnoreCase));
        if (current is not null && selected is not null &&
            (current.RegistrationId != selected.RegistrationId || current.Paused != selected.Paused)) _ = OpenProject(current);
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
            (project.RegistrationId is null ? "。没有文件开发历史，可加入监测；清理位置支持只读盘点。" : "。下方页面仅展示当前项目，清理需逐文件复核。");
        RegisterButton.IsEnabled = project.RegistrationId is null;
        ModulesPanel.Visibility = Visibility.Collapsed; DetailPanel.Visibility = Visibility.Visible;
        space.ShowProject(project); sessions.ShowProject(project);
        try { await Task.WhenAll(timeline.ShowProject(project), files.ShowProject(project)); }
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
            if (selected == project) await OpenProject(ProjectCatalog.Build(workspace.Projects, workspace.DiscoveredSessions).Single(p => p.Root.Equals(project.Root, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception error) { if (selected == project) { ProjectStatus.Text = "未登记：" + error.Message; RegisterButton.IsEnabled = true; } }
    }
}
