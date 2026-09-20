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
    private int calibrationIndex;
    private readonly Dictionary<Guid, long> revisions = [];
    private readonly Dictionary<Guid, long> recordedGaps = [];
    private readonly Dictionary<Guid, IReadOnlyDictionary<string, FileSnapshot>> previous = [];
    public LocalStore Store { get; }
    public Core.Sessions.SessionRegistry Sessions { get; }
    private readonly Core.Sessions.SessionDiscovery sessionDiscovery = new();
    private readonly CancellationTokenSource discoveryClosing = new();
    private readonly System.Windows.Threading.DispatcherTimer discoveryTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool discoveringSessions;
    public IReadOnlyList<Core.Sessions.DiscoveredSession> DiscoveredSessions { get; private set; } = [];
    public string DiscoveryStatus { get; private set; } = "正在发现本机项目会话…";
    public event Action? SessionsDiscovered;
    public async Task DiscoverSessions()
    {
        if (discoveringSessions || disposed) return;
        discoveringSessions = true;
        try
        {
            var result = await Task.Run(() => sessionDiscovery.Next(discoveryClosing.Token));
            if (disposed) return;
            DiscoveredSessions = result.Sessions; DiscoveryStatus = result.Status;
            discoveryTimer.Interval = TimeSpan.FromSeconds(result.HasMore ? 2 : 30);
            SessionsDiscovered?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!disposed) { DiscoveryStatus = "会话发现暂不可用，保留已知保护范围：" + error.Message; SessionsDiscovered?.Invoke(); } }
        finally { discoveringSessions = false; if (disposed) sessionDiscovery.Dispose(); }
    }
    public Core.Cleanup.RuleCatalog Rules { get; }
    private Core.Cleanup.CleanupJournal? cleanupJournal;
    private Core.Cleanup.CleanupExecutor? cleanupExecutor;
    private CancellationTokenSource? cleanupCancellation;
    public Task<Core.Cleanup.CleanupResult>? CleanupTask { get; private set; }
    public bool CleanupRunning => CleanupTask is { IsCompleted: false };
    public Core.Cleanup.CleanupJournal OpenCleanupJournal() => cleanupJournal ??= new(DataDirectory);
    public Task<Core.Cleanup.CleanupResult> ExecuteCleanup(Core.Cleanup.CleanupPlan plan, Func<Task<Core.Cleanup.CleanupContext>>? context = null)
    {
        if (CleanupRunning) throw new InvalidOperationException("已有清理任务执行中。");
        cleanupExecutor ??= new(OpenCleanupJournal()); cleanupCancellation?.Dispose(); cleanupCancellation = new();
        return CleanupTask = cleanupExecutor.ExecuteApproved(plan, context ?? CleanupContext, cleanupCancellation.Token);
    }
    public void CancelCleanup() => cleanupCancellation?.Cancel();
    public event Action? Indexed;
    public string DataDirectory { get; } = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CClean");
    public ProjectRegistry Registry { get; }
    public ObservableCollection<RegisteredProject> Projects { get; } = [];
    public Dictionary<Guid, DirectoryMonitor> Monitors { get; } = [];
    public string Status { get; private set; } = "尚未登记目录。";
    public event Action? Changed;
    public WorkspaceViewModel()
    {
        Registry = new(DataDirectory);
        Rules = new(DataDirectory);
        Store = new(DataDirectory);
        try { Sessions = new(DataDirectory); } catch { Store.Dispose(); throw; }
        foreach (var project in Registry.Projects) Projects.Add(project);
        statusTimer.Tick += async (_, _) =>
        {
            if (Projects.Count == 0 || collectionStopped || disposed) return;
            if (Monitors.Count > 0 && DateTimeOffset.UtcNow >= nextCalibration)
            {
                var collectors = Monitors.Values.ToArray();
                collectors[calibrationIndex++ % collectors.Length].Rescan();
                nextCalibration = DateTimeOffset.UtcNow.AddMinutes(15d / collectors.Length);
            }
            var ready = Monitors.Values.Count(m => m.Revision > 0);
            var reliable = Monitors.Values.Count(m => m.HasContinuousObservation);
            var gaps = Monitors.Values.Sum(m => m.NativeGaps + m.ApplicationGaps + m.ScanGaps);
            var measured = ready == 0 ? "尚未取得当前文件数据" : $"已知 {Monitors.Values.Sum(m => m.Snapshot.Files.Count)} 个文件";
            Status = $"可靠观察 {reliable}/{Projects.Count} 个登记根；{measured}；缺口提示 {gaps}；暂停 {Projects.Count(p => p.Paused)}。仅代表登记范围。";
            if (ready > reliable) Status += " 观察不完整的范围保持清理保护，可暂停后恢复监测。";
            if (errors.Count > 0) Status += " 不可访问：" + errors.Values.First();
            Changed?.Invoke();
            if (saving || disposed) return;
            saving = true;
            try
            {
                foreach (var entry in Monitors.ToArray())
                {
                    var revision = entry.Value.Revision;
                    if (revision == 0 || revisions.GetValueOrDefault(entry.Key) == revision) continue;
                    var snapshot = entry.Value.Snapshot;
                    var gapsToRecord = entry.Value.NativeGaps + entry.Value.ApplicationGaps + entry.Value.ScanGaps;
                    previous.TryGetValue(entry.Key, out var old);
                    var lastRecordedGaps = recordedGaps.GetValueOrDefault(entry.Key);
                    var saved = await Task.Run(() =>
                    {
                        if (gapsToRecord != lastRecordedGaps)
                            Store.RecordGap(entry.Key.ToString(), $"原生/队列/扫描累计缺口 {gapsToRecord}；补扫只恢复当前状态。", snapshot.CapturedAt);
                        old ??= Store.LoadSnapshots(entry.Key.ToString());
                        var success = Store.SaveSnapshot(entry.Key.ToString(), snapshot, old);
                        var persisted = success && snapshot.Gaps.Count == 0 ? snapshot.FilesByPath :
                            success ? Store.LoadSnapshots(entry.Key.ToString()) : old;
                        return (Success: success, Previous: persisted);
                    });
                    if (disposed) break;
                    if (saved.Success)
                    {
                        if (Monitors.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry.Value))
                        {
                            previous[entry.Key] = saved.Previous;
                            recordedGaps[entry.Key] = gapsToRecord;
                            revisions[entry.Key] = revision;
                        }
                        Indexed?.Invoke();
                    }
                    else
                    {
                        collectionStopped = true;
                        Status = "空间或自身预算不足，已停止扩展监测和历史落盘；已有索引仍可查看。";
                        foreach (var monitor in Monitors.Values.ToArray()) await Task.Run(monitor.Dispose);
                        Monitors.Clear(); previous.Clear(); Changed?.Invoke(); break;
                    }
                }
            }
            catch (Exception error)
            {
                if (!disposed)
                {
                    collectionStopped = true;
                    Status = "索引不可写，已停止采集：" + error.Message;
                    foreach (var monitor in Monitors.Values.ToArray()) await Task.Run(monitor.Dispose);
                    Monitors.Clear(); previous.Clear(); Changed?.Invoke();
                }
            }
            finally { saving = false; }
        };
        statusTimer.Start();
        discoveryTimer.Tick += async (_, _) => await DiscoverSessions();
        discoveryTimer.Start();
    }

    public async Task Resume()
    {
        await DiscoverSessions();
        foreach (var project in Projects.Where(p => !p.Paused).ToArray()) await Start(project);
    }
    public Core.Sessions.ActivitySnapshot Activity()
    {
        bool Known(string root) => Projects.Any(p =>
            (root.Equals(p.Root, StringComparison.OrdinalIgnoreCase) || root.StartsWith(p.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) &&
            Monitors.TryGetValue(p.Id, out var m) && m.HasContinuousObservation);
        var snapshot = Sessions.Activity(Projects, Known);
        var scopes = snapshot.Scopes.Select(s => Projects.Any(p => ProjectRegistry.Overlaps(p.Root, s.Root) &&
            Monitors.TryGetValue(p.Id, out var m) && m.LastActivity > DateTimeOffset.UtcNow.AddMinutes(-30))
                ? s with { Active = true, Reason = "近期目录发生变化，保持活动保护" } : s).ToArray();
        return snapshot with { Scopes = scopes.Concat(DiscoveredSessions.Select(s =>
            new Core.Sessions.ActivityScope(s.ProjectRoot, false, false, "工具会话自动发现；运行状态未知，保持保护"))).ToArray() };
    }
    public async Task<Core.Cleanup.CleanupContext> CleanupContext()
    {
        var rules = Rules.Scopes; var version = Rules.ConfigurationVersion; var activity = Activity();
        var consumers = await Task.Run(() => Core.Cleanup.CleanupConsumers.Observe(rules));
        // Process absence cannot replace registration/monitor coverage.
        var observed = rules.Select(r => new Core.Sessions.ActivityScope(r.Root, false,
            activity.Scopes.Any(s => Core.Cleanup.ProtectionPolicy.Contains(s.Root, r.Root) && s.ObservationKnown), "登记范围覆盖检查"));
        return new(version, rules, activity with { Scopes = activity.Scopes.Concat(consumers).Concat(observed).ToArray() }, [DataDirectory]);
    }
    public async Task EnforceBudget()
    {
        if (disposed || collectionStopped) return;
        try
        {
            var decision = await Task.Run(() =>
            {
                var space = VolumeSpaceReader.Read(DataDirectory);
                return RuntimeBudget.Evaluate(RuntimeBudget.Measure(DataDirectory), (long)space.UserAvailableBytes, true, 0, (long)space.TotalBytes);
            });
            if (disposed || decision.Collect) return;
            collectionStopped = true;
            foreach (var entry in Monitors.ToArray())
                if (Monitors.Remove(entry.Key)) await Task.Run(entry.Value.Dispose);
            previous.Clear();
            Status = "空间或自身预算不足，采集已暂停；已有索引可能过期，容量查询仍可用。";
            Changed?.Invoke();
        }
        catch (Exception error) { if (!disposed) { Status = "预算状态无法读取：" + error.Message; Changed?.Invoke(); } }
    }
    private async Task Start(RegisteredProject project)
    {
        if (collectionStopped || disposed || Monitors.ContainsKey(project.Id)) return;
        try
        {
            var started = await Task.Run(() =>
            {
                Store.RecordGap(project.Id.ToString(), "开始或恢复观察；此前的离线区间没有完整历史。");
                var baseline = Store.LoadSnapshots(project.Id.ToString());
                return (Monitor: new DirectoryMonitor(project.Root, absolutePaths: true, initialFiles: baseline), Baseline: baseline);
            });
            if (disposed || collectionStopped || Projects.Any(p => p.Id == project.Id && p.Paused) || Monitors.ContainsKey(project.Id))
            { await Task.Run(started.Monitor.Dispose); return; }
            previous[project.Id] = started.Baseline;
            Monitors.Add(project.Id, started.Monitor);
            revisions.Remove(project.Id);
            errors.Remove(project.Id);
            Status = "正在建立基线；历史仅从本次观察开始。";
        }
        catch (Exception error) { Status = project.Name + "：" + error.Message; errors[project.Id] = Status; }
        Changed?.Invoke();
    }
    public async Task Register(string name, string root, string tool)
    {
        var project = await Task.Run(() => Registry.Register(name, root, tool));
        Projects.Add(project);
        await Start(project);
    }
    public async Task TogglePause(RegisteredProject project)
    {
        Registry.SetPaused(project.Id, !project.Paused);
        var index = Projects.IndexOf(project);
        Projects[index] = project with { Paused = !project.Paused };
        if (Monitors.Remove(project.Id, out var monitor))
        {
            previous.Remove(project.Id);
            await Task.Run(monitor.Dispose);
        }
        else await Start(Projects[index]);
        await Task.Run(() => Store.RecordGap(project.Id.ToString(), !project.Paused ? "用户暂停观察，后续区间不可见。" : "恢复观察，正在校准当前状态。"));
        Status = !project.Paused ? "已暂停；暂停区间是历史缺口。" : "已恢复；正在重新校准当前状态。";
        Changed?.Invoke();
    }
    public void Dispose() { disposed = true; discoveryClosing.Cancel(); if (!discoveringSessions) sessionDiscovery.Dispose(); discoveryTimer.Stop(); statusTimer.Stop(); foreach (var monitor in Monitors.Values) monitor.Dispose(); Store.Dispose(); Sessions.Dispose(); cleanupJournal?.Dispose(); cleanupCancellation?.Dispose(); }
}
