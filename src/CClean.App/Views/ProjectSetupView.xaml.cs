using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Monitoring;
using CClean.Core.Projects;

namespace CClean.App.Views;

public partial class ProjectSetupView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    private DirectoryDiscovery? discovery;
    private string? discoveryRoot;
    private CancellationTokenSource? cancellation;
    public ProjectSetupView(WorkspaceViewModel workspace)
    {
        InitializeComponent();
        this.workspace = workspace;
        Registered.ItemsSource = workspace.Projects;
        SessionContent.Content = new SessionsView(workspace);
        Unloaded += (_, _) =>
        {
            cancellation?.Cancel();
            if (cancellation is null) { discovery?.Dispose(); discovery = null; }
        };
    }
    private async void Discover(object sender, RoutedEventArgs e)
    {
        if (cancellation is not null) return;
        cancellation = new();
        DiscoverButton.IsEnabled = false;
        var root = RootPath.Text;
        try
        {
            var result = await Task.Run(() =>
            {
                if (discovery is null || root != discoveryRoot)
                {
                    discovery?.Dispose(); discovery = new(root); discoveryRoot = root;
                }
                return discovery.Next(cancellation.Token);
            });
            Candidates.ItemsSource = result.Candidates;
            Feedback.Text = $"本批检查 {result.Inspected} 个对象；{(result.HasMore ? "仍有未扫描范围，可继续" : "本轮扫描结束")}；缺口 {result.Gaps.Count}。候选只展示前 200 个目录。";
        }
        catch (OperationCanceledException) { Feedback.Text = "已取消；可以继续扫描剩余范围。"; }
        catch (Exception error) { Feedback.Text = "发现失败：" + error.Message; }
        finally
        {
            cancellation.Dispose(); cancellation = null; DiscoverButton.IsEnabled = true;
            if (!IsLoaded) { discovery?.Dispose(); discovery = null; }
        }
    }
    private void Cancel(object sender, RoutedEventArgs e) => cancellation?.Cancel();
    private async void Register(object sender, RoutedEventArgs e)
    {
        RegisterButton.IsEnabled = false;
        try
        {
            await workspace.Register(ProjectName.Text, RootPath.Text, ((ComboBoxItem)Tool.SelectedItem).Content.ToString()!);
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
        if (Candidates.SelectedItem is DiscoveryCandidate candidate) RootPath.Text = candidate.Path;
    }
}
