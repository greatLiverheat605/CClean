using System.Collections.ObjectModel;
using System.IO;
using CClean.Core.Monitoring;
using CClean.Core.Projects;
using CClean.Core.Files;
using CClean.Core.Storage;

namespace CClean.App.ViewModels;

public sealed class WorkspaceViewModel : IDisposable
{
    private readonly System.Windows.Threading.DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<Guid, string> errors = [];
    private bool disposed;
    private bool saving;
    private bool collectionStopped;
    private DateTimeOffset nextCalibration = DateTimeOffset.UtcNow.AddSeconds(30);
    internal Action? BeforeCalibrationRescan { get; set; }
    private int calibrationIndex;
    private readonly Dictionary<Guid, long> revisions = [];
    private readonly Dictionary<Guid, long> recordedGaps = [];
    private readonly Dictionary<Guid, IReadOnlyDictionary<string, FileSnapshot>> previous = [];
    internal (int Files, bool SharesCurrent) SnapshotRetention(Guid id) =>
        previous.TryGetValue(id, out var files) ? (files.Count,
            Monitors.TryGetValue(id, out var monitor) && ReferenceEquals(files, monitor.Snapshot.Files)) : (0, false);
    private readonly Dictionary<Guid, string> observationKeys = [];
    private readonly Dictionary<Guid, long> monitorGenerations = [];
    private readonly Dictionary<Guid, Task> starting = [];
    private readonly HashSet<DirectoryMonitor> retiringMonitors = [];
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private Task discoveryTask = Task.CompletedTask;
    private Task statusTask = Task.CompletedTask;
    private Task budgetTask = Task.CompletedTask;
    private Task observationTask = Task.CompletedTask;
    private Task? shutdownTask;
    private long observationGeneration;
    public bool Stopping { get; private set; }
    public bool ObservationPaused { get; private set; }
    internal Func<Task>? BeforeMonitorStart { get; set; }
    internal Func<Guid, Task>? BeforeSnapshotSave { get; set; }
    // Rule-specific D-13 qualification is pending; ordinary UI entries cannot opt in yet.
    internal bool ObjectReviewForIsolatedValidation { get; init; }
    public LocalStore Store { get; }
    public Core.Sessions.SessionRegistry Sessions { get; }
    private readonly Core.Sessions.SessionDiscovery sessionDiscovery = new();
    private readonly CancellationTokenSource discoveryClosing = new();
    private readonly System.Windows.Threading.DispatcherTimer discoveryTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    public IReadOnlyList<Core.Sessions.DiscoveredSession> DiscoveredSessions { get; private set; } = [];
    public string DiscoveryStatus { get; private set; } = "正在发现本机项目会话…";
    public event Action? SessionsDiscovered;
    public Task DiscoverSessions()
    {
        if (Stopping || disposed) return Task.CompletedTask;
        return discoveryTask.IsCompleted ? discoveryTask = DiscoverSessionsCore() : discoveryTask;
    }
    private async Task DiscoverSessionsCore()
    {
        try
        {
            var result = await Task.Run(() => sessionDiscovery.Next(discoveryClosing.Token));
            if (Stopping || disposed) return;
            var changed = result.Status != DiscoveryStatus || !result.Sessions.SequenceEqual(DiscoveredSessions);
            DiscoveredSessions = result.Sessions; DiscoveryStatus = result.Status;
            discoveryTimer.Interval = TimeSpan.FromSeconds(result.HasMore ? 2 : 30);
            if (changed) SessionsDiscovered?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!Stopping && !disposed) { var status = "会话发现暂不可用，保留已知保护范围：" + error.Message;
            if (status != DiscoveryStatus) { DiscoveryStatus = status; SessionsDiscovered?.Invoke(); } } }
    }
    public Core.Cleanup.RuleCatalog Rules { get; }
    private Core.Cleanup.CleanupJournal? cleanupJournal;
    private Core.Cleanup.CleanupExecutor? cleanupExecutor;
    private CancellationTokenSource? cleanupCancellation;
    public Task<Core.Cleanup.CleanupResult>? CleanupTask { get; private set; }
    internal Task<Core.Cleanup.CleanupPlan>? CleanupPreviewTask { get; private set; }
    public bool CleanupRunning => CleanupTask is { IsCompleted: false };
    public bool CleanupBusy => CleanupRunning || CleanupPreviewTask is { IsCompleted: false };
    public Core.Cleanup.CleanupJournal OpenCleanupJournal()
    {
        ThrowIfStopping();
        return cleanupJournal ??= new(DataDirectory);
    }
    public Task<Core.Cleanup.CleanupResult> ExecuteCleanup(Core.Cleanup.CleanupPlan plan, Func<Task<Core.Cleanup.CleanupContext>>? context = null)
    {
        ThrowIfStopping();
        if (CleanupBusy) throw new InvalidOperationException("已有清理或跨批预览进行中。");
        cleanupExecutor ??= new(OpenCleanupJournal()); cleanupCancellation?.Dispose(); cleanupCancellation = new();
        return CleanupTask = cleanupExecutor.ExecuteApproved(plan, context ?? CleanupContext, cleanupCancellation.Token);
    }
    // Qualification-only entry: ordinary UI cannot use unqualified rules to continue a batch.
    internal Task<Core.Cleanup.CleanupPlan> PreviewNextCleanup(IEnumerable<string> paths,
        Func<Task<Core.Cleanup.CleanupContext>>? context = null)
    {
        ThrowIfStopping();
        if (!ObjectReviewForIsolatedValidation) throw new InvalidOperationException("跨批对象契约尚未完成规则验收。");
        if (CleanupBusy) throw new InvalidOperationException("已有清理或跨批预览进行中。");
        if (cleanupExecutor is null) throw new InvalidOperationException("尚无本进程的完整成功清理证据。");
        cleanupCancellation?.Dispose(); cleanupCancellation = new();
        return CleanupPreviewTask = cleanupExecutor.PreviewNext(paths, context ?? CleanupContext, cleanupCancellation.Token);
    }
    public void CancelCleanup()
    {
        try { cleanupCancellation?.Cancel(); }
        catch (ObjectDisposedException) { } // Repeated exit/cancel requests are harmless after drain.
    }
    public event Action? Indexed;
    public string DataDirectory { get; } = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CClean");
    public ProjectRegistry Registry { get; }
    public ObservableCollection<RegisteredProject> Projects { get; } = [];
    public Dictionary<Guid, DirectoryMonitor> Monitors { get; } = [];
    public string Status { get; private set; } = "尚未登记目录。";
    public event Action? Changed;
    public WorkspaceViewModel(bool enableSessionDiscovery = true)
    {
        Registry = new(DataDirectory);
        Rules = new(DataDirectory);
        Store = new(DataDirectory);
        try { Sessions = new(DataDirectory, Registry.Projects); } catch { Store.Dispose(); throw; }
        foreach (var project in Registry.Projects) Projects.Add(project);
        statusTimer.Tick += (_, _) =>
        {
            if (statusTask.IsCompleted) statusTask = RefreshStatus();
        };
        statusTimer.Start();
        discoveryTimer.Tick += (_, _) => { _ = DiscoverSessions(); };
        if (enableSessionDiscovery) discoveryTimer.Start();
    }

    private async Task RefreshStatus()
    {
        if (Projects.Count == 0 || collectionStopped || ObservationPaused || !observationTask.IsCompleted || Stopping || disposed) return;
        if (Monitors.Count > 0 && DateTimeOffset.UtcNow >= nextCalibration)
        {
            var collectors = Monitors.Values.ToArray();
            BeforeCalibrationRescan?.Invoke();
            collectors[calibrationIndex++ % collectors.Length].Rescan();
            nextCalibration = DateTimeOffset.UtcNow.AddMinutes(15d / collectors.Length);
        }
        var ready = Monitors.Values.Count(m => m.Revision > 0);
        var reliable = Monitors.Count(entry => monitorGenerations.GetValueOrDefault(entry.Key) == observationGeneration && entry.Value.HasContinuousObservation);
        var gaps = Monitors.Values.Sum(m => m.NativeGaps + m.ApplicationGaps + m.ScanGaps);
        var measured = ready == 0 ? "尚未取得当前文件数据" : $"已知 {Monitors.Values.Sum(m => m.Snapshot.Files.Count)} 个文件";
        Status = $"可靠观察 {reliable}/{Projects.Count} 个登记根；{measured}；缺口提示 {gaps}；暂停 {Projects.Count(p => p.Paused)}。仅代表登记范围。";
        if (ready > reliable) Status += " 观察不完整的范围保持清理保护，可暂停后恢复监测。";
        if (errors.Count > 0) Status += " 不可访问：" + errors.Values.First();
        Changed?.Invoke();
        if (saving || ObservationPaused || Stopping || disposed) return;
        saving = true;
        try
        {
            // Capture the history key with its watcher before any await. A root can change while another root saves.
            var batch = Monitors.Select(entry => (Id: entry.Key, Monitor: entry.Value,
                ObservationKey: observationKeys.GetValueOrDefault(entry.Key))).ToArray();
            foreach (var entry in batch)
            {
                if (BeforeSnapshotSave is not null) await BeforeSnapshotSave(entry.Id);
                var observationKey = entry.ObservationKey;
                if (Stopping || ObservationPaused || disposed || observationKey is null || !Monitors.TryGetValue(entry.Id, out var live) ||
                    !ReferenceEquals(live, entry.Monitor) || observationKeys.GetValueOrDefault(entry.Id) != observationKey) continue;
                var revision = entry.Monitor.Revision;
                if (revision == 0 || revisions.GetValueOrDefault(entry.Id) == revision) continue;
                var snapshot = entry.Monitor.Snapshot;
                var gapsToRecord = entry.Monitor.NativeGaps + entry.Monitor.ApplicationGaps + entry.Monitor.ScanGaps;
                previous.TryGetValue(entry.Id, out var old);
                var lastRecordedGaps = recordedGaps.GetValueOrDefault(entry.Id);
                var saved = await Task.Run(() =>
                {
                    if (gapsToRecord != lastRecordedGaps)
                        Store.RecordGap(observationKey, $"原生/队列/扫描累计缺口 {gapsToRecord}；补扫只恢复当前状态。", snapshot.CapturedAt);
                    old ??= Store.LoadSnapshots(observationKey);
                    var success = Store.SaveSnapshot(observationKey, snapshot, old);
                    var persisted = success && snapshot.Gaps.Count == 0 ? snapshot.FilesByPath :
                        success ? Store.LoadSnapshots(observationKey) : old;
                    return (Success: success, Previous: persisted);
                });
                if (Stopping || disposed) break;
                if (saved.Success)
                {
                    if (Monitors.TryGetValue(entry.Id, out var current) && ReferenceEquals(current, entry.Monitor) &&
                        observationKeys.GetValueOrDefault(entry.Id) == observationKey)
                    {
                        previous[entry.Id] = saved.Previous;
                        recordedGaps[entry.Id] = gapsToRecord;
                        revisions[entry.Id] = revision;
                    }
                    Indexed?.Invoke();
                }
                else
                {
                    collectionStopped = true;
                    Status = "空间或自身预算不足，已停止扩展监测和历史落盘；已有索引仍可查看。";
                    foreach (var id in Monitors.Keys.ToArray()) await StopMonitor(id);
                    previous.Clear(); if (!Stopping) Changed?.Invoke(); break;
                }
            }
        }
        catch (Exception error)
        {
            if (!Stopping && !disposed)
            {
                collectionStopped = true;
                Status = "索引不可写，已停止采集：" + error.Message;
                foreach (var id in Monitors.Keys.ToArray()) await StopMonitor(id);
                previous.Clear(); if (!Stopping) Changed?.Invoke();
            }
        }
        finally { saving = false; }
    }

    public async Task Resume()
    {
        if (Stopping || disposed || ObservationPaused) return;
        var generation = observationGeneration;
        await DiscoverSessions();
        if (Stopping || disposed || ObservationPaused || generation != observationGeneration) return;
        foreach (var project in Projects.Where(p => !p.Paused).ToArray()) await Start(project);
    }
    public Core.Sessions.ActivitySnapshot Activity() => ActivityCore(includeDirectoryChanges: true);
    private Core.Sessions.ActivitySnapshot ActivityCore(bool includeDirectoryChanges)
    {
        bool Known(string root) => !Stopping && !ObservationPaused && !disposed && Projects.Any(p =>
            (root.Equals(p.Root, StringComparison.OrdinalIgnoreCase) || root.StartsWith(p.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) &&
            Monitors.TryGetValue(p.Id, out var m) && monitorGenerations.GetValueOrDefault(p.Id) == observationGeneration &&
            observationKeys.GetValueOrDefault(p.Id) == p.ObservationKey && m.HasContinuousObservation);
        var snapshot = Sessions.Activity(Projects, Known);
        var scopes = snapshot.Scopes.Select(s => includeDirectoryChanges && Projects.Any(p => ProjectRegistry.Overlaps(p.Root, s.Root) &&
            Monitors.TryGetValue(p.Id, out var m) && m.LastActivity > DateTimeOffset.UtcNow.AddMinutes(-30))
                ? s with { Active = true, Reason = "近期目录发生变化，保持活动保护" } : s).ToArray();
        // Bind approval to the current registration and physical-root revision, including remove/re-add.
        scopes = scopes.Select(s => s with { Reason = s.Reason + $"；观察代次 {observationGeneration}；登记 " + string.Join(",", Projects
            .Where(p => ProjectRegistry.Overlaps(p.Root, s.Root)).Select(p => p.ObservationKey).Order()) }).ToArray();
        return snapshot with { Scopes = scopes.Concat(DiscoveredSessions.Select(s =>
            new Core.Sessions.ActivityScope(s.ProjectRoot, false, false, "工具会话自动发现；运行状态未知，保持保护"))).ToArray() };
    }
    public async Task<Core.Cleanup.CleanupContext> CleanupContext()
    {
        var rules = Rules.Scopes; var version = Rules.ConfigurationVersion;
        var consumers = await Task.Run(() => Core.Cleanup.CleanupConsumers.Observe(rules));
        if (version != Rules.ConfigurationVersion) throw new InvalidOperationException("消费者检查期间规则变化，请重新预览。");
        var observations = ObjectReviewForIsolatedValidation
            ? Monitors.Values.Select(m => new Core.Cleanup.CleanupObservation(m, m.Observe())).ToArray() : [];
        var activity = Activity();
        // Process absence cannot replace registration/monitor coverage.
        var observed = rules.Select(r => new Core.Sessions.ActivityScope(r.Root, false,
            activity.Scopes.Any(s => Core.Cleanup.ProtectionPolicy.Contains(s.Root, r.Root) && s.ObservationKnown), "登记范围覆盖检查"));
        var context = new Core.Cleanup.CleanupContext(version, rules,
            activity with { Scopes = activity.Scopes.Concat(consumers).Concat(observed).ToArray() }, [DataDirectory]);
        if (!ObjectReviewForIsolatedValidation) return context;
        var independent = ActivityCore(includeDirectoryChanges: false);
        return context with
        {
            Contract = Core.Cleanup.CleanupContract.ObjectReviewV1,
            IndependentActivity = independent with { Scopes = independent.Scopes.Concat(consumers).Concat(observed).ToArray() },
            Observations = observations
        };
    }
    public Task EnforceBudget()
    {
        if (Stopping || disposed || collectionStopped) return Task.CompletedTask;
        return budgetTask.IsCompleted ? budgetTask = EnforceBudgetCore() : budgetTask;
    }
    private async Task EnforceBudgetCore()
    {
        try
        {
            var decision = await Task.Run(() =>
            {
                var space = VolumeSpaceReader.Read(DataDirectory);
                return RuntimeBudget.Evaluate(RuntimeBudget.Measure(DataDirectory), (long)space.UserAvailableBytes, true, 0, (long)space.TotalBytes);
            });
            if (Stopping || disposed || decision.Collect) return;
            collectionStopped = true;
            foreach (var id in Monitors.Keys.ToArray()) await StopMonitor(id);
            previous.Clear();
            Status = "空间或自身预算不足，采集已暂停；已有索引可能过期，容量查询仍可用。";
            if (!Stopping) Changed?.Invoke();
        }
        catch (Exception error) { if (!Stopping && !disposed) { Status = "预算状态无法读取：" + error.Message; Changed?.Invoke(); } }
    }
    private async Task Start(RegisteredProject project)
    {
        var generation = observationGeneration;
        if (Stopping || ObservationPaused || disposed) return;
        while (starting.TryGetValue(project.Id, out var pending))
        {
            await pending;
            if (starting.GetValueOrDefault(project.Id) == pending) starting.Remove(project.Id);
        }
        if (!Registry.IsCurrent(project.Id, project.Revision) || project.Paused) return;
        if (collectionStopped || Stopping || ObservationPaused || disposed || generation != observationGeneration || Monitors.ContainsKey(project.Id)) return;
        var task = StartCore(project, generation);
        starting[project.Id] = task;
        try { await task; }
        finally { if (starting.GetValueOrDefault(project.Id) == task) starting.Remove(project.Id); }
    }
    private async Task StartCore(RegisteredProject project, long generation)
    {
        try
        {
            if (BeforeMonitorStart is not null) await BeforeMonitorStart();
            if (Stopping || ObservationPaused || disposed || generation != observationGeneration) return;
            var started = await Task.Run(() =>
            {
                Store.RecordGap(project.ObservationKey, $"范围 v{project.ScopeRevision}：{project.Root}；开始或恢复观察，此前离线区间没有完整历史。");
                var baseline = Store.LoadSnapshots(project.ObservationKey);
                return (Monitor: new DirectoryMonitor(project.Root, absolutePaths: true, initialFiles: baseline), Baseline: baseline);
            });
            if (Stopping || ObservationPaused || disposed || generation != observationGeneration || collectionStopped || !Registry.IsCurrent(project.Id, project.Revision) ||
                !Projects.Any(p => p.Id == project.Id && p.Revision == project.Revision && !p.Paused) || Monitors.ContainsKey(project.Id))
            { await RetireMonitor(started.Monitor); return; }
            previous[project.Id] = started.Baseline;
            Monitors.Add(project.Id, started.Monitor);
            observationKeys[project.Id] = project.ObservationKey;
            monitorGenerations[project.Id] = generation;
            revisions.Remove(project.Id);
            errors.Remove(project.Id);
            Status = "正在建立基线；历史仅从本次观察开始。";
        }
        catch (Exception error)
        {
            if (!Stopping && !disposed && generation == observationGeneration && Registry.IsCurrent(project.Id, project.Revision)) { Status = project.Name + "：" + error.Message; errors[project.Id] = Status; }
        }
        if (!Stopping && !disposed && generation == observationGeneration) Changed?.Invoke();
    }
    public async Task Register(string name, string root, string tool)
    {
        ThrowIfStopping();
        RegisteredProject project;
        await configurationGate.WaitAsync();
        try
        {
            ThrowIfStopping();
            project = await Task.Run(() => Registry.Register(name, root, tool));
            Projects.Add(project);
        }
        finally { configurationGate.Release(); }
        await Start(project);
    }
    public async Task TogglePause(RegisteredProject project)
    {
        await ChangeRegistration(project, () => Registry.SetPaused(project.Id, !project.Paused, project.Revision));
    }
    public Task UpdateRegistration(RegisteredProject project, string name, string root, string tool) =>
        ChangeRegistration(project, () => Registry.Update(project.Id, name, root, tool, project.Revision));
    public Task LinkScope(RegisteredProject project, RegisteredProject scope, string path, string evidence) =>
        ChangeRegistration(project, () => Registry.LinkScope(project.Id, scope.Id, path, evidence, project.Revision));
    public Task UnlinkScope(RegisteredProject project, Guid scopeId, string path) =>
        ChangeRegistration(project, () => Registry.UnlinkScope(project.Id, scopeId, path, project.Revision));

    private async Task StopMonitor(Guid id)
    {
        observationKeys.Remove(id); monitorGenerations.Remove(id); previous.Remove(id); revisions.Remove(id); recordedGaps.Remove(id); errors.Remove(id);
        if (Monitors.Remove(id, out var monitor)) await RetireMonitor(monitor);
    }
    private async Task RetireMonitor(DirectoryMonitor monitor)
    {
        retiringMonitors.Add(monitor);
        await Task.Run(monitor.Dispose);
        retiringMonitors.Remove(monitor);
    }
    private async Task ChangeRegistration(RegisteredProject project, Func<RegisteredProject> change)
    {
        ThrowIfStopping();
        RegisteredProject updated;
        string? gapProblem = null;
        await configurationGate.WaitAsync();
        try
        {
            ThrowIfStopping();
            updated = await Task.Run(change); // Configuration must commit before UI or watcher state changes.
            var index = Projects.ToList().FindIndex(p => p.Id == project.Id);
            Projects[index] = updated;
            if (updated.Paused || updated.ScopeRevision != project.ScopeRevision) await StopMonitor(project.Id);
            try
            {
                await Task.Run(() => Store.RecordGap(project.ObservationKey,
                    updated.Paused ? "用户暂停观察，后续区间不可见。" : updated.ScopeRevision != project.ScopeRevision
                        ? $"此范围已调整为 v{updated.ScopeRevision}：{updated.Root}；旧范围历史保留，不迁移到新路径。" : "登记信息已更新，不补造观察历史。"));
            }
            catch (Exception error) { gapProblem = " 配置已保存，但历史缺口写入失败：" + error.Message; }
            Status = updated.Paused ? "已暂停；历史与原显式保护保留。" : "登记已保存；当前范围重新核验，不沿用旧删除批准。";
        }
        finally { configurationGate.Release(); }
        await Start(updated);
        if (gapProblem is not null) Status += gapProblem;
        if (!Stopping && !disposed) { Changed?.Invoke(); Indexed?.Invoke(); }
    }
    public async Task RemoveRegistration(RegisteredProject project)
    {
        ThrowIfStopping();
        var restart = new List<RegisteredProject>();
        await configurationGate.WaitAsync();
        try
        {
            ThrowIfStopping();
            await Task.Run(() => Registry.Remove(project.Id, project.Revision));
            foreach (var registered in Registry.Projects)
            {
                var index = Projects.ToList().FindIndex(p => p.Id == registered.Id);
                if (index >= 0)
                {
                    if (Projects[index].Revision != registered.Revision && !registered.Paused) restart.Add(registered);
                    Projects[index] = registered;
                }
            }
            var removed = Projects.FirstOrDefault(p => p.Id == project.Id);
            if (removed is not null) Projects.Remove(removed);
            await StopMonitor(project.Id);
            Status = "已移除监测；未删除文件，历史与原显式保护保留。";
            try { await Task.Run(() => Store.RecordGap(project.ObservationKey, $"已移除监测：{project.Root}（范围 v{project.ScopeRevision}）；历史保留，未删除文件，原规则与显式保护不变。")); }
            catch (Exception error) { Status += " 移除已保存，但历史缺口写入失败：" + error.Message; }
        }
        finally { configurationGate.Release(); }
        foreach (var registered in restart) await Start(registered);
        if (!Stopping && !disposed) { Changed?.Invoke(); Indexed?.Invoke(); }
    }
    public string? CurrentObservationKey(Guid id) => Projects.FirstOrDefault(p => p.Id == id)?.ObservationKey;
    public IReadOnlyList<string> CurrentObservationKeys => Projects.Select(p => p.ObservationKey).ToArray();
    public Task SetObservationPaused(bool paused)
    {
        ThrowIfStopping();
        if (ObservationPaused == paused) return observationTask;
        ObservationPaused = paused;
        Status = paused ? "正在暂停观察；当前清理资格保持保护。" : "正在恢复观察并重新建立基线；离线空窗不补造历史。";
        var task = observationTask = SetObservationPausedCore(paused, ++observationGeneration);
        Changed?.Invoke();
        return task;
    }

    private async Task SetObservationPausedCore(bool paused, long generation)
    {
        await configurationGate.WaitAsync();
        try
        {
            ThrowIfStopping();
            if (generation != observationGeneration) return;
            // A superseded pause may never have reached this gate. Resume also retires the old generation.
            foreach (var id in Monitors.Keys.ToArray()) await StopMonitor(id);
            if (paused)
            {
                foreach (var project in Projects.Where(p => !p.Paused).ToArray())
                {
                    try { await Task.Run(() => Store.RecordGap(project.ObservationKey, "托盘临时暂停观察，后续区间不可见；单根暂停设置与显式保护保留。")); }
                    catch (Exception error) { errors[project.Id] = "观察已暂停，但历史缺口写入失败：" + error.Message; }
                }
            }
        }
        finally { configurationGate.Release(); }
        if (paused) await Task.WhenAll(starting.Values.ToArray());
        if (Stopping || disposed || generation != observationGeneration) return;
        if (!paused) foreach (var project in Projects.Where(p => !p.Paused).ToArray()) await Start(project);
        if (Stopping || disposed || generation != observationGeneration) return;
        Status = paused ? "观察已临时暂停；历史、单根暂停设置与显式保护保留。" : "观察已恢复；仅启动未单独暂停的登记根，离线空窗不补造历史。";
        Changed?.Invoke(); Indexed?.Invoke();
    }

    private void ThrowIfStopping()
    {
        if (disposed) throw new ObjectDisposedException(nameof(WorkspaceViewModel));
        if (Stopping) throw new InvalidOperationException("CClean 正在安全退出，不能开始新的操作。");
    }

    public void BeginShutdown()
    {
        if (Stopping || disposed) return;
        Stopping = true;
        observationGeneration++;
        statusTimer.Stop(); discoveryTimer.Stop();
        discoveryClosing.Cancel(); CancelCleanup();
        Status = "正在安全退出：停止后续项，等待当前操作与审计完成。";
    }

    public Task ShutdownAsync()
    {
        if (disposed) return Task.CompletedTask;
        if (shutdownTask is { IsCompleted: false }) return shutdownTask;
        BeginShutdown();
        return shutdownTask = ShutdownCore();
    }

    private async Task ShutdownCore()
    {
        var failures = new List<Exception>();
        async Task Finish(Task? task)
        {
            if (task is null) return;
            try { await task; }
            catch (OperationCanceledException) when (task == CleanupPreviewTask && task.IsCanceled && cleanupCancellation?.IsCancellationRequested == true) { }
            catch (Exception error) { failures.Add(error); }
        }
        // The executor may still need sessions, watchers and the journal to finish its current item's audit.
        await Finish(CleanupTask);
        await Finish(CleanupPreviewTask);
        await Finish(Task.WhenAll(discoveryTask, statusTask, budgetTask, observationTask));
        await configurationGate.WaitAsync();
        configurationGate.Release();
        await Finish(Task.WhenAll(starting.Values.ToArray()));
        foreach (var id in Monitors.Keys.ToArray()) await Finish(StopMonitor(id));
        var releaseFailed = false;
        foreach (var monitor in retiringMonitors.ToArray())
        {
            try { await RetireMonitor(monitor); }
            catch (Exception error) { releaseFailed = true; failures.Add(error); }
        }
        void Release(Action release)
        {
            try { release(); }
            catch (Exception error) { releaseFailed = true; failures.Add(error); }
        }
        Release(sessionDiscovery.Dispose);
        Release(Store.Dispose);
        Release(Sessions.Dispose);
        Release(() => cleanupJournal?.Dispose());
        Release(() => cleanupCancellation?.Dispose());
        Release(discoveryClosing.Dispose);
        disposed = !releaseFailed;
        if (failures.Count > 0) throw new AggregateException(releaseFailed
            ? "退出收尾未全部完成，可重试退出；新的操作仍被拒绝。" : "退出收尾已完成，但部分操作报告失败。", failures);
    }

    // MainWindow explicitly awaits ShutdownAsync; IDisposable must never block the UI dispatcher.
    public void Dispose() => _ = ShutdownAsync();
}
