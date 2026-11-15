using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CClean.App;
using CClean.App.Lifecycle;
using CClean.App.ViewModels;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Projects;
using CClean.Core.Sessions;
using CClean.Core.Monitoring;

internal static class WorkbenchLifecycleProbe
{
    internal static void InstanceChild(string data, string report)
    {
        var artifactRoot = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        report = Path.GetFullPath(report);
        if (!report.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Child report must stay in artifacts.");
        var start = SingleInstance.TryStart(data, System.Windows.Threading.Dispatcher.CurrentDispatcher, () => { }, _ => { });
        using (start.Instance)
        using (var parent = new DirectoryLease(Path.GetDirectoryName(report)!))
        using (var stream = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(stream, new { status = start.Status.ToString(), start.Error, start.AbandonedMutexRecovered,
                workspaceConstructed = false });
        Environment.ExitCode = start.Status == SingleInstance.StartStatus.RestoreSignaled ? 0 : 1;
    }

    internal static void Run(Application app, string output, bool desktop)
    {
        using var box = new ExperimentSandbox(Path.Combine(output, "workbench-sandbox"));
        var data = Path.Combine(box.RunDirectory, "data");
        var root = Path.Combine(box.RunDirectory, "project");
        var pausedRoot = Path.Combine(box.RunDirectory, "individual-paused");
        var content = Path.Combine(root, "_cacache", "content-v2");
        Directory.CreateDirectory(content); Directory.CreateDirectory(pausedRoot);
        var paths = Enumerable.Range(0, 3).Select(i => Path.Combine(content, "sample-" + i)).ToArray();
        foreach (var path in paths) { File.WriteAllBytes(path, new byte[16]); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1)); }
        var registry = new ProjectRegistry(data);
        registry.Register("隔离中文项目", root, "通用工具");
        var paused = registry.Register("单独暂停范围", pausedRoot, "通用工具");
        registry.SetPaused(paused.Id, true, paused.Revision);
        var previousData = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR");
        Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", data);
        var checks = new List<object>();
        var errors = new List<string>();
        var dpi = new List<object>();
        var window = new MainWindow(enableSessionDiscovery: false);
        app.MainWindow = window;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        void Check(string name, bool passed, object? detail = null)
        { checks.Add(new { name, passed, detail }); if (!passed) throw new InvalidOperationException(name); }
        void RecordDpi()
        {
            var value = VisualTreeHelper.GetDpi(window);
            dpi.Add(new { utc = DateTimeOffset.UtcNow, scaleX = value.DpiScaleX, scaleY = value.DpiScaleY,
                width = window.ActualWidth, height = window.ActualHeight });
        }
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(RecordDpi);
        app.Exit += (_, e) =>
        {
            string[] required = ["native-boundaries", "single-primary", "second-process-only-signals", "three-workspaces-start-overview",
                "native-tray-available", "readonly-directory-page-bounded", "readonly-directory-next-page", "scope-details-auxiliary-page",
                "return-restores-scope-page-row", "cancel-old-survey-before-scope-switch", "close-hides-keeps-observation",
                "restore-preserves-page", "global-pause-keeps-single-root-preference", "global-resume-only-unpaused-roots",
                "exit-fixture-three-eligible", "close-during-delete-hides-keeps-task", "explicit-exit-waits-current-item",
                "repeated-explicit-exit-reuses-pending-task", "repeated-close-during-exit-keeps-workspace-audit",
                "exit-finishes-audit-keeps-later-items", "classification-visible-without-scroll", "benchmark-card-protects-evidence",
                "codex-history-card-explains-loss", "empty-workbench-explains-next-step", "lazy-workspaces-open"];
            var records = checks.Select(c => JsonSerializer.SerializeToElement(c)).ToArray();
            var passed = !desktop && errors.Count == 0 && window.ExitReady && records.All(c => c.GetProperty("passed").GetBoolean()) &&
                required.All(name => records.Count(c => c.GetProperty("name").GetString() == name) == 1);
            if (!desktop && !passed) Environment.ExitCode = e.ApplicationExitCode = 1;
            var report = new { mode = desktop ? "isolated-actual-window-manual-input" : "isolated-WPF-component-lifecycle",
                fixture = box.RunDirectory, data, root, checks, errors, dpi,
                passed = desktop ? (bool?)null : passed, requiredAutomatedChecks = desktop ? [] : required,
                preflightPassed = desktop && records.Any(c => c.GetProperty("name").GetString() == "isolated-workspace" && c.GetProperty("passed").GetBoolean()),
                safeExitCompleted = window.ExitReady, installedUiAcceptance = false,
                limitation = "Component assertions are not keyboard usability, installed-product or multi-DPI acceptance. Controlled cleanup uses synthetic fixtures and a fixed context to test exit ordering only. No existing user files are deleted." };
            File.WriteAllText(Path.Combine(output, "workbench-lifecycle.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine(Path.Combine(output, "workbench-lifecycle.json"));
            Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", previousData);
        };
        window.Loaded += async (_, _) =>
        {
            ManualResetEventSlim? release = null;
            TaskCompletionSource<CleanupContext>? initialContext = null;
            try
            {
                await Until(() => window.Workspace is { } w && w.Monitors.Values.Any(m => m.HasContinuousObservation));
                RecordDpi();
                if (desktop)
                {
                    Check("isolated-workspace", window.Workspace!.DataDirectory == data, new { data, root });
                    Console.WriteLine(output);
                    return;
                }
                var workspace = window.Workspace!;
                Check("native-boundaries", SingleInstance.BoundarySelfCheck().Count == 0 && TrayIconHost.BoundarySelfCheck().Count == 0);
                var restored = 0;
                var single = SingleInstance.TryStart(data, app.Dispatcher, () => restored++, error => errors.Add(error.ToString()));
                Check("single-primary", single.Status == SingleInstance.StartStatus.Primary);
                using (single.Instance)
                {
                    var childReport = Path.Combine(output, "single-child.json");
                    var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "CClean.UiProbe.exe"))
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.CurrentDirectory };
                    start.ArgumentList.Add("--single-instance-child"); start.ArgumentList.Add(data); start.ArgumentList.Add(childReport);
                    using var child = Process.Start(start) ?? throw new IOException("Child process did not start.");
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    await Until(() => restored == 1);
                    Check("second-process-only-signals", child.ExitCode == 0 && restored == 1 && workspace.Monitors.Count == 1,
                        JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(childReport, Encoding.UTF8)));
                }
                var navigation = (ListBox)window.FindName("Navigation");
                Check("three-workspaces-start-overview", navigation.Items.Cast<ListBoxItem>().Select(i => i.Content?.ToString()).SequenceEqual(new[] { "空间与清理", "开发活动", "项目与版本" }) &&
                    ((ContentControl)window.FindName("PageContent")).Content is CClean.App.Views.OverviewView);
                Check("native-tray-available", window.TrayAvailable);
                var overview = (CClean.App.Views.OverviewView)((ContentControl)window.FindName("PageContent")).Content;
                overview.UpdateLayout();
                var overviewScroll = (ScrollViewer)overview.FindName("OverviewScroll");
                var classification = (FrameworkElement)overview.FindName("ClassificationLayout");
                var classificationY = classification.TransformToAncestor(overviewScroll).Transform(new Point()).Y;
                Check("classification-visible-without-scroll", classificationY + 100 < overviewScroll.ActualHeight,
                    new { classificationY, viewportHeight = overviewScroll.ActualHeight });
                var explanationRows = (DataGrid)overview.FindName("Results");
                var benchmarkRow = new SpaceFolder(ContentClassification.BenchmarkRoot, 0, 0, false, "", "");
                var historyRow = new SpaceFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions"), 0, 0, false, "", "");
                explanationRows.ItemsSource = new[] { benchmarkRow, historyRow };
                explanationRows.SelectedItem = benchmarkRow;
                Check("benchmark-card-protects-evidence", ((TextBlock)overview.FindName("PurposeText")).Text.Contains("index.db") &&
                    ((TextBlock)overview.FindName("ValueText")).Text.Contains("保留") && ((TextBlock)overview.FindName("ImpactText")).Text.Contains("不能恢复"));
                explanationRows.SelectedItem = historyRow;
                Check("codex-history-card-explains-loss", ((TextBlock)overview.FindName("ValueText")).Text.Contains("唯一历史") &&
                    ((TextBlock)overview.FindName("ImpactText")).Text.Contains("恢复"));
                explanationRows.ItemsSource = null;
                Check("empty-workbench-explains-next-step", ((TextBlock)overview.FindName("Advice")).Text.Contains("先盘点") &&
                    !((Button)overview.FindName("OpenFolderButton")).IsEnabled);
                var viewRoot = Path.Combine(box.RunDirectory, "view-samples");
                for (var i = 0; i < 123; i++)
                {
                    var folder = Path.Combine(viewRoot, "只读目录-" + i.ToString("D3"));
                    Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "sample.bin"), [1, 2, 3]);
                }
                overview.ShowScope(viewRoot);
                ((Button)overview.FindName("ScanButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await overview.WaitForPendingOperationsAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var rows = (DataGrid)overview.FindName("Results");
                Check("readonly-directory-page-bounded", rows.Items.Count == 100 && ((Button)overview.FindName("NextButton")).IsEnabled,
                    new { rows = rows.Items.Count, status = ((TextBlock)overview.FindName("PageStatus")).Text });
                ((Button)overview.FindName("NextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("readonly-directory-next-page", rows.Items.Count == 24 && !((Button)overview.FindName("NextButton")).IsEnabled);
                rows.SelectedIndex = 0;
                var selectedPath = ((TextBox)overview.FindName("SelectedPath")).Text;
                ((Button)overview.FindName("RegisterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("scope-details-auxiliary-page", ((ContentControl)window.FindName("PageContent")).Content is CClean.App.Views.ProjectSetupView);
                ((Button)window.FindName("ReturnButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("return-restores-scope-page-row", ReferenceEquals(overview, ((ContentControl)window.FindName("PageContent")).Content) &&
                    ((TextBox)overview.FindName("ScanRoot")).Text == viewRoot && ((TextBox)overview.FindName("SelectedPath")).Text == selectedPath && rows.Items.Count == 24);
                overview.ShowScope(viewRoot);
                ((Button)overview.FindName("ScanButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                overview.ShowScope(pausedRoot);
                await overview.WaitForPendingOperationsAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Check("cancel-old-survey-before-scope-switch", ((TextBox)overview.FindName("ScanRoot")).Text == pausedRoot && rows.Items.Count == 0);

                navigation.SelectedIndex = 1;
                var activityPage = ((ContentControl)window.FindName("PageContent")).Content;
                navigation.SelectedIndex = 2;
                Check("lazy-workspaces-open", activityPage is CClean.App.Views.ProjectWorkbenchView &&
                    ((ContentControl)window.FindName("PageContent")).Content is CClean.App.Views.ProjectWorkbenchView projectPage &&
                    !ReferenceEquals(activityPage, projectPage));
                var page = ((ContentControl)window.FindName("PageContent")).Content;
                var monitor = workspace.Monitors.Values.Single();
                window.Close();
                Check("close-hides-keeps-observation", !window.IsVisible && workspace.Monitors.Count == 1 && !workspace.Stopping);
                window.RestoreWindow();
                Check("restore-preserves-page", window.IsVisible && ReferenceEquals(page, ((ContentControl)window.FindName("PageContent")).Content) && navigation.SelectedIndex == 2);
                await workspace.SetObservationPaused(true);
                Check("global-pause-keeps-single-root-preference", workspace.ObservationPaused && workspace.Monitors.Count == 0 && workspace.Projects.Single(p => p.Id == paused.Id).Paused);
                await workspace.SetObservationPaused(false);
                await Until(() => workspace.Monitors.Values.Any(m => m.HasContinuousObservation));
                Check("global-resume-only-unpaused-roots", !workspace.ObservationPaused && workspace.Monitors.Count == 1 && workspace.Projects.Single(p => p.Id == paused.Id).Paused && !monitor.HasContinuousObservation);

                // Controlled test barrier on the existing executor hook, not a production observation exemption.
                var context = new CleanupContext(1, [new(root, CacheRule.NpmContent.Id, 1, "11.19.0", true, true)],
                    new ActivitySnapshot(1, [new(root, false, true, "fixed synthetic context: exit ordering only")]), [data]);
                var plan = CleanupPlanner.Preview(paths, context);
                Check("exit-fixture-three-eligible", plan.EligibleCount == 3);
                initialContext = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var firstContext = true;
                var execution = workspace.ExecuteCleanup(plan, () => firstContext ? First() : Task.FromResult(context));
                Task<CleanupContext> First() { firstContext = false; return initialContext.Task; }
                var executor = (CleanupExecutor?)typeof(WorkspaceViewModel).GetField("cleanupExecutor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)
                    ?? throw new InvalidOperationException("Executor was not created.");
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                release = new(false);
                var barrier = release;
                typeof(CleanupExecutor).GetProperty("TestCheckpoint", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(executor,
                    (Action<string>)(point => { if (point != "after-delete") return; reached.TrySetResult(); if (!barrier.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled delete barrier timed out."); }));
                initialContext.SetResult(context);
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var liveJournal = workspace.OpenCleanupJournal();
                window.Close();
                Check("close-during-delete-hides-keeps-task", !window.IsVisible && workspace.CleanupRunning && !workspace.Stopping);
                var exit = window.RequestExit();
                await Task.Delay(100);
                Check("explicit-exit-waits-current-item", !exit.IsCompleted && workspace.Stopping && workspace.CleanupRunning);
                var repeatedExit = window.RequestExit();
                Check("repeated-explicit-exit-reuses-pending-task", ReferenceEquals(exit, repeatedExit) && !repeatedExit.IsCompleted);
                var closedBeforeRelease = false;
                window.Closed += (_, _) => closedBeforeRelease = true;
                window.Close();
                window.Close();
                var retainedTotals = workspace.Store.ProjectTotals(workspace.Projects.Single(p => !p.Paused).ObservationKey);
                var pendingAudit = liveJournal.Events(plan.Id.ToString());
                var pendingRun = liveJournal.Runs().Single(r => r.Id == plan.Id.ToString());
                Check("repeated-close-during-exit-keeps-workspace-audit", !closedBeforeRelease && !window.ExitReady &&
                    ReferenceEquals(window.Workspace, workspace) && ReferenceEquals(exit, window.RequestExit()) && !exit.IsCompleted &&
                    workspace.Stopping && workspace.CleanupRunning && pendingRun.State == "Running" &&
                    pendingAudit.Count(e => e.State == "Started") == 1 && pendingAudit.All(e => e.State != "Deleted"),
                    new { storeFiles = retainedTotals.Files, auditState = pendingRun.State, auditEvents = pendingAudit.Count });
                window.Closed += (_, _) =>
                {
                    try
                    {
                        var result = execution.GetAwaiter().GetResult();
                        using var journal = new CleanupJournal(data);
                        Check("exit-finishes-audit-keeps-later-items", window.ExitReady && workspace.Monitors.Count == 0 && result.State == "Cancelled" && result.Deleted == 1 &&
                            paths.Skip(1).All(File.Exists) && journal.Events(plan.Id.ToString()).Count(e => e.State == "Deleted") == 1, result);
                    }
                    catch (Exception error) { errors.Add(error.ToString()); Environment.ExitCode = 1; }
                };
                release.Set();
            }
            catch (Exception error)
            {
                errors.Add(error.ToString()); Environment.ExitCode = 1;
                initialContext?.TrySetException(error); release?.Set();
                try { await window.RequestExit(); } catch (Exception closing) { errors.Add(closing.ToString()); app.Shutdown(1); }
            }
        };
        window.Show();
        app.Run();
    }

    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("WPF fixture did not reach the expected state.");
            await Task.Delay(20);
        }
    }
}
