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
    private bool executing, stopRequested;
    internal Func<Task>? BeforeSpaceSampling { get; set; }
    private readonly HashSet<Task> pending = [];
    private readonly System.Windows.Threading.DispatcherTimer expiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Func<Task<CleanupContext>> getContext;
    public CleanupPreviewView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace; getContext = workspace.CleanupContext;
        Loaded += (_, _) => { RootChoice.ItemsSource = workspace.Projects; RefreshApproval(DateTimeOffset.UtcNow); expiryTimer.Start(); };
        Unloaded += (_, _) => { expiryTimer.Stop(); CancelPendingOperations(); };
        expiryTimer.Tick += (_, _) => RefreshApproval(DateTimeOffset.UtcNow);
    }
    public void CancelPendingOperations()
    {
        cancellation?.Cancel(); plan = null; ExecuteButton.IsEnabled = false;
        Items.ItemsSource = null;
    }
    public async Task WaitForPendingOperationsAsync()
    {
        while (pending.Count > 0) await Task.WhenAll(pending.ToArray());
    }
    private async Task Track(Task operation)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Add(drained.Task);
        try { await operation; }
        finally { pending.Remove(drained.Task); drained.TrySetResult(); }
    }
    internal void RefreshApproval(DateTimeOffset now)
    {
        if (plan is not null && (now < plan.Created || now >= plan.Expires || workspace.Stopping))
        {
            plan = null; ExecuteButton.IsEnabled = false;
            Summary.Text = "清单已失效；请重新预览，尚未执行删除。";
        }
    }
    private void SaveRule(object sender, RoutedEventArgs e)
    {
        if (busy || workspace.CleanupBusy || workspace.Stopping) return;
        try
        {
            if (RootChoice.SelectedItem is not RegisteredProject project) throw new ArgumentException("请选择已登记的专用缓存根。");
            workspace.Rules.ConfigureNpm(project.Root, "11.19.0", Enabled.IsChecked == true, Exclusive.IsChecked == true);
            plan = null; ExecuteButton.IsEnabled = false; Items.ItemsSource = null; Summary.Text = "规则已保存，旧清单失效。外部规则只影响此根，需重新预览。";
        }
        catch (Exception error) { Summary.Text = "规则未保存：" + error.Message; }
    }
    private async void PreviewRules(object sender, RoutedEventArgs e) => await Preview(null);
    private void CancelPreview(object sender, RoutedEventArgs e)
    {
        if (executing || workspace.CleanupRunning) return;
        CancelPendingOperations();
        Summary.Text = busy ? "已请求取消，等待当前读取结束；没有删除文件。" : "已取消预览；没有删除文件。";
    }
    private async void PreviewDumps(object sender, RoutedEventArgs e) => await PreviewCrashDumps();
    public Task PreviewCrashDumps()
    {
        return Track(PreviewCore(null, () =>
        {
            var protectedRoots = workspace.Projects.Select(p => p.Root).Concat(workspace.DiscoveredSessions.Select(s => s.ProjectRoot)).Append(workspace.DataDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return Task.Run(() => CrashDumpRule.Context(protectedRoots));
        }, dumps: true));
    }
    public Task Preview(string[]? paths, Func<Task<CleanupContext>>? context = null) => Track(PreviewCore(paths, context));
    private async Task PreviewCore(string[]? paths, Func<Task<CleanupContext>>? context, bool dumps = false)
    {
        if (busy || workspace.CleanupBusy || workspace.Stopping) return;
        PreviewIntro.Text = context is null ? "独占 npm 缓存：清理后可能需要联网重建；规则默认关闭。"
            : "旧 WSL 故障转储：保留最新及最近 48 小时文件。只删除诊断转储，不清理项目、发行版或开发依赖。";
        NpmSetup.Visibility = context is null ? Visibility.Visible : Visibility.Collapsed;
        PreviewRulesButton.Visibility = DumpPreviewButton.Visibility = Visibility.Visible;
        busy = true; getContext = context ?? workspace.CleanupContext; plan = null; ExecuteButton.IsEnabled = false; cancellation = new(); var token = cancellation.Token;
        Items.ItemsSource = null; Summary.Text = "正在复核当前对象与消费者，尚未删除文件…";
        try
        {
            if (dumps) paths = await Task.Run(CrashDumpRule.GetPaths, token);
            token.ThrowIfCancellationRequested();
            var current = await getContext();
            token.ThrowIfCancellationRequested();
            var candidate = await Task.Run(() =>
            {
                if (paths is null)
                {
                    var selected = new List<string>(); var extra = new List<Core.Sessions.ActivityScope>();
                    foreach (var rule in current.Rules.Where(r => r.Enabled))
                    {
                        token.ThrowIfCancellationRequested();
                        var scan = DirectoryScanner.Scan(rule.Root, token);
                        selected.AddRange(scan.Files.Values.Select(f => f.Path).Take(1001 - selected.Count));
                        if (scan.Gaps.Count > 0) extra.Add(new(rule.Root, false, false, "批量扫描存在缺口"));
                        if (selected.Count > 1000) break;
                    }
                    current = current with { Activity = current.Activity with { Scopes = current.Activity.Scopes.Concat(extra).ToArray() } };
                    paths = selected.ToArray();
                }
                return CleanupPlanner.Preview(paths, current, token);
            }, token);
            token.ThrowIfCancellationRequested();
            if (workspace.Stopping) return;
            plan = candidate;
            Items.ItemsSource = plan.Items;
            Summary.Text = $"{plan.Items.Count} 项；候选 {plan.EligibleCount} 项，分配估算 {IndexedFile.FormatBytes(plan.EstimatedBytes)}；排除 {plan.Items.Count - plan.EligibleCount} 项。" + (plan.Truncated ? "超过 1000 项上限，请缩小选择。" : "");
            Details.Text = $"清单 {plan.Id} · 到期 {plan.Expires.ToLocalTime():HH:mm:ss}。永久删除不可恢复；分配估算不等于系统实际释放。";
            ExecuteButton.IsEnabled = plan.EligibleCount > 0 && !plan.Truncated;
            RefreshApproval(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { plan = null; Summary.Text = "预览已取消；没有删除文件。"; }
        catch (Exception error) { plan = null; Summary.Text = "预览失败：" + error.Message; }
        finally { cancellation.Dispose(); cancellation = null; busy = false; }
    }
    private void StopExecution(object sender, RoutedEventArgs e)
    {
        if (!executing && !workspace.CleanupRunning) return;
        stopRequested = true; workspace.CancelCleanup(); Summary.Text = "已请求停止后续项；已完成的永久删除不能恢复。";
    }
    private async void Execute(object sender, RoutedEventArgs e) => await Track(ExecuteCore());
    private async Task ExecuteCore()
    {
        RefreshApproval(DateTimeOffset.UtcNow);
        if (plan is null || busy || workspace.CleanupBusy || workspace.Stopping) return;
        var approved = plan;
        var consequence = approved.Items.Any(i => i.Decision.Scope?.RuleId == CrashDumpRule.Id)
            ? "仅清理旧故障转储，保留最新及最近 48 小时文件；旧故障诊断内容删除后无法恢复。"
            : "可能需要联网重建，离线安装可能失败。";
        if (MessageBox.Show(Window.GetWindow(this), $"永久删除本清单中 {approved.EligibleCount} 个候选文件，分配估算 {IndexedFile.FormatBytes(approved.EstimatedBytes)}。\n\n不进入回收站；{consequence}清单外与排除项不执行。\n\n确认执行清单 {approved.Id}？", "确认永久删除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await ExecuteConfirmed(approved);
    }
    // Called only after the explicit dialog; the internal probe supplies its own isolated approval.
    internal async Task ExecuteConfirmed(CleanupPlan approved)
    {
        if (busy || workspace.CleanupBusy || workspace.Stopping) return;
        ExecuteButton.IsEnabled = false; busy = executing = true; stopRequested = false; plan = null;
        try
        {
            var volumes = approved.Items.Where(i => i.Decision.Eligible).Select(i => System.IO.Path.GetPathRoot(i.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (BeforeSpaceSampling is not null) await BeforeSpaceSampling();
            var before = await Task.Run(() => VolumeSpaceReader.ReadVolumes(volumes));
            if (stopRequested) { Items.ItemsSource = null; Summary.Text = "已在开始前停止；没有删除文件。"; return; }
            var result = await workspace.ExecuteCleanup(approved, getContext);
            Summary.Text = result.Message; // Preserve the actual outcome even if later space sampling fails.
            var after = await Task.Run(() => VolumeSpaceReader.ReadVolumes(volumes));
            Summary.Text = result.Message;
            Items.ItemsSource = null; // The old preview is no longer an actionable candidate list.
            Details.Text = $"{result.State}；已标记对象分配估算 {IndexedFile.FormatBytes(result.EstimatedDeletedBytes)}。\n" + DescribeSpaceChange(before, after);
        }
        catch (Exception error) { Summary.Text = "未继续执行：" + error.Message; }
        finally { plan = null; busy = executing = false; }
    }
    internal static string DescribeSpaceChange(IReadOnlyList<VolumeSpaceSample> before, IReadOnlyList<VolumeSpaceSample> after) =>
        string.Join("\n", after.Select(sample =>
        {
            var prior = before.FirstOrDefault(p => p.Root.Equals(sample.Root, StringComparison.OrdinalIgnoreCase));
            return prior?.Space is { } first && sample.Space is { } last
                ? $"{sample.Root} 系统可用量变化 {(decimal)last.UserAvailableBytes - first.UserAvailableBytes:+#,##0;-#,##0;0} B（含其他进程影响，不能全归因于本次清理）。"
                : $"{sample.Root} 系统可用量变化未知：{prior?.Error ?? sample.Error ?? "缺少测量"}；清理结果以逐项审计为准。";
        }));
}
