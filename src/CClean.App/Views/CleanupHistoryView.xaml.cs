using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Cleanup;

namespace CClean.App.Views;
public partial class CleanupHistoryView : UserControl
{
    private const int PageSize = 200;
    private readonly WorkspaceViewModel workspace;
    private readonly HashSet<CancellationTokenSource> runQueries = [], detailQueries = [];
    private readonly HashSet<Task> pendingReads = [];
    private readonly Stack<CleanupRunCursor?> priorRuns = [];
    private CleanupRunCursor? runCursor, nextRunCursor;
    private CleanupRun? selectedRun;
    private DetailPosition[] positions = [new(-1), new(0), new(-1)];
    private int runRevision, detailRevision;
    private bool suppressSelection, readingRuns, readingDetails, validRunsPage, validDetailPage;
    // Probe-only asynchronous barrier; production never installs it.
    internal Func<string, Task>? BeforeRead { get; set; }

    private sealed class DetailPosition(long cursor)
    {
        public long Cursor = cursor, Next;
        public long? Through;
        public bool HasMore;
        public Stack<long> Previous { get; } = [];
    }
    private sealed record RunRow(CleanupRun Run)
    {
        public string Id => Run.Id;
        public string Created => LocalTime(Run.Created);
        public string State => StateText(Run.State);
        public string Summary => Run.Summary;
    }
    private sealed record HistoryRow(string Path, string State, string Reason, string Time, long Sequence = 0);
    private sealed record DetailPage(IReadOnlyList<HistoryRow> Rows, long Next, long? Through, int? Total,
        int Checked, bool HasMore, bool RunFound);

    public CleanupHistoryView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace;
        Loaded += async (_, _) => await LoadRuns();
        Unloaded += (_, _) => CancelPendingOperations();
    }
    public void CancelPendingOperations()
    {
        CancelRuns(); CancelDetails(); readingRuns = readingDetails = false; UpdateButtons();
    }
    public async Task WaitForPendingOperationsAsync()
    {
        while (pendingReads.Count > 0) await Task.WhenAll(pendingReads.ToArray());
    }
    private void CancelRuns() { runRevision++; foreach (var query in runQueries.ToArray()) query.Cancel(); }
    private void CancelDetails() { detailRevision++; foreach (var query in detailQueries.ToArray()) query.Cancel(); }
    private async Task Query<T>(bool runs, string kind, Func<CleanupJournal, CancellationToken, T> read, Action<T> publish)
    {
        if (workspace.Stopping) return;
        if (runs) { CancelRuns(); readingRuns = true; validRunsPage = false; }
        else { CancelDetails(); readingDetails = true; validDetailPage = false; }
        var request = runs ? runRevision : detailRevision;
        using var cancellation = new CancellationTokenSource();
        var queries = runs ? runQueries : detailQueries; queries.Add(cancellation);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); pendingReads.Add(completed.Task);
        UpdateButtons();
        bool Current() => !workspace.Stopping && !cancellation.IsCancellationRequested && request == (runs ? runRevision : detailRevision);
        try
        {
            var journal = workspace.OpenCleanupJournal(); var barrier = BeforeRead;
            var result = await Task.Run(async () =>
            {
                if (barrier is not null) await barrier(kind);
                cancellation.Token.ThrowIfCancellationRequested();
                return read(journal, cancellation.Token);
            });
            if (Current()) publish(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (Current())
            {
                if (runs) Status.Text = "审计任务读取失败；已载记录不代表本次刷新成功：" + error.Message;
                else DetailStatus.Text = "明细读取失败；已载记录不代表本次查询成功：" + error.Message;
            }
        }
        finally
        {
            if (request == (runs ? runRevision : detailRevision))
            {
                if (runs) readingRuns = false; else readingDetails = false;
                UpdateButtons();
            }
            queries.Remove(cancellation); pendingReads.Remove(completed.Task); completed.TrySetResult();
        }
    }
    private Task LoadRuns()
    {
        if (workspace.Stopping) return Task.CompletedTask;
        CancelDetails(); var cursor = runCursor; var selectedId = (Runs.SelectedItem as RunRow)?.Id;
        Status.Text = "正在读取历史任务，每页最多 200 项…";
        return Query(true, "runs", (journal, token) => journal.RunsPage(cursor, PageSize, token), page =>
        {
            var rows = page.Items.Select(run => new RunRow(run)).ToArray();
            suppressSelection = true;
            try
            {
                Runs.ItemsSource = rows;
                Runs.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedId) ?? rows.FirstOrDefault();
            }
            finally { suppressSelection = false; }
            nextRunCursor = page.HasMore ? page.NextCursor : null; validRunsPage = true;
            Status.Text = rows.Length == 0 ? "暂无清理记录。仅预览不会生成删除任务。" :
                $"任务第 {priorRuns.Count + 1} 页，本页 {rows.Length} 项；{(page.HasMore ? "还有后续任务，可翻页查看。" : "已到当前记录末尾。")}";
            SetRun((Runs.SelectedItem as RunRow)?.Run);
        });
    }
    private void SetRun(CleanupRun? run)
    {
        if (selectedRun?.Id != run?.Id)
        {
            CancelDetails(); positions = [new(-1), new(0), new(-1)]; Events.ItemsSource = null;
        }
        selectedRun = run; _ = LoadDetails();
    }
    private Task LoadDetails()
    {
        if (workspace.Stopping || selectedRun is null)
        {
            if (!workspace.Stopping) { Events.ItemsSource = null; DetailStatus.Text = "选择任务查看具体记录。"; }
            validDetailPage = false; UpdateButtons(); return Task.CompletedTask;
        }
        var run = selectedRun; var mode = Math.Max(0, DetailMode.SelectedIndex); var position = positions[mode];
        var cursor = position.Cursor; var through = position.Through;
        var name = mode switch { 1 => "原始审计", 2 => "恢复核查", _ => "批准清单" };
        DetailExplanation.Text = mode switch
        {
            1 => "原始审计保留当时事件。翻页固定本次读取的审计边界，刷新才纳入后来的事件；缺少结果不等于删除成功。",
            2 => "恢复核查只读观察当前对象，不恢复文件、不续删，也不改写原始审计；读取失败或缺失不能证明删除来源。",
            _ => "批准清单记录当时选择与排除，不代表全部执行成功；原任务不能重新执行，后续处理须重新预览。"
        };
        DetailStatus.Text = "正在读取" + name + "，每页最多检查 200 项…";
        return Query(false, mode switch { 1 => "events", 2 => "recovery", _ => "manifest" },
            (journal, token) => ReadDetails(journal, run.Id, mode, cursor, through, token), page =>
        {
            if (!page.RunFound)
            {
                Events.ItemsSource = null; DetailStatus.Text = "原任务已不可读取；不能用空清单表示执行结果。"; return;
            }
            position.Next = page.Next; position.Through = page.Through; position.HasMore = page.HasMore;
            Events.ItemsSource = page.Rows; validDetailPage = true;
            DetailStatus.Text = $"{name}第 {position.Previous.Count + 1} 页；本页检查 {page.Checked} 项，显示 {page.Rows.Count} 项" +
                (page.Total is int total ? $"，批准清单共 {total} 项" : "") +
                (page.HasMore ? "；还有后续记录。" : "；本次读取已到末尾。") +
                (mode == 2 ? " 核查按清单序号推进，排除项不作删除观察。" : "");
        });
    }
    private static DetailPage ReadDetails(CleanupJournal journal, string run, int mode, long cursor, long? through, CancellationToken token)
    {
        if (mode == 1)
        {
            var page = journal.EventsPage(run, cursor, PageSize, through, token);
            return new(page.Items.Select(EventRow).ToArray(), page.NextSequence, page.ThroughSequence, null, page.Items.Count, page.HasMore, page.RunFound);
        }
        if (mode == 2)
        {
            var page = CleanupRecovery.InspectPage(journal, run, checked((int)cursor), PageSize, through, token);
            return new(page.Items.Select(EventRow).ToArray(), page.NextOrdinal, page.ThroughSequence, page.TotalItems, page.CheckedItems, page.HasMore, page.RunFound);
        }
        var manifest = journal.ManifestPage(run, checked((int)cursor), PageSize, token);
        return new(manifest.Items.Select(item => new HistoryRow(item.Path, item.Decision.Eligible ? "当时批准候选" : "当时排除",
            item.Reason, "")).ToArray(), manifest.NextOrdinal, null, manifest.TotalItems, manifest.Items.Count, manifest.HasMore, manifest.RunFound);
    }
    private static HistoryRow EventRow(CleanupEvent entry) => new(entry.Path, StateText(entry.State), entry.Reason, LocalTime(entry.Time), entry.Sequence);
    private static string LocalTime(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "时间未知";
    private static string StateText(string value) => value switch
    {
        "Running" => "执行中", "Completed" => "已完成", "Cancelled" => "已停止后续项", "Failed" => "失败，需核查",
        "Interrupted" => "上次中断", "NeedsPreview" => "需要重新预览", "Started" => "已记录处理意图", "Deleted" => "已记录删除结果",
        "Skipped" => "已跳过", "SpaceBefore" => "处理前容量采样", "SpaceAfter" => "处理后容量采样", "RecoveryObservation" => "当前只读核查",
        _ => "其他记录（" + value + "）"
    };
    private void UpdateButtons()
    {
        if (workspace is null || RunPreviousButton is null) return;
        var active = !workspace.Stopping;
        RunPreviousButton.IsEnabled = active && !readingRuns && validRunsPage && priorRuns.Count > 0;
        RunNextButton.IsEnabled = active && !readingRuns && validRunsPage && nextRunCursor is not null;
        var position = positions[Math.Max(0, DetailMode.SelectedIndex)];
        DetailPreviousButton.IsEnabled = active && !readingDetails && validDetailPage && position.Previous.Count > 0;
        DetailNextButton.IsEnabled = active && !readingDetails && validDetailPage && position.HasMore;
        RefreshButton.IsEnabled = active;
    }
    private async void Refresh(object sender, RoutedEventArgs e)
    {
        CancelPendingOperations(); priorRuns.Clear(); runCursor = null;
        positions = [new(-1), new(0), new(-1)]; await LoadRuns();
    }
    private void Select(object sender, SelectionChangedEventArgs e)
    { if (!suppressSelection && workspace is not null && !workspace.Stopping) SetRun((Runs.SelectedItem as RunRow)?.Run); }
    private async void ModeChanged(object sender, SelectionChangedEventArgs e)
    { if (workspace is not null && !workspace.Stopping) await LoadDetails(); }
    private async void PreviousRuns(object sender, RoutedEventArgs e)
    { if (RunPreviousButton.IsEnabled) { runCursor = priorRuns.Pop(); await LoadRuns(); } }
    private async void NextRuns(object sender, RoutedEventArgs e)
    { if (RunNextButton.IsEnabled) { priorRuns.Push(runCursor); runCursor = nextRunCursor; await LoadRuns(); } }
    private async void PreviousDetails(object sender, RoutedEventArgs e)
    { if (DetailPreviousButton.IsEnabled) { var position = positions[Math.Max(0, DetailMode.SelectedIndex)]; position.Cursor = position.Previous.Pop(); await LoadDetails(); } }
    private async void NextDetails(object sender, RoutedEventArgs e)
    { if (DetailNextButton.IsEnabled) { var position = positions[Math.Max(0, DetailMode.SelectedIndex)]; position.Previous.Push(position.Cursor); position.Cursor = position.Next; await LoadDetails(); } }
}
