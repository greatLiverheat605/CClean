using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using CClean.App.ViewModels;
using CClean.App.Views;
using CClean.Core.Experiments;
using CClean.Core.Projects;
using CClean.Core.Sessions;

internal static class ScopeLifecycleProbe
{
    private sealed record Scenario(string Id, string Operation, string Expected, object Actual, bool Passed, double Milliseconds, string? Failure);

    public static Application CreateApplication()
    {
        using var source = typeof(ScopeLifecycleProbe).Assembly.GetManifestResourceStream("CClean.App.Theme.xaml")
            ?? throw new InvalidOperationException("Product theme resource is missing.");
        var application = XDocument.Load(source).Root ?? throw new InvalidOperationException("Product theme is empty.");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = application.Element(presentation + "Application.Resources")
            ?? throw new InvalidOperationException("Product theme resources are missing.");
        var dictionary = new XElement(presentation + "ResourceDictionary", application.Attributes().Where(a => a.IsNamespaceDeclaration), resources.Elements());
        return new Application { Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString()), ShutdownMode = ShutdownMode.OnExplicitShutdown };
    }

    public static void Run(Application app, string output)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        using var box = new ExperimentSandbox(Path.Combine(output, "scope-lifecycle-sandbox"));
        var data = Path.Combine(box.RunDirectory, "data");
        var priorData = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR");
        var started = DateTimeOffset.UtcNow;
        var scenarios = new List<Scenario>();
        var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var externalFixtures = new List<object>();
        WorkspaceViewModel? workspace = null;
        string? failure = null;

        string Root(string name, params int[] lengths)
        {
            var root = Path.Combine(box.RunDirectory, name); Directory.CreateDirectory(root);
            for (var i = 0; i < lengths.Length; i++)
            {
                var path = Path.Combine(root, $"sample-{i:D2}.bin");
                File.WriteAllBytes(path, Enumerable.Repeat((byte)(i + 1), lengths[i]).ToArray());
                originals[path] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            }
            return root;
        }

        async Task Scenario(string id, string operation, string expected, Func<Task<object>> action)
        {
            var timer = Stopwatch.StartNew();
            try { var actual = await action(); scenarios.Add(new(id, operation, expected, actual, true, timer.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error)
            {
                scenarios.Add(new(id, operation, expected, new
                {
                    projects = workspace?.Projects.Select(p => new { p.Id, p.Root, p.Revision, p.ScopeRevision, p.Paused, p.ObservationKey }).ToArray(),
                    monitorIds = workspace?.Monitors.Keys.ToArray()
                }, false, timer.Elapsed.TotalMilliseconds, error.ToString()));
                throw;
            }
        }

        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", data);
                workspace = new WorkspaceViewModel(enableSessionDiscovery: false);
                Check(app.Dispatcher.CheckAccess() && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Workspace must run on the WPF STA Dispatcher.");
                Check(app.MainWindow is null && app.Windows.Count == 0, "Component probe must not create a product window.");
                Check(Path.GetFullPath(workspace.DataDirectory) == Path.GetFullPath(data), "Workspace data escaped the isolated fixture.");

                await Scenario("editable-tool-template", "在目录登记与手动会话组件中，通过 PART_EditableTextBox 输入自定义工具名。",
                    "两个入口的编辑部件可见、可写，输入反馈到 ComboBox.Text；不显示真实桌面窗口。", async () =>
                {
                    var entries = new UserControl[] { new ProjectSetupView(workspace), new SessionsView(workspace) };
                    var actual = new List<object>();
                    foreach (var view in entries)
                    {
                        var combo = (ComboBox)view.FindName("Tool");
                        combo.ApplyTemplate(); combo.Measure(new Size(280, 80)); combo.Arrange(new Rect(0, 0, 280, 80));
                        var editable = combo.Template.FindName("PART_EditableTextBox", combo) as TextBox;
                        Check(editable is not null && editable.Visibility == Visibility.Visible && !editable.IsReadOnly, "Editable tool template part is missing, hidden or read-only.");
                        editable!.Text = "自定义 lifecycle Agent";
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        Check(combo.Text == editable.Text, "TextBox edits did not update ComboBox.Text.");
                        var explanation = ((TextBlock)view.FindName("ToolCapabilities")).Text;
                        Check(explanation.Contains("未识别安装或目录", StringComparison.Ordinal), "Tool explanation did not follow custom text input.");
                        actual.Add(new { view = view.GetType().Name, partVisibility = editable.Visibility.ToString(), combo.Text, explanation });
                    }
                    return actual;
                });

                await Scenario("remove-during-start", "卡住本次登记的观察器创建；完成移除后再释放旧启动请求。",
                    "旧请求完成后不重新登记或发布观察器；当前汇总为空，隔离原文件保持。", async () =>
                {
                    var root = Root("race-root", 128);
                    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    workspace.BeforeMonitorStart = async () => { entered.TrySetResult(); await release.Task; };
                    Task? registering = null;
                    RegisteredProject? removed = null;
                    try
                    {
                        registering = workspace.Register("启动竞争隔离范围", root, "通用工具");
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        removed = workspace.Projects.Single(p => p.Root == root);
                        await workspace.RemoveRegistration(removed).WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    finally
                    {
                        release.TrySetResult(); workspace.BeforeMonitorStart = null;
                        if (registering is not null) await registering.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    Check(removed is not null && !workspace.Registry.IsCurrent(removed.Id, removed.Revision), "Removed registration became current again.");
                    Check(workspace.Projects.Count == 0 && workspace.Monitors.Count == 0, "A stale start republished a registration or watcher.");
                    var totals = workspace.Store.CurrentTotals(root, workspace.CurrentObservationKeys);
                    Check(totals.Files == 0 && OriginalFilesIntact(), "Removed scope remained in current totals or fixture files changed.");
                    return new { removed!.Id, registrations = workspace.Projects.Count, watchers = workspace.Monitors.Count, currentFiles = totals.Files, originalFilesIntact = true };
                });

                var initialRoot = Root("initial-root", 192);
                var adjustedRoot = Root("adjusted-root", 256, 384);
                Guid primaryId = Guid.Empty;
                string initialKey = "";
                await Scenario("root-revision-history", "登记一个隔离根并等待真实 watcher / SQLite 基线，再调整为含两个文件的新根。",
                    "新根使用独立 ObservationKey；旧快照和路径历史保留，当前汇总只计新根。", async () =>
                {
                    await workspace.Register("根调整隔离范围", initialRoot, "Codex");
                    var initial = workspace.Projects.Single(p => p.Root == initialRoot); primaryId = initial.Id; initialKey = initial.ObservationKey;
                    await Until(() => workspace.Monitors.TryGetValue(primaryId, out var monitor) && monitor.HasContinuousObservation &&
                        workspace.Store.ProjectTotals(initialKey).Files == 1, "Initial watcher baseline was not persisted.");
                    await workspace.UpdateRegistration(initial, initial.Name, adjustedRoot, initial.Tool);
                    var current = workspace.Projects.Single(p => p.Id == primaryId);
                    await Until(() => workspace.Monitors.TryGetValue(primaryId, out var monitor) && monitor.HasContinuousObservation &&
                        workspace.Store.ProjectTotals(current.ObservationKey).Files == 2, "Adjusted watcher baseline was not persisted.");
                    var oldTotals = workspace.Store.ProjectTotals(initialKey);
                    var oldHistory = workspace.Store.Changes(started.AddSeconds(-1), 100, initialKey);
                    var currentTotals = workspace.Store.CurrentTotals(adjustedRoot, workspace.CurrentObservationKeys);
                    Check(current.ObservationKey != initialKey && current.ScopeRevision == initial.ScopeRevision + 1, "Root change reused the old observation version.");
                    Check(oldTotals.Files == 1 && oldHistory.Any(c => c.Path.StartsWith(initialRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "Old scope snapshot or history was lost.");
                    Check(currentTotals.Files == 2 && workspace.Monitors.Count == 1, "Current totals or watcher count include an obsolete root.");
                    return new { oldKey = initialKey, newKey = current.ObservationKey, current.ScopeRevision, oldFiles = oldTotals.Files,
                        oldHistoryRows = oldHistory.Count, currentFiles = currentTotals.Files, watchers = workspace.Monitors.Count };
                });

                await Scenario("pause-resume-single-watcher", "暂停当前根，再用同一暂停修订并发请求恢复两次。",
                    "暂停释放观察器，旧索引保留；一次恢复成功、陈旧操作拒绝，最终只有一个新观察器且观察键不变。", async () =>
                {
                    var before = workspace.Projects.Single(p => p.Id == primaryId); var oldMonitor = workspace.Monitors[primaryId];
                    await workspace.TogglePause(before);
                    var paused = workspace.Projects.Single(p => p.Id == primaryId);
                    Check(paused.Paused && !workspace.Monitors.ContainsKey(primaryId), "Pause did not release its watcher.");
                    Check(workspace.Store.ProjectTotals(before.ObservationKey).Files == 2, "Pause discarded the existing index.");
                    var resumes = new[] { workspace.TogglePause(paused), workspace.TogglePause(paused) };
                    var staleRejections = 0;
                    foreach (var resume in resumes)
                        try { await resume; } catch (InvalidOperationException) { staleRejections++; }
                    var resumed = workspace.Projects.Single(p => p.Id == primaryId);
                    await Until(() => workspace.Monitors.TryGetValue(primaryId, out var monitor) && monitor.HasContinuousObservation, "Resumed watcher did not establish observation.");
                    Check(!resumed.Paused && staleRejections == 1 && workspace.Monitors.Count == 1, "Concurrent resume did not reject exactly one stale request or created duplicate watchers.");
                    Check(resumed.ObservationKey == before.ObservationKey && !ReferenceEquals(oldMonitor, workspace.Monitors[primaryId]) && !oldMonitor.HasContinuousObservation,
                        "Resume changed the history key or retained its old watcher.");
                    return new { resumed.ObservationKey, staleRejections, watchers = workspace.Monitors.Count, oldWatcherStopped = !oldMonitor.HasContinuousObservation };
                });

                Guid secondaryId = Guid.Empty, sharedId = Guid.Empty;
                await Scenario("shared-root-references", "登记第二项目与一个共享数据根，让两个项目显式引用该已登记根。",
                    "共享引用保留依据，不新增物理根或 watcher，也不把共享文件重复计量。", async () =>
                {
                    var secondRoot = Root("second-project", 512); var sharedRoot = Root("shared-data", 640);
                    await workspace.Register("第二隔离项目", secondRoot, "Claude Code");
                    await workspace.Register("共享隔离数据", sharedRoot, "通用工具");
                    var second = workspace.Projects.Single(p => p.Root == secondRoot); secondaryId = second.Id;
                    var shared = workspace.Projects.Single(p => p.Root == sharedRoot); sharedId = shared.Id;
                    await Until(() => workspace.Monitors.Count == 3 && workspace.Monitors.Values.All(m => m.HasContinuousObservation) &&
                        workspace.Store.CurrentTotals(adjustedRoot, workspace.CurrentObservationKeys).Files == 4, "Three physical scopes were not fully indexed.");
                    var before = workspace.Monitors.ToDictionary(p => p.Key, p => p.Value);
                    await workspace.LinkScope(workspace.Projects.Single(p => p.Id == primaryId), shared, sharedRoot, "隔离项目一用户确认关联，不证明独占");
                    await workspace.LinkScope(workspace.Projects.Single(p => p.Id == secondaryId), shared, sharedRoot, "隔离项目二用户确认关联，不证明独占");
                    Check(workspace.Projects.Count == 3 && workspace.Monitors.Count == 3 && before.All(p => ReferenceEquals(p.Value, workspace.Monitors[p.Key])), "Linking a shared scope replaced or duplicated a watcher.");
                    var links = workspace.Projects.Where(p => p.Id == primaryId || p.Id == secondaryId).SelectMany(p => p.RelatedScopes).ToArray();
                    var totals = workspace.Store.CurrentTotals(adjustedRoot, workspace.CurrentObservationKeys);
                    Check(links.Length == 2 && links.All(r => r.ScopeId == sharedId) && totals.Files == 4, "Shared scope references or current totals are inconsistent.");
                    return new { physicalRoots = workspace.Projects.Count, watchers = workspace.Monitors.Count, explicitReferences = links.Length, currentFiles = totals.Files,
                        evidence = links.Select(r => r.Evidence).ToArray(), grantsCleanupPermission = false };
                });

                await Scenario("save-failure-keeps-ui-and-watcher", "用隔离 data 内的 projects.json.new 空目录阻断写入，尝试改名并调整根。",
                    "保存错误明确返回；Registry、ObservableCollection、观察键和现有 watcher 全部保持原状态。", async () =>
                {
                    var project = workspace.Projects.Single(p => p.Id == primaryId);
                    var target = Root("failed-adjustment", 768);
                    var beforeProjects = JsonSerializer.Serialize(workspace.Projects);
                    var beforeRegistry = JsonSerializer.Serialize(workspace.Registry.Projects);
                    var beforeMonitors = workspace.Monitors.ToDictionary(p => p.Key, p => p.Value);
                    var blocker = Path.Combine(data, "projects.json.new"); Directory.CreateDirectory(blocker);
                    Exception? rejected = null;
                    try { await workspace.UpdateRegistration(project, "不应生效", target, "通用工具"); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = error; }
                    finally { Directory.Delete(blocker); }
                    Check(rejected is not null, "Injected persistence failure was not returned to the caller.");
                    Check(beforeProjects == JsonSerializer.Serialize(workspace.Projects) && beforeRegistry == JsonSerializer.Serialize(workspace.Registry.Projects), "Failed persistence changed UI or registry state.");
                    Check(workspace.Monitors.Count == beforeMonitors.Count && beforeMonitors.All(p => ReferenceEquals(p.Value, workspace.Monitors[p.Key])), "Failed persistence changed a watcher.");
                    return new { failureType = rejected!.GetType().Name, uiUnchanged = true, registryUnchanged = true,
                        watchers = workspace.Monitors.Count, observationKey = workspace.CurrentObservationKey(primaryId) };
                });

                await Scenario("remove-excludes-current-retains-history-and-files", "移除调整后的主范围，再移除其余隔离范围。",
                    "移除后当前汇总排除旧索引、watcher 归零；历史仍可查询，全部隔离原文件字节保持。", async () =>
                {
                    var project = workspace.Projects.Single(p => p.Id == primaryId);
                    await workspace.RemoveRegistration(project);
                    var afterPrimary = workspace.Store.CurrentTotals(adjustedRoot, workspace.CurrentObservationKeys);
                    Check(afterPrimary.Files == 2 && !workspace.Monitors.ContainsKey(primaryId), "Removed primary scope remained in current totals or watchers.");
                    Check(workspace.Store.ProjectTotals(project.ObservationKey).Files == 2 && workspace.Store.ProjectTotals(initialKey).Files == 1,
                        "Removing monitoring erased a scope's retained index.");
                    await workspace.RemoveRegistration(workspace.Projects.Single(p => p.Id == secondaryId));
                    await workspace.RemoveRegistration(workspace.Projects.Single(p => p.Id == sharedId));
                    var current = workspace.Store.CurrentTotals(adjustedRoot, workspace.CurrentObservationKeys);
                    var historical = workspace.Store.Totals(adjustedRoot);
                    Check(workspace.Projects.Count == 0 && workspace.Monitors.Count == 0 && current.Files == 0, "Removed roots are still present in current state.");
                    Check(historical.Files >= 5 && OriginalFilesIntact(), "Historical indexes or original fixture bytes were removed.");
                    return new { currentFilesAfterPrimaryRemoval = afterPrimary.Files, finalCurrentFiles = current.Files, retainedIndexedRows = historical.Files,
                        registrations = workspace.Projects.Count, watchers = workspace.Monitors.Count, checkedOriginalFiles = originals.Count, originalFilesIntact = true };
                });

                await Scenario("multi-root-save-does-not-cross-scope-revision", "等两个隔离根完成真实基线；阻塞计时批次的首根 A，再把同批次 B 调整为 C；新 watcher 发布后才释放 A。",
                    "旧批次不能把 B 路径写进 C 的新观察键；新索引与变化历史只含 C，B 原历史保留。", async () =>
                {
                    var firstRoot = Root("batch-first-root", 896);
                    var secondRoot = Root("batch-second-root", 1024);
                    var nextRoot = Root("batch-next-root", 1152);
                    await workspace.Register("批次隔离范围一", firstRoot, "通用工具");
                    await workspace.Register("批次隔离范围二", secondRoot, "通用工具");
                    await Until(() => workspace.Monitors.Count == 2 && workspace.Monitors.Values.All(m => m.HasContinuousObservation) &&
                        workspace.Projects.All(p => workspace.Store.ProjectTotals(p.ObservationKey).Files == 1), "Both batch roots must have persisted real watcher baselines.");
                    var order = workspace.Monitors.Keys.ToArray();
                    var anchor = workspace.Projects.Single(p => p.Id == order[0]);
                    var moving = workspace.Projects.Single(p => p.Id == order[1]);
                    var oldMonitor = workspace.Monitors[moving.Id];
                    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var staleEntryVisited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    RegisteredProject? changed = null;
                    workspace.BeforeSnapshotSave = async id =>
                    {
                        if (id == anchor.Id && !entered.Task.IsCompleted)
                        {
                            entered.TrySetResult();
                            await release.Task;
                        }
                        else if (id == moving.Id && entered.Task.IsCompleted && release.Task.IsCompleted) staleEntryVisited.TrySetResult();
                    };
                    try
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        await workspace.UpdateRegistration(moving, moving.Name, nextRoot, moving.Tool).WaitAsync(TimeSpan.FromSeconds(10));
                        changed = workspace.Projects.Single(p => p.Id == moving.Id);
                        await Until(() => workspace.Monitors.TryGetValue(moving.Id, out var current) &&
                            !ReferenceEquals(current, oldMonitor) && current.HasContinuousObservation, "New-root watcher must be published before releasing the older timer batch.");
                        Check(changed.ObservationKey != moving.ObservationKey && !release.Task.IsCompleted, "Root revision was not changed inside the blocked timer batch.");
                        release.TrySetResult();
                        await staleEntryVisited.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        await Until(() => workspace.Store.ProjectTotals(changed.ObservationKey).Files == 1, "New-root snapshot was not persisted after the older batch completed.");
                    }
                    finally
                    {
                        release.TrySetResult();
                        workspace.BeforeSnapshotSave = null;
                    }
                    Check(changed is not null, "New scope revision was not published.");
                    var currentRows = workspace.Store.LoadSnapshots(changed!.ObservationKey);
                    var newHistory = workspace.Store.Changes(started.AddSeconds(-1), 100, changed.ObservationKey);
                    var oldHistory = workspace.Store.Changes(started.AddSeconds(-1), 100, moving.ObservationKey);
                    bool InRoot(string path, string root) => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    Check(currentRows.Count == 1 && currentRows.Keys.All(path => InRoot(path, nextRoot)), "An old-root snapshot polluted the new observation key.");
                    Check(newHistory.Count > 0 && newHistory.All(change => InRoot(change.Path, nextRoot)) && !newHistory.Any(change => InRoot(change.Path, moving.Root)),
                        "An old-root change was written to the new observation history.");
                    Check(workspace.Store.ProjectTotals(moving.ObservationKey).Files == 1 && oldHistory.Any(change => InRoot(change.Path, moving.Root)), "The previous root's retained snapshot or history was lost.");
                    await workspace.RemoveRegistration(workspace.Projects.Single(p => p.Id == anchor.Id));
                    await workspace.RemoveRegistration(workspace.Projects.Single(p => p.Id == moving.Id));
                    Check(workspace.Monitors.Count == 0 && workspace.Projects.Count == 0 && OriginalFilesIntact(), "Batch fixture removal left a watcher or changed original file bytes.");
                    return new { trigger = "real 500 ms DispatcherTimer", anchorRoot = anchor.Root, previousRoot = moving.Root, newRoot = nextRoot,
                        oldKey = moving.ObservationKey, newKey = changed.ObservationKey, staleBatchEntryVisited = staleEntryVisited.Task.IsCompleted,
                        currentPaths = currentRows.Keys.ToArray(), newHistoryPaths = newHistory.Select(change => change.Path).ToArray(), oldHistoryRows = oldHistory.Count,
                        watchersAfterRemoval = workspace.Monitors.Count, checkedOriginalFiles = originals.Count, originalFilesIntact = true };
                });

                await Scenario("cross-volume-current-totals-and-lifecycle", "只在 D 盘指定实验区新建专属 sandbox 与 17 B 样本；与 C 盘 23 B 新样本共同登记、关联、暂停及移除。",
                    "按真实卷根筛选只计本卷样本且合计不重复；暂停保留最后索引并标为观察未知，移除排除当前键，原文件和历史保留。", async () =>
                {
                    const string lab = @"D:\CCleanLab\CCLEAN-20260917-DEV";
                    var requestedRoot = Path.Combine(lab, "r1-002-cross-volume-" + Guid.NewGuid().ToString("N"));
                    ExperimentSandbox? remoteBox = null;
                    string? remoteFile = null;
                    try
                    {
                        if (!Directory.Exists(lab)) throw new DirectoryNotFoundException("The designated D-volume experiment parent is unavailable; existing contents were not scanned.");
                        if (Directory.Exists(requestedRoot)) throw new IOException("The exclusive fixture path unexpectedly exists; refusing to reuse it.");
                        remoteBox = new ExperimentSandbox(requestedRoot, budget: 4096);
                        remoteFile = remoteBox.CreateFixture("cross-volume-17b.bin", 17);
                        originals[remoteFile] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(remoteFile)));
                        externalFixtures.Add(new { requestedRoot, available = true, remoteBox.RunDirectory, sample = remoteFile, logicalBytes = 17,
                            retained = true, existingParentContentsScanned = false });
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExperimentRefusedException)
                    {
                        remoteBox?.Dispose();
                        externalFixtures.Add(new { requestedRoot, available = false, validationGap = "D-volume fixture could not be created; cross-volume evidence is unavailable.",
                            error = error.ToString(), existingParentContentsScanned = false });
                        throw new InvalidOperationException("跨卷验证缺口：无法建立专属 D 盘 fixture，未用同卷或伪造数据代替。", error);
                    }
                    using var retainedRemoteFixture = remoteBox!;
                    var localRoot = Root("cross-volume-local", 23);
                    var remoteRoot = remoteBox!.RunDirectory;
                    var localVolume = Path.GetPathRoot(localRoot)!;
                    var remoteVolume = Path.GetPathRoot(remoteRoot)!;
                    Check(!localVolume.Equals(remoteVolume, StringComparison.OrdinalIgnoreCase), "Cross-volume fixtures must be on distinct actual volumes.");
                    await workspace.Register("跨卷 C 样本", localRoot, "通用工具");
                    await workspace.Register("跨卷 D 样本", remoteRoot, "通用工具");
                    var local = workspace.Projects.Single(p => p.Root == localRoot);
                    var remote = workspace.Projects.Single(p => p.Root == remoteRoot);
                    await Until(() => workspace.Monitors.Count == 2 && workspace.Monitors.Values.All(m => m.HasContinuousObservation) &&
                        workspace.Store.ProjectTotals(local.ObservationKey).Files == 1 && workspace.Store.ProjectTotals(remote.ObservationKey).Files == 1,
                        "Both physical volumes must have persisted real watcher baselines.");
                    var keys = workspace.CurrentObservationKeys.ToArray();
                    var localTotals = workspace.Store.CurrentTotals(localVolume, keys);
                    var remoteTotals = workspace.Store.CurrentTotals(remoteVolume, keys);
                    var allRows = workspace.Store.QueryFiles(observationKeys: keys);
                    Check(localTotals.Files == 1 && localTotals.Logical == 23 && remoteTotals.Files == 1 && remoteTotals.Logical == 17,
                        "A per-volume current summary included another volume or omitted its own fixture.");
                    Check(localTotals.Files + remoteTotals.Files == 2 && localTotals.Logical + remoteTotals.Logical == 40 &&
                        allRows.Count == 2 && allRows.Select(row => row.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
                        "Cross-volume summaries duplicated or lost a physical sample.");
                    var watchers = workspace.Monitors.ToDictionary(entry => entry.Key, entry => entry.Value);
                    await workspace.LinkScope(local, remote, remoteRoot, "隔离跨卷显式关联，不证明独占或删除资格");
                    Check(workspace.Monitors.Count == 2 && watchers.All(entry => ReferenceEquals(entry.Value, workspace.Monitors[entry.Key])),
                        "A cross-volume reference added or replaced a physical watcher.");
                    Check(workspace.CurrentObservationKeys.Count == 2 && workspace.Store.CurrentTotals(localVolume, workspace.CurrentObservationKeys).Logical == 23 &&
                        workspace.Store.CurrentTotals(remoteVolume, workspace.CurrentObservationKeys).Logical == 17, "A cross-volume reference duplicated an indexed sample.");
                    await workspace.TogglePause(remote);
                    var paused = workspace.Projects.Single(p => p.Id == remote.Id);
                    var pausedTotals = workspace.Store.CurrentTotals(remoteVolume, workspace.CurrentObservationKeys);
                    var pausedActivity = workspace.Activity().Scopes.Single(scope => scope.Root == remoteRoot);
                    Check(paused.Paused && workspace.Monitors.Count == 1 && !workspace.Monitors.ContainsKey(remote.Id) && !watchers[remote.Id].HasContinuousObservation,
                        "Pause did not release the remote-volume watcher.");
                    Check(workspace.CurrentObservationKeys.Contains(remote.ObservationKey) && pausedTotals.Files == 1 && pausedTotals.Logical == 17 &&
                        !pausedActivity.ObservationKnown && workspace.Status.Contains("暂停", StringComparison.Ordinal),
                        "Paused registration must retain its last known 17 B index while activity observation remains unknown.");
                    await workspace.RemoveRegistration(paused);
                    var afterRemoteRemoval = workspace.Store.CurrentTotals(remoteVolume, workspace.CurrentObservationKeys);
                    var remainingLocal = workspace.Store.CurrentTotals(localVolume, workspace.CurrentObservationKeys);
                    Check(!workspace.CurrentObservationKeys.Contains(remote.ObservationKey) && afterRemoteRemoval.Files == 0 && afterRemoteRemoval.Logical == 0 &&
                        remainingLocal.Files == 1 && remainingLocal.Logical == 23 && workspace.Monitors.Count == 1,
                        "Remote removal did not exclude its current key or disturbed the other volume.");
                    var retainedRemoteTotals = workspace.Store.ProjectTotals(remote.ObservationKey);
                    var remoteHistory = workspace.Store.Changes(started.AddSeconds(-1), 100, remote.ObservationKey);
                    Check(retainedRemoteTotals.Files == 1 && retainedRemoteTotals.Logical == 17 && remoteHistory.Any(change => change.Path == remoteFile),
                        "Remote removal erased its retained index or path history.");
                    await workspace.RemoveRegistration(workspace.Projects.Single(p => p.Id == local.Id));
                    var retainedLocalTotals = workspace.Store.ProjectTotals(local.ObservationKey);
                    var localHistory = workspace.Store.Changes(started.AddSeconds(-1), 100, local.ObservationKey);
                    var localFile = Path.Combine(localRoot, "sample-00.bin");
                    Check(workspace.Monitors.Count == 0 && workspace.CurrentObservationKeys.Count == 0 &&
                        workspace.Store.CurrentTotals(localVolume, workspace.CurrentObservationKeys).Files == 0 && OriginalFilesIntact(),
                        "Final cross-volume removal left current data or changed original file bytes.");
                    Check(retainedLocalTotals.Files == 1 && retainedLocalTotals.Logical == 23 && localHistory.Any(change => change.Path == localFile),
                        "Local removal erased its retained index or path history.");
                    return new { localVolume, localRoot, localFile, localSampleSha256 = originals[localFile], localTotals,
                        remoteVolume, remoteRoot, remoteFile, remoteSampleSha256 = originals[remoteFile!], remoteTotals,
                        combinedFiles = localTotals.Files + remoteTotals.Files, combinedLogical = localTotals.Logical + remoteTotals.Logical,
                        referenceWatchers = watchers.Count, pausedTotals, pausedObservationKnown = pausedActivity.ObservationKnown,
                        pausedMeasurement = "最后已知索引，暂停期间不代表实时数据", afterRemoteRemoval, retainedRemoteTotals, remoteHistoryRows = remoteHistory.Count,
                        retainedLocalTotals, localHistoryRows = localHistory.Count,
                        watchersAfterRemoval = workspace.Monitors.Count, remoteFixtureRetained = true, checkedOriginalFiles = originals.Count, originalFilesIntact = true };
                });

                await Scenario("removed-source-session-remains-visible-and-endable", "在独立隔离根开始手动会话并移除监测；从另一项目的会话组件显示遗留保护会话，再用实际结束按钮结束。",
                    "默认项目筛选排除旧来源；勾选显示遗留后保留原根、版本与 ID，说明保护仍在；结束后不再列为未结束遗留。", async () =>
                {
                    var sourceRoot = Root("detached-session-source", 29);
                    var otherRoot = Root("detached-session-other-project", 31);
                    await workspace.Register("会话原范围", sourceRoot, "Codex");
                    await workspace.Register("会话其他项目", otherRoot, "Claude Code");
                    var source = workspace.Projects.Single(project => project.Root == sourceRoot);
                    var other = workspace.Projects.Single(project => project.Root == otherRoot);
                    await Until(() => workspace.Monitors.Count == 2 && workspace.Monitors.Values.All(monitor => monitor.HasContinuousObservation) &&
                        workspace.Store.ProjectTotals(source.ObservationKey).Files == 1 && workspace.Store.ProjectTotals(other.ObservationKey).Files == 1,
                        "Both session fixture roots must have persisted baselines.");
                    var session = workspace.Sessions.Begin(source, "Codex", "移除来源后的隔离保护会话");
                    await workspace.RemoveRegistration(source);
                    Check(workspace.Activity().Scopes.Any(scope => scope.Root == sourceRoot && scope.Active && !scope.ObservationKnown),
                        "Removing monitoring silently ended or released the original manual session protection.");
                    var view = new SessionsView(workspace);
                    view.ShowProject(ProjectCatalog.Build(workspace.Projects, []).Single(project => project.RegistrationId == other.Id));
                    var sessions = (DataGrid)view.FindName("Sessions");
                    var includeDetached = (CheckBox)view.FindName("IncludeDetached");
                    var notice = (TextBlock)view.FindName("SessionNotice");
                    Check(includeDetached.IsChecked != true && !sessions.Items.OfType<DevelopmentSession>().Any(item => item.Id == session.Id),
                        "Another project's default session filter included the removed source.");
                    var initialNotice = notice.Text;
                    Check(initialNotice.Contains("另有 1 个", StringComparison.Ordinal) && initialNotice.Contains("结束会解除该手动保护", StringComparison.Ordinal),
                        "The component did not disclose the remaining detached protection or how to end it.");
                    includeDetached.IsChecked = true;
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    var shown = sessions.Items.OfType<DevelopmentSession>().Single(item => item.Id == session.Id);
                    var columnBindings = sessions.Columns.OfType<DataGridTextColumn>().Select(column =>
                        (column.Binding as System.Windows.Data.Binding)?.Path?.Path).ToArray();
                    Check(shown.ProjectRoot == sourceRoot && shown.ScopeRevision == source.ScopeRevision &&
                        columnBindings.Contains(nameof(DevelopmentSession.ProjectRoot)) && columnBindings.Contains(nameof(DevelopmentSession.ScopeRevision)),
                        "The detached row lost its original root/version or the grid does not expose them.");
                    sessions.SelectedItem = shown;
                    var endButton = LogicalDescendants<Button>(view).Single(button => button.Content as string == "结束选中会话");
                    endButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, endButton));
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    var ended = workspace.Sessions.All.Single(item => item.Id == session.Id);
                    Check(ended.State == SessionState.Ended && !sessions.Items.OfType<DevelopmentSession>().Any(item => item.Id == session.Id) &&
                        notice.Text.Contains("另有 0 个", StringComparison.Ordinal) && !workspace.Activity().Scopes.Any(scope => scope.Root == sourceRoot && scope.Active),
                        "Ending through the component did not remove the unfinished detached session or release its manual protection.");
                    await workspace.RemoveRegistration(workspace.Projects.Single(project => project.Id == other.Id));
                    Check(workspace.Monitors.Count == 0 && OriginalFilesIntact() && workspace.Store.ProjectTotals(source.ObservationKey).Files == 1,
                        "Session component operations changed fixture files or erased the original indexed history.");
                    return new { sessionId = session.Id, originalRoot = shown.ProjectRoot, shown.ScopeRevision, selectedProject = other.Root,
                        defaultIncludedRemovedSource = false, includedAfterCheckbox = true, columnBindings, initialNotice, noticeAfterEnd = notice.Text,
                        endedState = ended.State.ToString(), ended.Ended, watchersAfterRemoval = workspace.Monitors.Count,
                        checkedOriginalFiles = originals.Count, originalFilesIntact = true };
                });
                Check(workspace.DiscoveredSessions.Count == 0, "Session discovery must remain disabled for this fixture.");
                Check(app.MainWindow is null && app.Windows.Count == 0, "Component probe unexpectedly created a product window.");
            }
            catch (Exception error) { failure = error.ToString(); }
            finally
            {
                try { if (workspace is not null) await workspace.ShutdownAsync(); } catch (Exception error) { failure ??= error.ToString(); }
                Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", priorData);
                var passed = failure is null && scenarios.Count == 10 && scenarios.All(s => s.Passed);
                var report = new
                {
                    mode = "isolated Workspace component lifecycle on WPF Dispatcher with real FileSystemWatcher and SQLite",
                    componentOnly = true, installedUiAcceptance = false, sessionDiscovery = "disabled by constructor; Resume and DiscoverSessions were not invoked",
                    startedUtc = started, finishedUtc = DateTimeOffset.UtcNow, fixtureRoot = box.RunDirectory, dataDirectory = data,
                    automaticCleanupInvoked = false, externalFixtures, scenarios, passed, failure
                };
                try
                {
                    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    File.WriteAllText(Path.Combine(output, "scope-lifecycle.json"), json, new UTF8Encoding(false));
                    Console.WriteLine(json);
                }
                catch (Exception error) { Console.Error.WriteLine(error); passed = false; }
                Environment.ExitCode = passed ? 0 : 1;
                app.Shutdown(Environment.ExitCode);
                app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();

        bool OriginalFilesIntact() => originals.All(p => File.Exists(p.Key) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Key))) == p.Value);
    }

    private static async Task Until(Func<bool> condition, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(failure);
            await Task.Delay(100);
        }
    }
    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in LogicalDescendants<T>(child)) yield return descendant;
        }
    }
    private static void Check(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }
}
