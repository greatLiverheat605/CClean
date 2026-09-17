using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Cleanup;

namespace CClean.App.Views;
public partial class CleanupHistoryView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    public CleanupHistoryView(WorkspaceViewModel workspace) { InitializeComponent(); this.workspace = workspace; Loaded += (_, _) => Update(); }
    private void Refresh(object sender, RoutedEventArgs e) => Update();
    private void Update()
    {
        try { Runs.ItemsSource = workspace.OpenCleanupJournal().Runs(); Status.Text = "最近最多 200 次任务；中断不自动续删，缺失不等于上次删除成功。"; }
        catch (Exception error) { Status.Text = "审计暂不可用：" + error.Message; }
    }
    private void Select(object sender, SelectionChangedEventArgs e)
    {
        try { Events.ItemsSource = Runs.SelectedItem is CleanupRun run ? run.State is "Interrupted" or "Failed" ? CleanupRecovery.Inspect(workspace.OpenCleanupJournal(), run.Id) : workspace.OpenCleanupJournal().Events(run.Id) : null; }
        catch (Exception error) { Status.Text = "读取记录失败：" + error.Message; }
    }
}
