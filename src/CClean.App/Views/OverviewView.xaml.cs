using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.Core.Cleanup;
using CClean.Core.Monitoring;
using CClean.Core.Projects;
using CClean.Core.Storage;

namespace CClean.App.Views;

public partial class OverviewView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    private SpaceSurvey? survey;
    private SpaceSurveyResult? result;
    private CancellationTokenSource? scanning;
    private bool updatingVolumes, updatingFilters, storedResult, storedFoldersTruncated, loadingStoredSummary, loadedSummary;
    private int updateRevision, scopeGeneration, page;
    private string? pendingScope;
    private string selectedVolume = Path.GetPathRoot(Environment.SystemDirectory)!;
    private Guid? surveyRegistrationId;
    private long surveyScopeRevision = 1, displayScopeRevision = 1;
    private const int PageSize = 100;
    private FilesView? files;
    private double overviewOffset;
    private bool firstLoad = true;
    private Stopwatch? surveyClock;
    private string surveyPhase = "盘点尚未开始";
    private readonly System.Windows.Threading.DispatcherTimer scanClockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TaskCompletionSource? pendingSurvey;
    private readonly HashSet<Task> pendingReads = [];
    private sealed record VolumeChoiceItem(string Root, string Label, VolumeSpaceSample Sample);
    private sealed record ScopeChoiceItem(string? Root, string Label);
    private sealed record CategoryChoice(ContentPurpose? Purpose, string Label, string Measurement, string State);
    public event Action<string?>? ManageScopeRequested;

    public OverviewView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace;
        scanClockTimer.Tick += (_, _) => UpdateScanTiming();
        RefreshScopeChoices();
        ScanRoot.Text = selectedVolume;
        Loaded += async (_, _) =>
        {
            if (workspace.Stopping) return;
            if (firstLoad)
            {
                firstLoad = false;
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => OverviewScroll.ScrollToTop()));
            }
            if (!loadedSummary) { loadedSummary = true; LoadStoredSummary(); }
            RefreshScopeChoices(); await Update();
        };
        Unloaded += (_, _) => { CancelPendingOperations(); if (scanning is null) { survey?.Dispose(); survey = null; } };
        workspace.Indexed += async () => { if (IsVisible) await Update(); };
        workspace.Projects.CollectionChanged += (_, _) => ScopeRegistrationsChanged();
        workspace.SessionsDiscovered += () => RefreshScopeChoices(sessionsOnly: true);
        ShowResults();
    }

    public void CancelPendingOperations()
    {
        files?.CancelPendingOperations();
        if (scanning is null) return;
        surveyPhase = "正在停止，当前读取 / 写入收尾中"; UpdateScanTiming();
        CancelButton.IsEnabled = false; scanning.Cancel();
    }
    public async Task WaitForPendingOperationsAsync()
    {
        while (true)
        {
            var tasks = pendingReads.Append(pendingSurvey?.Task ?? Task.CompletedTask)
                .Append(files?.WaitForPendingOperationsAsync() ?? Task.CompletedTask).Where(t => !t.IsCompleted).ToArray();
            if (tasks.Length == 0) return;
            await Task.WhenAll(tasks);
        }
    }
    public void ShowScope(string root)
    {
        if (workspace.Stopping) return;
        if (FilesPanel.Visibility == Visibility.Visible) BackFromFiles(this, new());
        if (scanning is not null)
        {
            pendingScope = root; scopeGeneration++; CancelPendingOperations(); return;
        }
        if (ScanRoot.Text.Equals(root, StringComparison.OrdinalIgnoreCase)) { RefreshScopeChoices(); return; }
        ScanRoot.Text = root;
    }
    private void ScopeRegistrationsChanged()
    {
        if (workspace.Stopping) return;
        RefreshScopeChoices();
        if (survey is null && result is null) return;
        var root = survey?.Root ?? result!.Root;
        var current = workspace.Projects.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
        if (current?.Id == surveyRegistrationId && (current?.ScopeRevision ?? 1) == surveyScopeRevision) return;
        scopeGeneration++; CancelPendingOperations();
        if (result is not null) { result = result with { IsStale = true }; ShowResults(); }
        if (scanning is null) { survey?.Dispose(); survey = null; }
        ScanStatus.Text = "范围已修订或移除；原结果仅供旧范围查阅，请确认当前路径后重新盘点。";
    }
    private void RefreshScopeChoices(bool sessionsOnly = false)
    {
        if (workspace is null || workspace.Stopping || ScopeChoice is null) return;
        var projects = new[] { new ScopeChoiceItem(null, "不限定项目位置") }.Concat(
            workspace.Projects.Select(p => new ScopeChoiceItem(p.Root, p.Name + " · " + p.Root))
            .Concat(workspace.DiscoveredSessions.Select(s => new ScopeChoiceItem(s.ProjectRoot, Path.GetFileName(s.ProjectRoot) + " · " + s.ProjectRoot)))
            .DistinctBy(p => p.Root, StringComparer.OrdinalIgnoreCase)).ToArray();
        var shownProjects = ProjectChoice.ItemsSource as IEnumerable<ScopeChoiceItem>;
        if (sessionsOnly && shownProjects is not null && shownProjects.SequenceEqual(projects)) return;
        updatingFilters = true;
        try
        {
            var root = ScanRoot.Text;
            var scopes = new[] { new ScopeChoiceItem(null, "手动目录 / 未登记范围") }.Concat(workspace.Projects.Select(p =>
                new ScopeChoiceItem(p.Root, $"{p.Name} · {Path.GetPathRoot(p.Root)} · {(p.Paused ? "观察暂停" : "已登记")}"))).ToArray();
            ScopeChoice.ItemsSource = scopes;
            ScopeChoice.SelectedItem = scopes.FirstOrDefault(p => p.Root?.Equals(root, StringComparison.OrdinalIgnoreCase) == true) ?? scopes[0];
            if (shownProjects is null || !shownProjects.SequenceEqual(projects))
            { ProjectChoice.ItemsSource = projects; shownProjects = projects; }
            ProjectChoice.SelectedItem = shownProjects.FirstOrDefault(p => p.Root?.Equals(root, StringComparison.OrdinalIgnoreCase) == true) ?? shownProjects.First();
            var tool = ToolFilter.SelectedItem as string;
            var tools = new[] { "全部工具 / 关联未知" }.Concat(workspace.Projects.Select(p => p.Tool)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            ToolFilter.ItemsSource = tools; ToolFilter.SelectedItem = tools.Contains(tool) ? tool : tools[0];
        }
        finally { updatingFilters = false; }
    }
    private void ScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingFilters && ScopeChoice.SelectedItem is ScopeChoiceItem { Root: { } root }) ShowScope(root);
    }
    private void ProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingFilters) return;
        if (ProjectChoice.SelectedItem is ScopeChoiceItem { Root: { } root }) ShowScope(root);
        page = 0; ShowResults();
    }
    private async void Refresh(object sender, RoutedEventArgs e) => await Update();
    private async Task Update()
    {
        if (workspace.Stopping) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); pendingReads.Add(completion.Task);
        var request = ++updateRevision;
        var keys = workspace.CurrentObservationKeys;
        try
        {
            var volumes = await Task.Run(VolumeSpaceReader.ReadLocalVolumes);
            if (workspace.Stopping || request != updateRevision) return;
            updatingVolumes = true;
            try
            {
                var choices = volumes.Select(v => new VolumeChoiceItem(v.Root, v.Space is null ? $"{v.Root} 容量未知：{v.Error}" :
                    $"{v.Root} 可用 {v.Space.UserAvailableBytes / 1073741824d:F1} / {v.Space.TotalBytes / 1073741824d:F1} GiB", v)).ToArray();
                VolumeChoice.ItemsSource = choices;
                VolumeChoice.SelectedItem = choices.FirstOrDefault(v => v.Root.Equals(selectedVolume, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
                if (VolumeChoice.SelectedItem is VolumeChoiceItem selected) selectedVolume = selected.Root;
            }
            finally { updatingVolumes = false; }
            var sample = (VolumeChoice.SelectedItem as VolumeChoiceItem)?.Sample;
            var space = sample?.Space;
            DiskSummary.Text = space is null ? $"{selectedVolume} 容量未知：{sample?.Error ?? "卷未列出或不可用"}" :
                $"{selectedVolume} 可用 {space.UserAvailableBytes / 1073741824d:F1} GiB，共 {space.TotalBytes / 1073741824d:F1} GiB；{space.SampledAt.ToLocalTime():HH:mm:ss} 测量。整卷容量不等于盘点覆盖。";
            DiskUsage.Visibility = space is null ? Visibility.Collapsed : Visibility.Visible;
            if (space is not null) DiskUsage.Value = space.TotalBytes == 0 ? 0 : 100d * (space.TotalBytes - space.UserAvailableBytes) / space.TotalBytes;
            var volume = selectedVolume;
            var totals = await Task.Run(() => workspace.Store.CurrentTotals(volume, keys));
            if (workspace.Stopping || request != updateRevision || !keys.SequenceEqual(workspace.CurrentObservationKeys)) return;
            Logical.Text = IndexedFile.FormatBytes(totals.Logical); Allocated.Text = IndexedFile.FormatBytes(totals.Allocated);
            Growth.Text = totals.Unknown > 0 ? "未知" : (totals.Growth > 0 ? "+" : "") + IndexedFile.FormatBytes(totals.Growth);
            var projects = workspace.Projects.Where(p => selectedVolume.Equals(Path.GetPathRoot(p.Root), StringComparison.OrdinalIgnoreCase)).ToArray();
            Coverage.Text = $"{selectedVolume} 已登记范围最后索引（可能过期）：{totals.Files:N0} 条路径，{totals.Unknown:N0} 个未知对象；暂停 {projects.Count(p => p.Paused)} 根。全机共 {workspace.Projects.Count} 个物理登记根。已移除及旧修订仅留历史，不计此处；不是全盘覆盖或可释放量。";
        }
        catch (Exception error) { if (request == updateRevision) Coverage.Text = "统计不可用，请重试： " + error.Message; }
        finally { pendingReads.Remove(completion.Task); completion.TrySetResult(); }
    }
    private async void VolumeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingVolumes || VolumeChoice.SelectedItem is not VolumeChoiceItem selected) return;
        selectedVolume = selected.Root; ShowScope(selectedVolume); await Update();
    }
    private long SurveyScopeRevision(string root) => workspace.Projects.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase))?.ScopeRevision ?? 1;
    private void SurveyRootChanged(object sender, TextChangedEventArgs e)
    {
        if (workspace is null || workspace.Stopping || Results is null || scanning is not null || loadingStoredSummary) return;
        scopeGeneration++; updateRevision++;
        survey?.Dispose(); survey = null; result = null; storedResult = storedFoldersTruncated = false; page = 0;
        try
        {
            var volume = Path.GetPathRoot(Path.GetFullPath(ScanRoot.Text));
            if (volume is not null && !volume.Equals(selectedVolume, StringComparison.OrdinalIgnoreCase))
            { selectedVolume = volume; if (IsLoaded) _ = Update(); }
        }
        catch (Exception) { }
        RefreshScopeChoices(); LoadStoredSummary();
        surveyClock = null; surveyPhase = result is null ? "盘点尚未开始" : "显示过期摘要，尚未开始本轮盘点"; UpdateScanTiming();
    }
    private void LoadStoredSummary()
    {
        if (workspace.Stopping || survey is not null || scanning is not null || loadingStoredSummary) return;
        loadingStoredSummary = true;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ScanRoot.Text));
            var summary = workspace.Store.LoadInventorySummary(root, SurveyScopeRevision(root));
            result = summary?.ToSurveyResult(); storedResult = result is not null; storedFoldersTruncated = summary?.FoldersTruncated == true;
            displayScopeRevision = summary?.ScopeRevision ?? 1;
            var registration = workspace.Projects.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
            surveyRegistrationId = registration?.Id; surveyScopeRevision = displayScopeRevision;
            ShowResults();
        }
        catch (Exception error)
        {
            result = null; ShowResults(); ScanStatus.Text = "盘点摘要不可用： " + error.Message + "。请确认路径后重试。";
        }
        finally { loadingStoredSummary = false; }
    }
    private void Browse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择只读盘点目录" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ShowScope(dialog.FolderName);
    }
    private async void Scan(object sender, RoutedEventArgs e) => await RunSurvey();
    private async Task RunSurvey()
    {
        if (workspace.Stopping || scanning is not null) return;
        using var cancellation = new CancellationTokenSource(); scanning = cancellation;
        var completion = pendingSurvey = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = cancellation.Token; var request = scopeGeneration;
        surveyClock = Stopwatch.StartNew(); surveyPhase = "读取文件元数据"; scanClockTimer.Start(); UpdateScanTiming();
        ScanButton.IsEnabled = ScanRoot.IsEnabled = BrowseButton.IsEnabled = VolumeChoice.IsEnabled = ScopeChoice.IsEnabled = ProjectChoice.IsEnabled = false;
        CancelButton.IsEnabled = true; ScanProgress.Visibility = Visibility.Visible; SetSelection();
        bool Current() => !workspace.Stopping && request == scopeGeneration && pendingScope is null;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ScanRoot.Text));
            var registration = workspace.Projects.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
            var scopeRevision = registration?.ScopeRevision ?? 1;
            bool SameScope()
            {
                var now = workspace.Projects.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
                return now?.Id == registration?.Id && (now?.ScopeRevision ?? 1) == scopeRevision;
            }
            storedResult = storedFoldersTruncated = false;
            if (survey is null || !root.Equals(survey.Root, StringComparison.OrdinalIgnoreCase) || result?.HasMore != true || result.IsStale ||
                surveyRegistrationId != registration?.Id || surveyScopeRevision != scopeRevision)
            {
                survey?.Dispose(); survey = null; result = null; ShowResults();
                UpdateScanTiming();
                var created = await Task.Run(() => new SpaceSurvey(root), token);
                if (!Current() || !SameScope()) { created.Dispose(); return; }
                survey = created; surveyRegistrationId = registration?.Id; surveyScopeRevision = scopeRevision;
            }
            displayScopeRevision = scopeRevision;
            var timer = Stopwatch.StartNew();
            do
            {
                ScanStatus.Text = "正在只读盘点文件大小与用途线索，可以停止；未完成部分保持未知。";
                surveyPhase = "读取文件元数据"; UpdateScanTiming();
                var next = await Task.Run(() => survey.Next(token), token);
                token.ThrowIfCancellationRequested();
                if (!Current() || !SameScope()) return;
                result = next; ShowResults();
                surveyPhase = "保存显示摘要"; UpdateScanTiming();
                var saved = await Task.Run(() => workspace.Store.SaveInventorySummary(root, scopeRevision, next));
                token.ThrowIfCancellationRequested();
                if (!Current() || !SameScope()) return;
                if (!saved) throw new IOException("本次结果已显示，但有界摘要保存失败；重启不会冒充已保存。");
            } while (result.HasMore && timer.Elapsed < TimeSpan.FromSeconds(30));
            surveyPhase = result.HasMore ? "本轮预算结束，可继续" : "本轮盘点结束";
        }
        catch (OperationCanceledException)
        {
            surveyPhase = "已停止，未完成范围仍未知";
            if (Current())
            {
                if (survey is not null) { result = survey.Snapshot(); ShowResults(); }
                ScanStatus.Text += " 已停止；未完成范围仍未知，可以继续。";
            }
        }
        catch (Exception error) { surveyPhase = "失败或未完成"; if (Current()) ScanStatus.Text = "盘点失败或未完成： " + error.Message + "。已有部分结果仅供查阅，可缩小范围重试。"; }
        finally
        {
            scanning = null;
            surveyClock?.Stop(); scanClockTimer.Stop(); UpdateScanTiming();
            ScanButton.IsEnabled = ScanRoot.IsEnabled = BrowseButton.IsEnabled = VolumeChoice.IsEnabled = ScopeChoice.IsEnabled = ProjectChoice.IsEnabled = true;
            CancelButton.IsEnabled = false; ScanProgress.Visibility = Visibility.Collapsed;
            ScanLabel.Text = result is { HasMore: true, IsStale: false } ? "继续盘点" : result is null ? "开始盘点" : "重新盘点";
            SetSelection();
            if (!IsLoaded) { survey?.Dispose(); survey = null; }
            if (pendingScope is { } nextScope) { pendingScope = null; if (!workspace.Stopping) ShowScope(nextScope); }
            completion.TrySetResult();
        }
    }
    private void Cancel(object sender, RoutedEventArgs e) => CancelPendingOperations();
    private IEnumerable<SpaceFolder> ToolRows()
    {
        var rows = result?.Folders.AsEnumerable() ?? [];
        if (ToolFilter.SelectedIndex > 0 && ToolFilter.SelectedItem is string tool)
        {
            var roots = workspace.Projects.Where(p => p.Tool.Equals(tool, StringComparison.OrdinalIgnoreCase)).Select(p => p.Root).ToArray();
            rows = rows.Where(f => roots.Any(r => ProtectionPolicy.Contains(r, f.Path)));
        }
        return rows;
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Results is null || updatingFilters) return;
        page = 0; ShowResults();
    }
    private string CoverageState() => result is null ? "未盘点" : storedResult || result.IsStale ? "过期快照，需刷新" :
        result.HasMore || result.Limited || result.Skipped > 0 || result.UnknownAllocationObjects > 0 || result.IdentityBudgetReached ? "部分覆盖 / 计量有未知" : "本轮可读范围已结束";
    private CategoryChoice Category(ContentPurpose? purpose, string label, SpaceFolder[] rows) => new(purpose, $"{label}（{rows.Length:N0}）",
        result is null ? "逻辑量：未盘点" : $"目录线索逻辑合计 {IndexedFile.FormatBytes(rows.Sum(f => f.Bytes))}（重叠未去重）",
        "规则资格：未核验，见对象说明；根覆盖：" + CoverageState());
    private void ShowResults()
    {
        if (Filter is null || Results is null) return;
        ScopeFilters.Header = $"范围 / 项目 / 工具筛选 · {ToolFilter.SelectedItem ?? "全部工具"}";
        var purpose = (Filter.SelectedItem as CategoryChoice)?.Purpose;
        var all = ToolRows().ToArray();
        updatingFilters = true;
        try
        {
            var categories = new[] { Category(null, "全部用途", all) }.Concat(Enum.GetValues<ContentPurpose>().Select(p =>
                Category(p, PurposeLabel(p), all.Where(f => f.Explanation.Purpose == p).ToArray()))).ToArray();
            Filter.ItemsSource = categories; Filter.SelectedItem = categories.First(c => c.Purpose == purpose);
        }
        finally { updatingFilters = false; }
        var rows = all.Where(f => purpose is null || f.Explanation.Purpose == purpose).OrderByDescending(f => f.Bytes).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        page = Math.Clamp(page, 0, Math.Max(0, (rows.Length - 1) / PageSize));
        var selected = (Results.SelectedItem as SpaceFolder)?.Path;
        var visible = rows.Skip(page * PageSize).Take(PageSize).ToArray();
        Results.ItemsSource = visible; Results.SelectedItem = visible.FirstOrDefault(f => f.Path.Equals(selected, StringComparison.OrdinalIgnoreCase));
        PreviousButton.IsEnabled = page > 0; NextButton.IsEnabled = (page + 1) * PageSize < rows.Length;
        PageStatus.Text = rows.Length == 0 ? "0 条目录线索" : $"第 {page + 1} / {(rows.Length + PageSize - 1) / PageSize} 页 · 共 {rows.Length:N0} 条目录线索";
        if (result is null)
        {
            ListStatus.Text = "未盘点：没有结果不代表范围为空";
            ScanStatus.Text = "此范围尚无盘点摘要；确认路径后开始只读盘点。";
        }
        else
        {
            ListStatus.Text = rows.Length > 0 ? "目录用途线索 · 大小包含子项，请逐项查看" :
                all.Length == 0 ? "无匹配目录：可能是工具筛选不匹配或本轮未取得目录" : "此用途分类无匹配目录；调整分类可继续查看";
            var state = storedResult || result.IsStale ? $"过期快照 {result.CapturedAt.ToLocalTime():MM-dd HH:mm:ss}，需重新盘点" :
                result.Limited ? "部分结果：达到目录上限，请缩小范围" : result.HasMore ? "部分结果：可以继续盘点" :
                result.Skipped > 0 ? "本轮结束，但存在跳过或不可读项" : result.Files == 0 ? "本轮可读范围确实未发现文件" : "本轮盘点结束";
            ScanStatus.Text = $"{result.Root}（盘点范围 v{displayScopeRevision}）；{state}。{result.Files:N0} 文件，逐路径逻辑 {IndexedFile.FormatBytes(result.Bytes)}；根内已核验去重逻辑 {IndexedFile.FormatBytes(result.KnownUniqueLogicalBytes)} / 分配 {IndexedFile.FormatBytes(result.KnownAllocatedBytes)}，分配未知 {result.UnknownAllocationObjects:N0}，跳过 {result.Skipped:N0} 项。" +
                (storedFoldersTruncated ? " 保存摘要的目录线索已截断，重新盘点可读取更多。" : "") +
                " 父子目录不能相加；上述均不是可释放承诺。";
        }
        SetSelection();
    }
    private static string PurposeLabel(ContentPurpose purpose) => purpose switch
    {
        ContentPurpose.WorkProduct => "源码与工作成果", ContentPurpose.Configuration => "配置与凭据", ContentPurpose.History => "Git / Agent 历史",
        ContentPurpose.Dependencies => "已安装依赖", ContentPurpose.BuildOutput => "疑似构建产物", ContentPurpose.DownloadCache => "下载缓存线索",
        ContentPurpose.LogsAndDumps => "日志与故障转储", ContentPurpose.TestSamples => "测试样本（待核验）", ContentPurpose.ToolRuntime => "工具运行资源",
        ContentPurpose.Mixed => "混合用途", _ => "尚未识别"
    };
    private void PreviousPage(object sender, RoutedEventArgs e) { page--; ShowResults(); }
    private void NextPage(object sender, RoutedEventArgs e) { page++; ShowResults(); }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => SetSelection();
    private void SetSelection()
    {
        if (OpenFolderButton is null) return;
        var selected = Results.SelectedItem as SpaceFolder;
        OpenFolderButton.IsEnabled = selected is not null; DrillButton.IsEnabled = selected is not null && scanning is null;
        ViewFilesButton.IsEnabled = selected is not null;
        RegisterButton.IsEnabled = selected is not null && scanning is null;
        var explanation = selected?.Explanation;
        DetailTitle.Text = explanation?.Title ?? "用途与影响";
        SelectedPath.Text = selected?.Path ?? "选择目录后显示完整路径，可直接复制。";
        PurposeText.Text = explanation?.Use ?? "选择一条目录线索，查看内容的用途；查看不代表清理选择。";
        AssociationText.Text = explanation is null ? "尚未选择对象；具体项目、消费者与来源不作推断。" :
            explanation.Association + "\n依据：" + explanation.Basis + "\n证据：" + (explanation.Evidence switch
            { ContentEvidence.KnownScope => "已知范围，仍需逐项核验", ContentEvidence.ProjectMarker => "项目标记线索", ContentEvidence.PathHint => "路径 / 名称线索", _ => "未知" });
        ValueText.Text = explanation?.Value ?? "源码、配置、历史、报告和未知内容默认保留。";
        ImpactText.Text = explanation is null ? "处理影响和重建条件尚未核验，不能用目录名称代替判断。" : explanation.Impact + "\n重建条件：" + explanation.Rebuild;
        MeasurementText.Text = result is null ? "未盘点；容量与范围内文件计量尚未建立。" :
            $"本轮范围：{result.Root}；{(storedResult || result.IsStale ? "过期快照" : result.HasMore || result.Limited || result.Skipped > 0 ? "部分覆盖" : "本轮可读范围已结束")}。\n" +
            (selected is null ? "" : $"此目录逐路径逻辑 {IndexedFile.FormatBytes(selected.Bytes)}，包含子项；无单目录去重分配承诺。\n") +
            $"本轮根内已知去重分配 {IndexedFile.FormatBytes(result.KnownAllocatedBytes)}，未知对象 {result.UnknownAllocationObjects:N0}。{result.AllocationBasis} 父子目录不能相加，也不是可释放量。";
        Advice.Text = explanation is null ? "当前没有可执行清单：未盘点、证据不足、无适用规则和本轮为空是不同情况。先盘点、查看依据或管理范围。" :
            explanation.Qualification + "\n" + explanation.NextStep + "\n当前只能查看用途、文件索引与盘点结果；从此页保留对象及确认清理的功能尚不可用。";
        if (selected?.Path.Equals(ContentClassification.BenchmarkRoot, StringComparison.OrdinalIgnoreCase) == true)
            MeasurementText.Text += "\n历史盘点参考：100,002 个文件；100,000 个生成样本逻辑 20 GiB、已观察分配约 20.218 GiB；另有 index.db 约 124.719 MiB 与 19 B 标记。父范围约 20.340 GiB 不代表当前值或可释放承诺；报告、复现资料、保留项与未知项保留。";
    }
    private async void Drill(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not SpaceFolder selected || scanning is not null) return;
        ShowScope(selected.Path); await RunSurvey();
    }
    private void Register(object sender, RoutedEventArgs e) { if (Results.SelectedItem is SpaceFolder selected) ManageScopeRequested?.Invoke(selected.Path); }
    private void ManageCurrentScope(object sender, RoutedEventArgs e) => ManageScopeRequested?.Invoke(ScanRoot.Text);
    private async void OpenCurrentFiles(object sender, RoutedEventArgs e) => await ShowFiles(ScanRoot.Text);
    private async void OpenSelectedFiles(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is SpaceFolder selected) await ShowFiles(selected.Path);
    }
    private async Task ShowFiles(string root)
    {
        if (workspace.Stopping) return;
        try
        {
            files ??= new(workspace, readOnly: true); FilesContent.Content = files;
            overviewOffset = OverviewScroll.VerticalOffset;
            OverviewScroll.Visibility = Visibility.Collapsed; FilesPanel.Visibility = Visibility.Visible;
            await files.ShowDirectory(root);
        }
        catch (Exception error) { BackFromFiles(this, new()); Advice.Text = "文件索引未打开：" + error.Message; }
    }
    private void BackFromFiles(object sender, RoutedEventArgs e)
    {
        files?.CancelPendingOperations(); FilesPanel.Visibility = Visibility.Collapsed; OverviewScroll.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => OverviewScroll.ScrollToVerticalOffset(overviewOffset)));
    }
    private void UpdateScanTiming()
    {
        var elapsed = surveyClock?.Elapsed ?? TimeSpan.Zero;
        ScanTiming.Text = $"{surveyPhase} · 已耗时 {elapsed.TotalSeconds:F1} 秒；本轮已读 {result?.Files ?? 0:N0} 文件、跳过 {result?.Skipped ?? 0:N0} 项；" +
            (scanning is not null || result?.HasMore == true ? "未检查数量未知，不显示虚假百分比。" : "未读取范围以覆盖说明为准。");
    }
    private void KeepReadingPosition(object sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject is UIElement target ? !target.IsKeyboardFocusWithin : !OverviewScroll.IsKeyboardFocusWithin) e.Handled = true;
    }
    private void OpenFolder(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is SpaceFolder selected)
            Launch(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                { UseShellExecute = true, ArgumentList = { selected.Path } });
    }
    private void OpenStorage(object sender, RoutedEventArgs e) => Launch(new("ms-settings:storagesense") { UseShellExecute = true });
    private void OpenDiskCleanup(object sender, RoutedEventArgs e)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "cleanmgr.exe");
        if (!File.Exists(executable)) { OpenStorage(sender, e); return; }
        Launch(new(executable) { UseShellExecute = true, ArgumentList = { "/d", selectedVolume[..1] } });
    }
    private void Launch(ProcessStartInfo info)
    {
        if (workspace.Stopping) return;
        try { Process.Start(info)?.Dispose(); Advice.Text = "已打开独立窗口；Windows 系统工具请在其窗口中核对和确认。CClean 没有执行删除。"; }
        catch (Exception error) { Advice.Text = "无法打开： " + error.Message; }
    }
    private void LayoutChanged(object sender, SizeChangedEventArgs e)
    {
        if (ClassificationLayout is null) return;
        var narrow = ActualWidth < 760;
        CategoryColumn.Width = narrow ? new GridLength(0) : new GridLength(190);
        Grid.SetColumn(CategoryPanel, narrow ? 1 : 0); Grid.SetRow(CategoryPanel, 0);
        Grid.SetRow(ContentPanel, narrow ? 1 : 0); Filter.MaxHeight = narrow ? 180 : 440;
        var sideBySide = ActualWidth >= 900;
        DetailColumn.Width = new GridLength(sideBySide ? 350 : 0);
        Grid.SetColumn(DetailCard, sideBySide ? 1 : 0); Grid.SetRow(DetailCard, sideBySide ? 0 : 1);
        DetailCard.Margin = sideBySide ? new Thickness(12, 0, 0, 0) : new Thickness(0, 4, 0, 0);
    }
}
