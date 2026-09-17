using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Cleanup;
using CClean.Core.Monitoring;
using CClean.Core.Projects;
using CClean.Core.Storage;

namespace CClean.App.Views;

public partial class CleanupPreviewView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    private CancellationTokenSource? cancellation;
    private CleanupPlan? plan;
    private bool busy;
    public CleanupPreviewView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace;
        Loaded += (_, _) => { RootChoice.ItemsSource = workspace.Projects; };
        Unloaded += (_, _) => cancellation?.Cancel();
    }
    private void SaveRule(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            if (RootChoice.SelectedItem is not RegisteredProject project) throw new ArgumentException("请选择已登记的专用缓存根。");
            workspace.Rules.ConfigureNpm(project.Root, "11.19.0", Enabled.IsChecked == true, Exclusive.IsChecked == true);
            plan = null; ExecuteButton.IsEnabled = false; Items.ItemsSource = null; Summary.Text = "规则已保存，旧清单失效。外部规则只影响此根，需重新预览。";
        }
        catch (Exception error) { Summary.Text = "规则未保存：" + error.Message; }
    }
    private async void PreviewRules(object sender, RoutedEventArgs e) => await Preview(null);
    private void CancelPreview(object sender, RoutedEventArgs e) { if (workspace.CleanupRunning) return; cancellation?.Cancel(); plan = null; ExecuteButton.IsEnabled = false; Items.ItemsSource = null; Summary.Text = "已取消预览；没有删除文件。"; }
    public async Task Preview(string[]? paths)
    {
        if (busy || workspace.CleanupRunning) return;
        busy = true; plan = null; ExecuteButton.IsEnabled = false; cancellation = new(); var token = cancellation.Token;
        Items.ItemsSource = null; Summary.Text = "正在复核当前对象与消费者，尚未删除文件…";
        try
        {
            var context = await workspace.CleanupContext();
            plan = await Task.Run(() =>
            {
                if (paths is null)
                {
                    var selected = new List<string>(); var extra = new List<Core.Sessions.ActivityScope>();
                    foreach (var rule in context.Rules.Where(r => r.Enabled))
                    {
                        token.ThrowIfCancellationRequested();
                        var scan = DirectoryScanner.Scan(rule.Root, token);
                        selected.AddRange(scan.Files.Values.Select(f => f.Path).Take(1001 - selected.Count));
                        if (scan.Gaps.Count > 0) extra.Add(new(rule.Root, false, false, "批量扫描存在缺口"));
                        if (selected.Count > 1000) break;
                    }
                    context = context with { Activity = context.Activity with { Scopes = context.Activity.Scopes.Concat(extra).ToArray() } };
                    paths = selected.ToArray();
                }
                return CleanupPlanner.Preview(paths, context, token);
            }, token);
            token.ThrowIfCancellationRequested();
            Items.ItemsSource = plan.Items;
            Summary.Text = $"{plan.Items.Count} 项；候选 {plan.EligibleCount} 项，分配估算 {IndexedFile.FormatBytes(plan.EstimatedBytes)}；排除 {plan.Items.Count - plan.EligibleCount} 项。" + (plan.Truncated ? "超过 1000 项上限，请缩小选择。" : "");
            Details.Text = $"清单 {plan.Id} · 到期 {plan.Expires.ToLocalTime():HH:mm:ss}。永久删除不可恢复；分配估算不等于系统实际释放。";
            ExecuteButton.IsEnabled = plan.EligibleCount > 0 && !plan.Truncated;
        }
        catch (OperationCanceledException) { plan = null; Summary.Text = "预览已取消；没有删除文件。"; }
        catch (Exception error) { plan = null; Summary.Text = "预览失败：" + error.Message; }
        finally { cancellation.Dispose(); cancellation = null; busy = false; }
    }
    private void StopExecution(object sender, RoutedEventArgs e) { workspace.CancelCleanup(); Summary.Text = "已请求停止后续项；已完成的永久删除不能恢复。"; }
    private async void Execute(object sender, RoutedEventArgs e)
    {
        if (plan is null || busy || workspace.CleanupRunning) return;
        var approved = plan;
        if (MessageBox.Show(Window.GetWindow(this), $"永久删除本清单中 {approved.EligibleCount} 个候选文件，分配估算 {IndexedFile.FormatBytes(approved.EstimatedBytes)}。\n\n不进入回收站；可能需要联网重建，离线安装可能失败。清单外与排除项不执行。\n\n确认执行清单 {approved.Id}？", "确认永久删除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ExecuteButton.IsEnabled = false; busy = true;
        try
        {
            var volumes = approved.Items.Where(i => i.Decision.Eligible).Select(i => System.IO.Path.GetPathRoot(i.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var before = await Task.Run(() => volumes.ToDictionary(v => v, VolumeSpaceReader.Read));
            var result = await workspace.ExecuteCleanup(approved);
            var after = await Task.Run(() => volumes.ToDictionary(v => v, VolumeSpaceReader.Read));
            Summary.Text = result.Message;
            Details.Text = $"{result.State}；已标记对象分配估算 {IndexedFile.FormatBytes(result.EstimatedDeletedBytes)}。\n" + string.Join("\n", volumes.Select(v => $"{v} 系统可用量变化 {(decimal)after[v].UserAvailableBytes - before[v].UserAvailableBytes:+#,##0;-#,##0;0} B（含其他进程影响，不能全归因于本次清理）。"));
        }
        catch (Exception error) { Summary.Text = "未继续执行：" + error.Message; }
        finally { plan = null; busy = false; }
    }
}
