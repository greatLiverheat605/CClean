using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Projects;
using CClean.Core.Sessions;
using CClean.Core.Tools;

namespace CClean.App.Views;

public partial class SessionsView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    private ProjectModule? scope;
    public void ShowProject(ProjectModule project)
    {
        scope = project; DiscoverySearch.Text = "";
        Project.ItemsSource = workspace.Projects.Where(p => p.Id == project.RegistrationId).ToArray();
        Project.SelectedIndex = Project.Items.Count > 0 ? 0 : -1; Project.IsEnabled = false;
        Refresh(); RefreshDiscovered();
    }
    public SessionsView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace;
        Tool.ItemsSource = KnownTools.RegistrationLabels;
        Tool.SelectedIndex = 0;
        Tool.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(ToolChanged));
        ShowToolCapabilities();
        Project.ItemsSource = workspace.Projects;
        workspace.SessionsDiscovered += RefreshDiscovered;
        workspace.Projects.CollectionChanged += (_, _) => Refresh();
        Loaded += (_, _) => RefreshDiscovered();
        Loaded += (_, _) => { if (Project.SelectedIndex < 0 && Project.Items.Count > 0) Project.SelectedIndex = 0; Refresh(); };
    }
    private bool Detached(DevelopmentSession session) => session.State != SessionState.Ended &&
        (session.ProjectRoot is null || !workspace.Projects.Any(p => p.Id == session.ProjectId && p.ScopeRevision == session.ScopeRevision));
    private void Refresh()
    {
        var sessions = workspace.Sessions.All;
        var detached = sessions.Count(Detached);
        Sessions.ItemsSource = sessions.Where(s => scope is null || s.ProjectId == scope.RegistrationId ||
            IncludeDetached.IsChecked == true && Detached(s)).OrderByDescending(s => s.Started).ToArray();
        SessionNotice.Text = $"另有 {detached} 个已移除 / 旧范围或来源未知的未结束会话。移除监测不会结束会话；结束会解除该手动保护，仍需规则与当前观察核验。";
    }
    private void DetachedChanged(object sender, RoutedEventArgs e) { if (workspace is not null) Refresh(); }
    private async void Discover(object sender, RoutedEventArgs e) => await workspace.DiscoverSessions();
    private void SearchDiscovered(object sender, TextChangedEventArgs e) { if (workspace is not null) RefreshDiscovered(); }
    private void RefreshDiscovered()
    {
        var selected = Discovered.SelectedItem as DiscoveredSession;
        var query = DiscoverySearch.Text.Trim();
        var rows = workspace.DiscoveredSessions.Where(s => (scope is null || ProjectCatalog.IncludesSession(scope, s)) && (s.ProjectRoot + " " + s.Tool + " " + s.Id).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        Discovered.ItemsSource = rows;
        Discovered.SelectedItem = rows.FirstOrDefault(s => s.Tool == selected?.Tool && s.Id == selected.Id);
        DiscoveryFeedback.Text = workspace.DiscoveryStatus + (rows.Length == 0 ? " 当前无匹配记录；可使用手动会话。" : $" 当前显示 {rows.Length} 条。");
    }
    private void SelectDiscovered(object sender, SelectionChangedEventArgs e)
    {
        var selected = Discovered.SelectedItem as DiscoveredSession;
        RegisterDiscoveredButton.IsEnabled = selected is not null;
        DiscoveredDetails.Text = selected is null ? "选择会话查看项目目录和来源。" : $"项目：{selected.ProjectRoot}\n工具：{selected.Tool} · ID：{selected.Id}\n来源：{selected.SourceFile}";
    }
    private async void RegisterDiscovered(object sender, RoutedEventArgs e)
    {
        if (Discovered.SelectedItem is not DiscoveredSession selected) return;
        RegisterDiscoveredButton.IsEnabled = false;
        try
        {
            await workspace.Register(selected.ProjectName, selected.ProjectRoot, selected.Tool);
            DiscoveryFeedback.Text = "项目已加入持续监测；不会导入聊天内容或启用清理。";
        }
        catch (Exception error) { DiscoveryFeedback.Text = "未登记：" + error.Message; }
        finally { RegisterDiscoveredButton.IsEnabled = Discovered.SelectedItem is not null; }
    }
    private void Begin(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Project.SelectedItem is not RegisteredProject project) throw new InvalidOperationException("请先登记并选择项目。");
            workspace.Sessions.Begin(project, Tool.Text, Label.Text,
                Dependencies.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Refresh(); Feedback.Text = "会话已开始，项目及关联依赖受活动保护；仅表示手动上下文。";
        }
        catch (Exception error) { Feedback.Text = error.Message; }
    }
    private void End(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Sessions.SelectedItem is not DevelopmentSession session) throw new InvalidOperationException("请选择会话。");
            workspace.Sessions.End(session.Id); Refresh(); Feedback.Text = "已结束。仍需观察与规则复核，不会自动变成可删除对象。";
        }
        catch (Exception error) { Feedback.Text = error.Message; }
    }
    private void Resume(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Sessions.SelectedItem is not DevelopmentSession session) throw new InvalidOperationException("请选择中断会话。");
            workspace.Sessions.Resume(session.Id, workspace.Projects.Single(p => p.Id == session.ProjectId));
            Refresh(); Feedback.Text = "已建立新的会话时间段；原中断区间仍保留。";
        }
        catch (Exception error) { Feedback.Text = error.Message; }
    }
    private void ToolChanged(object sender, TextChangedEventArgs e) => ShowToolCapabilities();
    private void ShowToolCapabilities()
    {
        try { ToolCapabilities.Text = KnownTools.Describe(Tool.Text).Summary; }
        catch (ArgumentException error) { ToolCapabilities.Text = error.Message; }
    }
}
