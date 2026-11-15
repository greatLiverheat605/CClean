using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using CClean.App.ViewModels;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;

internal static class WorkspaceShutdownProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static async Task<object> Run(string output)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("A WPF application is required.");
        dispatcher.VerifyAccess();
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "The probe must run on the WPF STA Dispatcher.");
        using var box = new ExperimentSandbox(Path.Combine(output, "workspace-shutdown-sandbox"));
        var previousData = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR");
        var started = DateTimeOffset.UtcNow;
        var scenarios = new List<object>();
        var reportPath = Path.Combine(output, "workspace-shutdown.json");
        object Report(bool passed, string? failure) => new
        {
            mode = "isolated Workspace shutdown races on the WPF Dispatcher",
            componentOnly = true, installedUiAcceptance = false,
            startedUtc = started, finishedUtc = DateTimeOffset.UtcNow, fixtureRoot = box.RunDirectory,
            sessionDiscovery = "disabled", scenarios, passed, failure,
            limitation = "Controlled startup/save barriers only; no approved deletion or cleanup execution. Existing user files are not accessed."
        };
        void Save(object report) => File.WriteAllText(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        try
        {
            scenarios.Add(await StartBarrier());
            scenarios.Add(await SaveBarrier());
            var report = Report(true, null);
            Save(report);
            return report;
        }
        catch (Exception error)
        {
            Save(Report(false, error.ToString()));
            throw;
        }
        finally { Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", previousData); }

        async Task<object> StartBarrier()
        {
            var data = Path.Combine(box.RunDirectory, "start-data");
            var root = FixtureRoot("start-root");
            var rejectedRoot = FixtureRoot("rejected-root");
            Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", data);
            var workspace = new WorkspaceViewModel(enableSessionDiscovery: false);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.BeforeMonitorStart = async () => { entered.TrySetResult(); await release.Task; };
            Task? registering = null;
            try
            {
                registering = workspace.Register("退出时正在启动的隔离范围", root, "通用工具");
                await entered.Task.WaitAsync(Timeout);
                Check(workspace.Projects.Count == 1 && workspace.Monitors.Count == 0, "The startup barrier must precede watcher publication.");
                // This real, empty preview cannot delete anything, even if the stopping guard regresses.
                var emptyPreview = CleanupPlanner.Preview([], await workspace.CleanupContext());
                Check(emptyPreview.EligibleCount == 0 && emptyPreview.Items.Count == 0, "The rejection probe must have no cleanup candidates.");
                workspace.BeginShutdown();
                Check(workspace.Stopping, "BeginShutdown did not synchronously reject new work.");
                await ExpectStopped(() => workspace.Register("退出后拒绝登记", rejectedRoot, "通用工具"));
                await ExpectStopped(() => workspace.ExecuteCleanup(emptyPreview));
                await workspace.Resume().WaitAsync(Timeout);
                Check(workspace.Projects.Count == 1 && workspace.Monitors.Count == 0 && workspace.CleanupTask is null,
                    "Register, ExecuteCleanup or Resume accepted new work after BeginShutdown.");
                var shutdown = workspace.ShutdownAsync();
                Check(!shutdown.IsCompleted && !registering.IsCompleted && !release.Task.IsCompleted,
                    "Shutdown completed while its watcher startup was still blocked.");
                // A real SQLite read must remain available until the pending start has drained.
                var beforeRelease = workspace.Store.ProjectTotals(workspace.Projects.Single().ObservationKey);
                release.TrySetResult();
                await Task.WhenAll(registering, shutdown).WaitAsync(Timeout);
                Check(workspace.Monitors.Count == 0 && workspace.CleanupTask is null && FilesIntact(root, rejectedRoot),
                    "A stale startup published a watcher after shutdown, or fixture files changed.");
                CheckStoreClosed(workspace);
                await workspace.ShutdownAsync().WaitAsync(Timeout);
                return new
                {
                    id = "shutdown-during-monitor-start", passed = true, dataDirectory = data,
                    rejectedRegister = true, rejectedExecuteCleanup = true, resumedWithoutStarting = true,
                    waitedForStartupBarrier = true, storeReadableBeforeRelease = true, indexedFilesBeforeRelease = beforeRelease.Files,
                    watchersAfterShutdown = workspace.Monitors.Count, cleanupTaskCreated = false, fixtureFilesIntact = true
                };
            }
            finally
            {
                release.TrySetResult(); workspace.BeforeMonitorStart = null;
                await workspace.ShutdownAsync().WaitAsync(Timeout);
                if (registering is not null) await registering.WaitAsync(Timeout);
            }
        }

        async Task<object> SaveBarrier()
        {
            var data = Path.Combine(box.RunDirectory, "save-data");
            var root = FixtureRoot("save-root");
            Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", data);
            var workspace = new WorkspaceViewModel(enableSessionDiscovery: false);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await workspace.Register("退出时正在保存的隔离范围", root, "通用工具").WaitAsync(Timeout);
                var project = workspace.Projects.Single();
                await Until(() => workspace.Monitors.TryGetValue(project.Id, out var monitor) && monitor.HasContinuousObservation &&
                    workspace.Store.ProjectTotals(project.ObservationKey).Files == 1,
                    "The real watcher baseline was not saved before the shutdown probe.");
                var watcher = workspace.Monitors[project.Id];
                workspace.BeforeSnapshotSave = async id =>
                {
                    if (id != project.Id || entered.Task.IsCompleted) return;
                    entered.TrySetResult();
                    await release.Task;
                };
                await entered.Task.WaitAsync(Timeout);
                workspace.BeginShutdown();
                var shutdown = workspace.ShutdownAsync();
                Check(workspace.Stopping && !shutdown.IsCompleted && !release.Task.IsCompleted,
                    "Shutdown completed while a real timer save batch was blocked.");
                var totals = workspace.Store.ProjectTotals(project.ObservationKey);
                var rows = workspace.Store.LoadSnapshots(project.ObservationKey);
                Check(totals.Files == 1 && rows.Count == 1 && workspace.Monitors.Count == 1,
                    "Storage or its watcher was released before the blocked save batch drained.");
                release.TrySetResult();
                await shutdown.WaitAsync(Timeout);
                Check(workspace.Monitors.Count == 0 && !watcher.HasContinuousObservation && workspace.CleanupTask is null && FilesIntact(root),
                    "Shutdown did not retire its watcher after saving, or fixture files changed.");
                CheckStoreClosed(workspace);
                return new
                {
                    id = "shutdown-during-snapshot-save", passed = true, dataDirectory = data,
                    trigger = "real 500 ms DispatcherTimer and BeforeSnapshotSave barrier",
                    waitedForSaveBarrier = true, storeReadableBeforeRelease = true, indexedFilesBeforeRelease = totals.Files,
                    watchersAfterShutdown = workspace.Monitors.Count, storeClosedAfterShutdown = true,
                    cleanupTaskCreated = false, fixtureFilesIntact = true
                };
            }
            finally
            {
                release.TrySetResult(); workspace.BeforeSnapshotSave = null;
                await workspace.ShutdownAsync().WaitAsync(Timeout);
            }
        }

        string FixtureRoot(string name)
        {
            var root = Path.Combine(box.RunDirectory, name);
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "sample.bin"), [0x11, 0x22, 0x33]);
            return root;
        }
        bool FilesIntact(params string[] roots) => roots.All(root =>
            File.ReadAllBytes(Path.Combine(root, "sample.bin")).SequenceEqual(new byte[] { 0x11, 0x22, 0x33 }));
    }

    private static async Task ExpectStopped(Func<Task> action)
    {
        try { await action().WaitAsync(Timeout); }
        catch (InvalidOperationException error) when (error.Message.Contains("安全退出", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("A new operation was not rejected by the stopping guard.");
    }

    private static void CheckStoreClosed(WorkspaceViewModel workspace)
    {
        try { workspace.Store.ProjectTotals("shutdown-probe"); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Storage remains usable after ShutdownAsync completed.");
    }

    private static async Task Until(Func<bool> condition, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > Timeout) throw new TimeoutException(failure);
            await Task.Delay(25);
        }
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }
}
