using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CClean.App;
using CClean.App.ViewModels;
using CClean.App.Views;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Sessions;

internal static class HistoryPagingProbe
{
    private sealed record ProbeCheck(string name, bool passed, object? detail);
    private sealed record Fixture(string Root, string Content, byte[] Sample, string[] Paths,
        CleanupContext Context, CleanupPlan Plan, string[] Eligible);
    private static readonly string[] RequiredChecks =
    [
        "fixture-metadata-only", "runs-first-page-bounded", "runs-next-page-complete", "runs-previous-page-restores",
        "manifest-first-page-bounded", "short-viewport-scrolls-with-bounded-virtualized-tables", "manifest-complete-no-duplicates", "events-snapshot-complete-beyond3000",
        "events-refresh-includes-later-write", "recovery-pages-skip-excluded-advance", "raw-audit-readable-after-recovery",
        "old-mode-request-cannot-replace-current", "old-run-request-cannot-replace-current", "read-error-is-not-empty-history",
        "exit-waits-pending-history", "history-refuses-new-work-after-stopping", "exit-drains-before-store-close",
        "fixture-content-preserved-no-cleanup"
    ];

    internal static async Task<object> Run(string output)
    {
        Application.Current.Dispatcher.VerifyAccess();
        using var box = new ExperimentSandbox(Path.Combine(output, "history-paging-sandbox"));
        var previousData = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR");
        var data = Path.Combine(box.RunDirectory, "data");
        var started = DateTimeOffset.UtcNow;
        var checks = new List<ProbeCheck>(); var errors = new List<string>();
        var releases = new List<TaskCompletionSource>();
        WorkspaceViewModel? workspace = null; CleanupHistoryView? view = null; MainWindow? window = null;
        Fixture? fixture = null;
        var exitVerificationReady = false;
        object? exitReport = null;
        var reportPath = Path.Combine(output, "history-paging.json");
        object Report(string? failure) => new
        {
            mode = "isolated WPF history pagination and shutdown barriers", componentOnly = true, installedUiAcceptance = false,
            startedUtc = started, finishedUtc = DateTimeOffset.UtcNow, fixtureRoot = box.RunDirectory, data,
            requiredChecks = RequiredChecks, checks, errors,
            passed = failure is null && errors.Count == 0 && checks.Select(c => c.name).SequenceEqual(RequiredChecks) && checks.All(c => c.passed),
            safeExitCompleted = window?.ExitReady == true, cleanupTaskCreated = workspace?.CleanupTask is not null,
            productionBatchLimit = 1000, lifetimeMinutes = 5, manifestLimitBytes = 8 * 1024 * 1024,
            limitation = "Synthetic journal display fixtures only: 202 tasks, 405 objects, 3205 initial audit events. No executor, deletion, real consumer proof, UI input or large-batch acceptance."
        };
        void Save(object report) => File.WriteAllText(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        void Check(string name, bool passed, object? detail = null)
        {
            checks.Add(new(name, passed, detail)); if (!passed) throw new InvalidOperationException(name);
        }
        Application.Current.Exit += (_, e) =>
        {
            try
            {
                if (!exitVerificationReady || errors.Count != 0 ||
                    !checks.Select(c => c.name).SequenceEqual(RequiredChecks.Take(RequiredChecks.Length - 2)))
                    throw new InvalidOperationException("Application exited before all history checks reached the final exit barrier.");
                Exception? closed = null;
                try { workspace!.Store.ProjectTotals("history-fixture-no-project"); } catch (Exception error) { closed = error; }
                Check("exit-drains-before-store-close", window?.ExitReady == true && closed is not null &&
                    view!.WaitForPendingOperationsAsync().IsCompleted && workspace!.Monitors.Count == 0 && workspace.CleanupTask is null);
                Check("fixture-content-preserved-no-cleanup", workspace!.CleanupTask is null && FixturePreserved(fixture!));
            }
            catch (Exception error) { errors.Add(error.ToString()); }
            try
            {
                exitReport = Report(errors.Count == 0 ? null : "Exit verification failed.");
                Save(exitReport);
                Console.WriteLine(reportPath);
            }
            catch (Exception error) { errors.Add(error.ToString()); Console.Error.WriteLine(error); }
            finally
            {
                if (errors.Count != 0 || !checks.Select(c => c.name).SequenceEqual(RequiredChecks) || checks.Any(c => !c.passed))
                    Environment.ExitCode = e.ApplicationExitCode = 1;
                Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", previousData);
            }
        };
        try
        {
            fixture = CreateFixture(box.RunDirectory, data);
            var paths = fixture.Paths; var sample = fixture.Sample; var plan = fixture.Plan; var eligible = fixture.Eligible;
            Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", data);
            workspace = new WorkspaceViewModel(enableSessionDiscovery: false);
            var journal = workspace.OpenCleanupJournal();
            Check("fixture-metadata-only", plan.Items.Count == 405 && plan.EligibleCount == 403 && !plan.Truncated && workspace.CleanupTask is null,
                new { plan.Items.Count, plan.EligibleCount, logicalBytes = paths.Length * sample.Length, executionAttempted = false });
            await SeedHistory(journal, fixture);
            view = new CleanupHistoryView(workspace);
            var runs = (DataGrid)view.FindName("Runs"); var rows = (DataGrid)view.FindName("Events");
            var mode = (ComboBox)view.FindName("DetailMode"); var details = (TextBlock)view.FindName("DetailStatus");
            var allRunIds = new HashSet<string>(StringComparer.Ordinal);
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await view.WaitForPendingOperationsAsync().WaitAsync(TimeSpan.FromSeconds(20));
            foreach (var item in runs.Items.Cast<object>()) allRunIds.Add(Value<string>(item, "Id"));
            Check("runs-first-page-bounded", runs.Items.Count == 200 && Button("RunNextButton").IsEnabled && !Button("RunPreviousButton").IsEnabled);
            await Click("RunNextButton");
            foreach (var item in runs.Items.Cast<object>()) allRunIds.Add(Value<string>(item, "Id"));
            Check("runs-next-page-complete", runs.Items.Count == 2 && allRunIds.Count == 202 && !Button("RunNextButton").IsEnabled);
            await Click("RunPreviousButton");
            Check("runs-previous-page-restores", runs.Items.Count == 200 && !Button("RunPreviousButton").IsEnabled && Button("RunNextButton").IsEnabled);
            runs.SelectedItem = runs.Items.Cast<object>().Single(item => Value<string>(item, "Id") == plan.Id.ToString());
            await view.WaitForPendingOperationsAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check("manifest-first-page-bounded", rows.Items.Count == 200 && Button("DetailNextButton").IsEnabled && details.Text.Contains("405", StringComparison.Ordinal));
            var shortSize = new Size(960, 240);
            view.Measure(shortSize); view.Arrange(new Rect(shortSize)); view.UpdateLayout();
            await Application.Current.Dispatcher.InvokeAsync(view.UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
            var pageScroll = (ScrollViewer)view.FindName("HistoryScroll");
            var runScroll = VisualChild<ScrollViewer>(runs); var detailScroll = VisualChild<ScrollViewer>(rows);
            var runContainers = RealizedRows(runs); var detailContainers = RealizedRows(rows);
            pageScroll.ScrollToEnd(); view.UpdateLayout();
            await Application.Current.Dispatcher.InvokeAsync(view.UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
            var pagingBounds = Button("DetailNextButton").TransformToAncestor(view).TransformBounds(new Rect(Button("DetailNextButton").RenderSize));
            var bottomReached = pageScroll.VerticalOffset > 0 && pagingBounds.Top >= -1 && pagingBounds.Bottom <= shortSize.Height + 1;
            Check("short-viewport-scrolls-with-bounded-virtualized-tables", pageScroll.ViewportHeight > 0 &&
                pageScroll.ScrollableHeight > 0 && bottomReached && double.IsFinite(runs.ActualHeight) && double.IsFinite(rows.ActualHeight) &&
                runs.ActualHeight >= 160 && rows.ActualHeight >= 240 &&
                runScroll is { ViewportHeight: > 0 } && detailScroll is { ViewportHeight: > 0 } &&
                runScroll.ExtentHeight > runScroll.ViewportHeight && detailScroll.ExtentHeight > detailScroll.ViewportHeight &&
                runContainers is > 0 and < 100 && detailContainers is > 0 and < 100 &&
                runs.EnableRowVirtualization && rows.EnableRowVirtualization,
                new { widthDip = shortSize.Width, heightDip = shortSize.Height, pageScroll.ViewportHeight, pageScroll.ScrollableHeight,
                    bottomReached, runHeight = runs.ActualHeight, detailHeight = rows.ActualHeight, runContainers, detailContainers,
                    runViewport = runScroll?.ViewportHeight, detailViewport = detailScroll?.ViewportHeight });
            pageScroll.ScrollToTop(); view.UpdateLayout();
            var manifestPaths = await CollectPaths();
            Check("manifest-complete-no-duplicates", manifestPaths.Count == 405 && manifestPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 405 &&
                paths.All(path => manifestPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) && rows.Items.Count == 5);

            await ChangeMode(1);
            var initialThrough = journal.EventsPage(plan.Id.ToString(), pageSize: 1).ThroughSequence;
            journal.Record(plan.Id, eligible[0], "Started", "较晚追加事件，旧页面快照不应混入");
            var auditSequences = await CollectSequences();
            Check("events-snapshot-complete-beyond3000", auditSequences.Count == 3205 && auditSequences.Distinct().Count() == 3205 &&
                auditSequences.Max() == initialThrough && rows.Items.Count == 5,
                new { events = auditSequences.Count, throughSequence = initialThrough, lastPage = rows.Items.Count });
            await Click("RefreshButton");
            var refreshedSequences = await CollectSequences();
            Check("events-refresh-includes-later-write", refreshedSequences.Count == 3206 && refreshedSequences.Max() > initialThrough);
            await ChangeMode(2);
            var recoveryPaths = await CollectPaths();
            Check("recovery-pages-skip-excluded-advance", recoveryPaths.Count == 403 && recoveryPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 403 &&
                eligible.All(path => recoveryPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) && rows.Items.Count == 5 && details.Text.Contains("405", StringComparison.Ordinal));
            await ChangeMode(1);
            Check("raw-audit-readable-after-recovery", rows.Items.Count > 0 && rows.Items.Cast<object>().All(row => Value<long>(row, "Sequence") > 0) &&
                journal.EventsPage(plan.Id.ToString(), pageSize: 1).ThroughSequence == refreshedSequences.Max());

            var modeHold = HoldNext("manifest");
            mode.SelectedIndex = 0; await modeHold.reached.WaitAsync(TimeSpan.FromSeconds(10));
            mode.SelectedIndex = 1;
            await Until(() => details.Text.StartsWith("原始审计第", StringComparison.Ordinal));
            modeHold.release.TrySetResult();
            await view.WaitForPendingOperationsAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check("old-mode-request-cannot-replace-current", mode.SelectedIndex == 1 && rows.Items.Cast<object>().All(row => Value<long>(row, "Sequence") > 0));
            var runHold = HoldNext("manifest");
            mode.SelectedIndex = 0; await runHold.reached.WaitAsync(TimeSpan.FromSeconds(10));
            var other = runs.Items.Cast<object>().First(item => Value<string>(item, "Id") != plan.Id.ToString());
            runs.SelectedItem = other;
            await Until(() => rows.Items.Count == 1 && details.Text.StartsWith("批准清单第", StringComparison.Ordinal));
            runHold.release.TrySetResult();
            await view.WaitForPendingOperationsAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check("old-run-request-cannot-replace-current", ReferenceEquals(runs.SelectedItem, other) && rows.Items.Count == 1);
            view.BeforeRead = null;
            runs.SelectedItem = runs.Items.Cast<object>().Single(item => Value<string>(item, "Id") == plan.Id.ToString());
            await view.WaitForPendingOperationsAsync().WaitAsync(TimeSpan.FromSeconds(20));
            view.BeforeRead = kind => kind == "events" ? Task.FromException(new IOException("隔离只读失败样例")) : Task.CompletedTask;
            await ChangeMode(1);
            Check("read-error-is-not-empty-history", details.Text.Contains("明细读取失败", StringComparison.Ordinal) &&
                runs.Items.Count == 200 && !Button("DetailNextButton").IsEnabled && !Button("DetailPreviousButton").IsEnabled);
            view.BeforeRead = null; await ChangeMode(0);

            var exitHold = HoldNext("recovery");
            mode.SelectedIndex = 2; await exitHold.reached.WaitAsync(TimeSpan.FromSeconds(10));
            window = new MainWindow(enableSessionDiscovery: false); Application.Current.MainWindow = window;
            Field("workspace").SetValue(window, workspace); Field("cleanupHistory").SetValue(window, view);
            ((ContentControl)window.FindName("PageContent")).Content = view;
            var exit = window.RequestExit();
            await Task.Delay(50);
            var totalsBeforeExit = workspace.Store.ProjectTotals("history-fixture-no-project");
            Check("exit-waits-pending-history", !exit.IsCompleted && !window.ExitReady && workspace.Stopping,
                new { storeReadable = true, totalsBeforeExit.Files });
            var forbiddenReads = 0;
            view.BeforeRead = _ => { Interlocked.Increment(ref forbiddenReads); return Task.CompletedTask; };
            Button("RefreshButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await Task.Delay(20);
            Check("history-refuses-new-work-after-stopping", forbiddenReads == 0 && !Button("RefreshButton").IsEnabled && !exit.IsCompleted);
            exitVerificationReady = true;
            exitHold.release.TrySetResult(); await exit.WaitAsync(TimeSpan.FromSeconds(20));
            return exitReport ?? throw new InvalidOperationException("Application exit did not write the history report.");

            Button Button(string name) => (Button)view.FindName(name);
            async Task Click(string name)
            {
                var button = Button(name);
                if (!button.IsEnabled) throw new InvalidOperationException("Paging action is disabled: " + name);
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await view.WaitForPendingOperationsAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            async Task ChangeMode(int index)
            { mode.SelectedIndex = index; await view.WaitForPendingOperationsAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            async Task<List<string>> CollectPaths()
            {
                var found = new List<string>();
                while (true)
                {
                    if (rows.Items.Count > 200) throw new InvalidOperationException("Unbounded UI detail page.");
                    found.AddRange(rows.Items.Cast<object>().Select(row => Value<string>(row, "Path")));
                    if (!Button("DetailNextButton").IsEnabled) return found;
                    await Click("DetailNextButton");
                }
            }
            async Task<List<long>> CollectSequences()
            {
                var found = new List<long>();
                while (true)
                {
                    if (rows.Items.Count > 200) throw new InvalidOperationException("Unbounded UI audit page.");
                    found.AddRange(rows.Items.Cast<object>().Select(row => Value<long>(row, "Sequence")));
                    if (!Button("DetailNextButton").IsEnabled) return found;
                    await Click("DetailNextButton");
                }
            }
            (Task reached, TaskCompletionSource release) HoldNext(string heldKind)
            {
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); releases.Add(release);
                var held = 0;
                view.BeforeRead = kind =>
                {
                    if (kind != heldKind || Interlocked.CompareExchange(ref held, 1, 0) != 0) return Task.CompletedTask;
                    reached.TrySetResult(); return release.Task;
                };
                return (reached.Task, release);
            }
        }
        catch (Exception error) { errors.Add(error.ToString()); Environment.ExitCode = 1; Save(Report(error.ToString())); throw; }
        finally
        {
            foreach (var release in releases) release.TrySetResult();
            try
            {
                if (view is not null) { view.BeforeRead = null; view.CancelPendingOperations(); await view.WaitForPendingOperationsAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                if (window is not null && !window.ExitReady) await window.RequestExit().WaitAsync(TimeSpan.FromSeconds(20));
                if (workspace is not null) await workspace.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally { Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", previousData); }
        }
    }

    internal static void RunDesktop(Application app, string output)
    {
        using var box = new ExperimentSandbox(Path.Combine(output, "history-desktop-sandbox"));
        var previousData = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR");
        var data = Path.Combine(box.RunDirectory, "data");
        var started = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        var reportPath = Path.Combine(output, "history-desktop.json");
        WorkspaceViewModel? seedWorkspace = null;
        MainWindow? window = null;
        Fixture? fixture = null;
        var seedOwnerReleased = false;
        var windowShown = false;
        Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", data);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Exit += (_, e) =>
        {
            try
            {
                var workspace = window?.Workspace;
                var safeExit = window?.ExitReady == true && workspace is { Stopping: true, CleanupTask: null } &&
                    workspace.Monitors.Count == 0;
                var preserved = false;
                try { preserved = fixture is not null && FixturePreserved(fixture); }
                catch (Exception error) { errors.Add(error.ToString()); }
                if (!seedOwnerReleased || !windowShown || !safeExit || !preserved)
                    errors.Add("Desktop history fixture did not finish a normal safe exit with its content preserved.");
                var report = new
                {
                    mode = "isolated actual-window history; manual desktop input required",
                    passed = (bool?)null, installedUiAcceptance = false,
                    startedUtc = started, finishedUtc = DateTimeOffset.UtcNow,
                    fixtureRoot = box.RunDirectory, data, root = fixture?.Root, seedOwnerReleased, windowShown,
                    seededTasks = seedOwnerReleased ? 202 : 0, fixtureObjects = fixture?.Paths.Length ?? 0,
                    eligibleObjects = fixture?.Eligible.Length ?? 0, initialAuditEvents = seedOwnerReleased ? 3205 : 0,
                    mainRunId = fixture?.Plan.Id.ToString(), safeExitCompleted = safeExit,
                    fixtureContentPreserved = preserved, cleanupTaskCreated = workspace?.CleanupTask is not null, errors,
                    limitation = "Synthetic display fixtures only. The real sidebar, history paging and detail modes must be operated and recorded by the reviewer. Launch and exit do not establish usability or installed-product acceptance. No executor or cleanup is started by this probe."
                };
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                Console.WriteLine(reportPath);
            }
            catch (Exception error) { errors.Add(error.ToString()); Console.Error.WriteLine(error); }
            finally
            {
                if (errors.Count != 0) Environment.ExitCode = e.ApplicationExitCode = 1;
                Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", previousData);
            }
        };
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                fixture = CreateFixture(box.RunDirectory, data);
                seedWorkspace = new WorkspaceViewModel(enableSessionDiscovery: false);
                await SeedHistory(seedWorkspace.OpenCleanupJournal(), fixture);
                await seedWorkspace.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20));
                seedOwnerReleased = true;
                seedWorkspace = null;
                window = new MainWindow(enableSessionDiscovery: false);
                app.MainWindow = window;
                window.Show(); windowShown = true;
                Console.WriteLine(output);
            }
            catch (Exception error)
            {
                errors.Add(error.ToString()); Console.Error.WriteLine(error); Environment.ExitCode = 1;
                if (seedWorkspace is not null)
                    try { await seedWorkspace.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                    catch (Exception closing) { errors.Add(closing.ToString()); }
                if (window is not null)
                    try { await window.RequestExit().WaitAsync(TimeSpan.FromSeconds(20)); }
                    catch (Exception closing) { errors.Add(closing.ToString()); }
                app.Shutdown(1);
            }
        });
        try { app.Run(); }
        finally { Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", previousData); }
    }

    private static Fixture CreateFixture(string runDirectory, string data)
    {
        var root = Path.Combine(runDirectory, "fixture-cache");
        var content = Path.Combine(root, "_cacache", "content-v2");
        Directory.CreateDirectory(content);
        var sample = new byte[] { 0x43, 0x43 };
        var paths = Enumerable.Range(0, 405).Select(i => Path.Combine(content, $"sample-{i:D3}" + (i is 0 or 201 ? ".db" : ".bin"))).ToArray();
        foreach (var path in paths) { File.WriteAllBytes(path, sample); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1)); }
        var context = new CleanupContext(1, [new(root, CacheRule.NpmContent.Id, 1, "11.19.0", true, true)],
            new ActivitySnapshot(1, [new(root, false, true, "synthetic history fixture only; no live consumer evidence")]), [data]);
        var plan = CleanupPlanner.Preview(paths, context);
        if (plan.Items.Count != 405 || plan.EligibleCount != 403 || plan.Truncated)
            throw new InvalidOperationException("History display fixture did not produce the required 405 objects and 403 eligible entries.");
        return new(root, content, sample, paths, context, plan, plan.Items.Where(item => item.Decision.Eligible).Select(item => item.Path).ToArray());
    }

    private static Task SeedHistory(CleanupJournal journal, Fixture fixture) => Task.Run(() =>
    {
        for (var i = 0; i < 201; i++)
        {
            var small = CleanupPlanner.Preview([fixture.Eligible[0]], fixture.Context);
            journal.Begin(small); journal.Finish(small.Id, "Cancelled", "隔离历史显示样例，未执行删除");
        }
        journal.Begin(fixture.Plan);
        for (var i = 0; i < 3205; i++)
            journal.Record(fixture.Plan.Id, fixture.Eligible[i % fixture.Eligible.Length], "Started", $"隔离显示事件 {i}，未执行删除");
        journal.Finish(fixture.Plan.Id, "Interrupted", "隔离分页与核查显示样例，未执行删除");
    });

    private static bool FixturePreserved(Fixture fixture) =>
        fixture.Paths.All(path => File.ReadAllBytes(path).SequenceEqual(fixture.Sample)) &&
        Directory.EnumerateFiles(fixture.Content).Count() == fixture.Paths.Length;

    private static int RealizedRows(DataGrid grid) => Enumerable.Range(0, grid.Items.Count)
        .Count(index => grid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow);
    private static T? VisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (VisualChild<T>(child) is { } found) return found;
        }
        return null;
    }

    private static T Value<T>(object item, string property) => (T)(item.GetType().GetProperty(property)?.GetValue(item)
        ?? throw new MissingMemberException(item.GetType().Name, property));
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        ?? throw new MissingFieldException(typeof(MainWindow).FullName, name);
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("History view did not publish the expected current request.");
            await Task.Delay(20);
        }
    }
}
