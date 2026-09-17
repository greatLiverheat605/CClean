using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Projects;
using CClean.Core.Sessions;

namespace CClean.App.Views;

public partial class SessionsView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    public SessionsView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace;
        Project.ItemsSource = workspace.Projects;
        Loaded += (_, _) => { if (Project.SelectedIndex < 0 && workspace.Projects.Count > 0) Project.SelectedIndex = 0; Refresh(); };
    }
    private void Refresh() => Sessions.ItemsSource = workspace.Sessions.All.OrderByDescending(s => s.Started).ToArray();
    private void Begin(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Project.SelectedItem is not RegisteredProject project) throw new InvalidOperationException("请先登记并选择项目。");
            workspace.Sessions.Begin(project, ((ComboBoxItem)Tool.SelectedItem).Content.ToString()!, Label.Text,
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
}
