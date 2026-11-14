using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;
using CClean.App.Views;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Storage;

internal static class CleanupPreviewLifecycleProbe
{
    internal static async Task Run(string output)
    {
        using var box = new ExperimentSandbox(Path.Combine(Path.GetTempPath(), "cclean-preview-lifecycle"));
        var saved = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR");
        WorkspaceViewModel? workspace = null;
        CleanupPreviewView? view = null;
        var checks = new List<string>();
        var releases = new List<TaskCompletionSource>();
        var passed = false; string? failure = null;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        TaskCompletionSource Barrier()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releases.Add(source); return source;
        }
        try
        {
            Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", Path.Combine(box.RunDirectory, "data"));
            workspace = new(enableSessionDiscovery: false) { ObjectReviewForIsolatedValidation = true };
            var root = Path.Combine(box.RunDirectory, "wsl-crashes"); Directory.CreateDirectory(root);
            var paths = Enumerable.Range(0, 4).Select(i => Path.Combine(root, $"wsl-crash-isolated-{i}.dmp")).ToArray();
            foreach (var path in paths)
            {
                File.WriteAllBytes(path, [1, 2, 3]);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-72));
            }
            await workspace.Register("预览生命周期隔离样本", root, "通用工具");
            var monitor = workspace.Monitors.Values.Single();
            await Until(() => monitor.HasContinuousObservation);
            // Isolated host-dump fixture: real Workspace activity/observers are retained, no npm consumer gate is bypassed.
            // Timestamp is synthetic; this does not qualify natural retention or a production rule entry.
            async Task<CleanupContext> Context()
            {
                var context = await workspace.CleanupContext();
                return context with
                {
                    Rules = [new(root, CrashDumpRule.Id, CrashDumpRule.Version, "host-dump-v1", true, true)],
                    ProtectedRoots = context.ProtectedRoots.Append(paths[^1]).ToArray()
                };
            }
            view = new(workspace);
            Button Execute() => (Button)view.FindName("ExecuteButton");
            TextBlock Summary() => (TextBlock)view.FindName("Summary");
            var rows = (DataGrid)view.FindName("Items");
            await view.Preview(paths, Context);
            Check(rows.Items.Count == 4 && Execute().IsEnabled && paths.All(File.Exists), "Preview shows fixed objects without deleting");
            view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Check(rows.Items.Count == 0 && !Execute().IsEnabled, "Unloading clears unapproved manifest and disables execution");
            await view.Preview(paths, Context);
            view.RefreshApproval(DateTimeOffset.UtcNow.AddMinutes(6));
            Check(!Execute().IsEnabled && Summary().Text.Contains("失效"), "Expiry disables approval and explains re-preview (controlled clock)");
            await view.Preview(paths, () => Task.FromException<CleanupContext>(new IOException("controlled context failure")));
            Check(!Execute().IsEnabled && rows.Items.Count == 0 && Summary().Text.Contains("失败"), "Context failure cannot leave an actionable old preview");

            var entered = Barrier(); var release = Barrier();
            var preview = view.Preview(paths, async () => { entered.TrySetResult(); await release.Task; return await Context(); });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var secondReads = 0;
            await view.Preview(paths, () => { secondReads++; return Context(); });
            Check(secondReads == 0, "A second preview cannot replace the running context");
            view.CancelPendingOperations();
            var drain = view.WaitForPendingOperationsAsync();
            Check(!drain.IsCompleted && !Execute().IsEnabled, "Preview drain waits for the pending context after cancellation");
            release.TrySetResult(); await preview.WaitAsync(TimeSpan.FromSeconds(10)); await drain;
            Check(rows.Items.Count == 0 && !Execute().IsEnabled && paths.All(File.Exists), "Cancelled completion does not republish candidates");

            var unknown = CleanupPreviewView.DescribeSpaceChange([new("Z:\\", null, "离线")], [new("Z:\\", null, "离线")]);
            Check(unknown.Contains("未知") && !unknown.Contains("变化 0") && unknown.Contains("审计"), "Space sampling failure stays unknown and preserves audit meaning");
            var sample = new VolumeSpace(100, 1000, 100, DateTimeOffset.UtcNow);
            var negative = CleanupPreviewView.DescribeSpaceChange([new("C:\\", sample, null)], [new("C:\\", sample with { UserAvailableBytes = 80 }, null)]);
            Check(negative.Contains("-20") && negative.Contains("其他进程"), "Negative space movement remains visible without false attribution");

            var first = CleanupPlanner.Preview(paths.Take(1), await Context());
            var sampleEntered = Barrier(); var sampleRelease = Barrier();
            view.BeforeSpaceSampling = async () => { sampleEntered.TrySetResult(); await sampleRelease.Task; };
            var beforeStart = view.ExecuteConfirmed(first);
            await sampleEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var messageBefore = Summary().Text;
            ((Button)view.FindName("CancelPreviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Summary().Text == messageBefore, "Cancel preview cannot falsely report cancellation after approval");
            ((Button)view.FindName("StopExecutionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            sampleRelease.TrySetResult(); await beforeStart.WaitAsync(TimeSpan.FromSeconds(10));
            view.BeforeSpaceSampling = null;
            Check(workspace.CleanupTask is null && paths.All(File.Exists) && Summary().Text.Contains("开始前停止"),
                "Stop requested during pre-execution space sampling prevents starting deletion");
            Check((await workspace.ExecuteCleanup(first, Context)).State == "Completed", "Workspace executes one approved isolated dump fixture");
            await Until(() => monitor.Snapshot.Files.Count == 3);
            var next = await workspace.PreviewNextCleanup(paths.Skip(1).Take(1), Context);
            Check(next.EligibleCount == 1 && File.Exists(paths[1]) && workspace.OpenCleanupJournal().Runs().Count == 1,
                "Workspace continuation preview remains unapproved and creates no run");
            Check((await workspace.ExecuteCleanup(next, Context)).State == "Completed", "Workspace executes the separately approved continuation fixture");
            await Until(() => monitor.Snapshot.Files.Count == 2);
            Check(File.Exists(paths[^1]), "Explicitly preserved fixture survives both batches");

            var nextEntered = Barrier(); var nextRelease = Barrier();
            var pendingNext = workspace.PreviewNextCleanup(paths.Skip(2).Take(1), async () =>
            { nextEntered.TrySetResult(); await nextRelease.Task; return await Context(); });
            await nextEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(workspace.CleanupBusy && !workspace.CleanupRunning, "Workspace owns the pending cross-batch preview");
            var refused = false;
            try { await workspace.ExecuteCleanup(first, Context); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "Execution cannot overlap a cross-batch preview");
            workspace.BeginShutdown();
            var shutdown = workspace.ShutdownAsync();
            Check(!shutdown.IsCompleted && workspace.OpenForProbe(), "Shutdown waits before disposing resources used by preview");
            var readsAfterStop = 0;
            await view.Preview(paths, () => { readsAfterStop++; return Context(); });
            Check(readsAfterStop == 0, "Stopped Workspace cannot start another view preview");
            nextRelease.TrySetResult();
            try { await pendingNext; } catch (OperationCanceledException) { }
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            Check(pendingNext.IsCanceled && paths.Skip(2).All(File.Exists), "Shutdown drains cancelled continuation without deleting remaining objects");
            workspace = null;

            Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", Path.Combine(box.RunDirectory, "ordinary-data"));
            workspace = new(enableSessionDiscovery: false);
            refused = false;
            try { await workspace.PreviewNextCleanup(paths); } catch (InvalidOperationException) { refused = true; }
            Check(refused && workspace.CleanupPreviewTask is null, "Ordinary Workspace cannot enter unqualified continuation");
            passed = true;
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            foreach (var release in releases) release.TrySetResult();
            try
            {
                if (view is not null) { view.CancelPendingOperations(); await view.WaitForPendingOperationsAsync(); }
                if (workspace is not null) await workspace.ShutdownAsync();
            }
            finally
            {
                Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", saved);
                File.WriteAllText(Path.Combine(output, "cleanup-preview-lifecycle.json"), JsonSerializer.Serialize(new
                {
                    passed, failure, checks, fixtureRoot = box.RunDirectory, componentOnly = true,
                    installedUiAcceptance = false, productionRuleQualified = false,
                    limitation = "WPF controls, real Workspace and watcher, generated host-dump-shaped fixtures with synthetic old timestamps; no natural retention, real npm rebuild, installed desktop or consumer absence injection."
                }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
        }
    }
    private static bool OpenForProbe(this WorkspaceViewModel workspace)
    {
        workspace.Store.ProjectTotals("no-project"); return true;
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        if (!condition()) throw new TimeoutException("Watcher did not settle.");
    }
}
