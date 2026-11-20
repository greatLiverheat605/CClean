using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using CClean.App;

internal static class WorkspaceCapacityProbe
{
    public static void Run(string sampleRoot, string dataRoot, string reportPath)
    {
        sampleRoot = Path.GetFullPath(sampleRoot);
        dataRoot = Path.GetFullPath(dataRoot);
        reportPath = Path.GetFullPath(reportPath);
        var artifacts = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        if (!reportPath.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) ||
            !dataRoot.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) || File.Exists(reportPath) ||
            !File.Exists(Path.Combine(Path.GetDirectoryName(sampleRoot)!, ".cclean-sandbox")) ||
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(sampleRoot)!, ".cclean-sandbox")) != "CCLEAN-20260917-DEV" ||
            Directory.Exists(dataRoot)) throw new InvalidOperationException("Capacity probe requires a new report/data path and an owned isolated sample.");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", dataRoot);
        using var writer = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        using var process = Process.GetCurrentProcess();
        var watch = Stopwatch.StartNew();
        var stage = "startup";
        MainWindow? activeWindow = null;
        var stop = new CancellationTokenSource();
        var sampleSync = new object();
        void Sample(string phase, MainWindow? window = null)
        {
            lock (sampleSync)
            {
                process.Refresh();
                var monitor = window?.Workspace?.Monitors.Values.SingleOrDefault();
                var retention = monitor is not null && window?.Workspace is { } workspace
                    ? workspace.SnapshotRetention(workspace.Monitors.Keys.Single()) : default;
                var discovered = window?.Workspace?.DiscoveredSessions.Count;
                var discoveryStatus = window?.Workspace?.DiscoveryStatus;
                writer.WriteLine(JsonSerializer.Serialize(new
                {
                    phase, seconds = watch.Elapsed.TotalSeconds, workingSetBytes = process.WorkingSet64,
                    peakWorkingSetBytes = process.PeakWorkingSet64, privateBytes = process.PrivateMemorySize64,
                    managedBytes = GC.GetTotalMemory(false), heapBytes = GC.GetGCMemoryInfo().HeapSizeBytes,
                    allocatedBytes = GC.GetTotalAllocatedBytes(), cpuSeconds = process.TotalProcessorTime.TotalSeconds,
                    revision = monitor?.Revision, snapshotFiles = monitor?.Snapshot.Files.Count,
                    previousFiles = retention.Files, previousSharesCurrent = retention.SharesCurrent,
                    discoveredSessions = discovered, discoveryStatus,
                    scanMs = monitor?.ScanTime.TotalMilliseconds, applicationGaps = monitor?.ApplicationGaps,
                    nativeGaps = monitor?.NativeGaps, scanGaps = monitor?.ScanGaps
                }));
            }
        }
        var sampling = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                Sample(Volatile.Read(ref stage), Volatile.Read(ref activeWindow));
                try { await Task.Delay(2000, stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        var app = new App();
        app.InitializeComponent();
        var failed = false;
        app.Startup += (_, _) =>
        {
            app.Dispatcher.BeginInvoke(async () =>
            {
                if (app.MainWindow is not MainWindow window)
                {
                    writer.WriteLine(JsonSerializer.Serialize(new { phase = "failed", error = "Main window unavailable after startup." }));
                    failed = true;
                    app.Shutdown(1);
                    return;
                }
                try
                {
                    async Task Until(Func<bool> ready, int minutes = 10)
                    {
                        var deadline = DateTimeOffset.UtcNow.AddMinutes(minutes);
                        while (!ready())
                        {
                            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Capacity phase timed out.");
                            await Task.Delay(250);
                        }
                    }
                    await Until(() => window.Workspace is not null, 2);
                    var workspace = window.Workspace!;
                    Volatile.Write(ref activeWindow, window);
                    var automaticCalibrationRequested = false;
                    workspace.BeforeCalibrationRescan = () => { automaticCalibrationRequested = true; Sample("automatic-calibration-requested", window); };
                    workspace.SessionsDiscovered += () => Sample("sessions-discovered", window);
                    Sample("startup-loaded", window);
                    Volatile.Write(ref stage, "scan-and-index");
                    var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    workspace.Indexed += () => indexed.TrySetResult();
                    await workspace.Register("capacity sample", sampleRoot, "generic");
                    var monitor = workspace.Monitors.Values.Single();
                    monitor.BeforeScan = () => Sample("scan-start", window);
                    await Until(() => monitor.Revision > 0);
                    if (monitor.Snapshot.Files.Count != 100_000 || !monitor.HasContinuousObservation)
                        throw new IOException("Initial 100,000-object scan has a coverage gap.");
                    Sample("scan-complete", window);
                    await indexed.Task.WaitAsync(TimeSpan.FromMinutes(10));
                    if (workspace.Store.CurrentTotals(sampleRoot, workspace.CurrentObservationKeys).Files != 100_000)
                        throw new IOException("The indexed 100,000-object baseline is incomplete.");
                    Sample("indexed", window);
                    Volatile.Write(ref stage, "calibration");
                    await Until(() => automaticCalibrationRequested && monitor.Revision > 1);
                    if (monitor.Snapshot.Files.Count != 100_000 || !monitor.HasContinuousObservation)
                        throw new IOException("Calibration has a coverage gap.");
                    Sample("calibration-scan-complete", window);
                    await Until(() => workspace.SnapshotRetention(workspace.Monitors.Keys.Single()).SharesCurrent);
                    Sample("calibration-indexed", window);
                    Volatile.Write(ref stage, "idle");
                    var idleOutput = Path.ChangeExtension(reportPath, "idle.json");
                    var idle = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var argument in new[] { "-NoProfile", "-File", Path.GetFullPath("tools/Measure-AppIdle.ps1"),
                        "-AppProcessId", Environment.ProcessId.ToString(), "-DataDirectory", dataRoot,
                        "-Output", idleOutput, "-Seconds", "600" }) idle.ArgumentList.Add(argument);
                    using var idleProcess = Process.Start(idle) ?? throw new IOException("Idle measurement did not start.");
                    await idleProcess.WaitForExitAsync();
                    var idleStatus = await idleProcess.StandardOutput.ReadToEndAsync();
                    var idleError = await idleProcess.StandardError.ReadToEndAsync();
                    Sample("idle-ten-minutes", window);
                    var forcedIdleFailure = Environment.GetEnvironmentVariable("CCLEAN_UI_PROBE_FORCE_IDLE_FAILURE") == "1";
                    lock (sampleSync) writer.WriteLine(JsonSerializer.Serialize(new
                    { phase = "idle-protocol", idleOutput, idleProcess.ExitCode, idleStatus, idleError, forcedIdleFailure }));
                    if (idleProcess.ExitCode != 0) { failed = true; Environment.ExitCode = 1; }
                    await window.RequestExit();
                }
                catch (Exception error)
                {
                    lock (sampleSync) writer.WriteLine(JsonSerializer.Serialize(new { phase = "failed", error = error.ToString() }));
                    failed = true; Environment.ExitCode = 1;
                    await window.RequestExit();
                }
            });
        };
        app.Run();
        stop.Cancel();
        sampling.GetAwaiter().GetResult();
        Sample("exited");
        if (failed) Environment.ExitCode = 1;
    }
}
