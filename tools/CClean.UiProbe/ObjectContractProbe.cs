using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using CClean.App.ViewModels;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;

internal static class ObjectContractProbe
{
    internal static async Task Run(string output)
    {
        Application.Current.Dispatcher.VerifyAccess();
        using var box = new ExperimentSandbox(Path.Combine(output, "object-contract-sandbox"));
        var savedData = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR");
        var checks = new List<string>();
        WorkspaceViewModel? workspace = null;
        var passed = false; string? failure = null;
        object? consumerEvidence = null;
        var productionExecutionQualified = false;
        var expectedRejection = false;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        try
        {
            var root = Path.Combine(box.RunDirectory, "generated-cache");
            var content = Path.Combine(root, "_cacache", "content-v2");
            Directory.CreateDirectory(content);
            var files = Enumerable.Range(0, 3).Select(i => Path.Combine(content, $"generated-{i}.bin")).ToArray();
            foreach (var file in files)
            {
                File.WriteAllBytes(file, [1, 2, 3]);
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
            }
            Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", Path.Combine(box.RunDirectory, "data"));
            workspace = new(enableSessionDiscovery: false) { ObjectReviewForIsolatedValidation = true };
            await workspace.Register("对象契约隔离样本", root, "通用工具");
            var project = workspace.Projects.Single();
            await Until(() => workspace.Monitors[project.Id].HasContinuousObservation);
            workspace.Rules.ConfigureNpm(root, "11.19.0", enabled: true, exclusive: true);
            var context = await workspace.CleanupContext();
            Check(context.Contract == CleanupContract.ObjectReviewV1 && context.Observations.Count == 1, "Workspace publishes the explicit contract and real observer");
            Check(context.IndependentActivity is not null, "Independent activity is available without replacing display activity");
            var consumers = CleanupConsumers.Observe(workspace.Rules.Scopes);
            consumerEvidence = consumers;
            // This machine has running Node processes. Test the real conservative gate, never inject absence.
            Check(consumers.Any(s => s.Active || !s.ObservationKnown), "Real consumer enumeration requires protection on this host");
            Check(context.IndependentActivity!.Scopes.Any(s => s.Active || !s.ObservationKnown), "Workspace carries real consumer protection into object review");
            var plan = CleanupPlanner.Preview(files, context);
            Check(plan.EligibleCount == 0, "Real consumers prevent approval of npm-shaped generated fixtures");
            var refused = false;
            try { await workspace.ExecuteCleanup(plan); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused && files.All(File.Exists), "Actual Workspace execution refuses the unqualified manifest and preserves files");
            expectedRejection = refused;
            var monitor = workspace.Monitors[project.Id];
            var before = monitor.Observe().Sequence;
            File.SetAttributes(Path.GetDirectoryName(files[0])!, FileAttributes.Hidden);
            await Until(() => monitor.Observe().Sequence > before);
            var changed = await workspace.CleanupContext();
            Check(workspace.Activity().Scopes.Any(s => s.Active && s.Reason.Contains("近期")), "Raw recent activity remains visible");
            Check(changed.Observations[0].Evidence.Events.Any(e => e.Path == content && e.Kind == WatcherChangeTypes.Changed), "Raw parent metadata notification is retained");
            var session = workspace.Sessions.Begin(project, "通用工具", "隔离活动会话");
            var active = await workspace.CleanupContext();
            Check(active.IndependentActivity!.Scopes.Any(s => s.Active && s.Reason.Contains("手动")), "Object review preserves live session protection");
            workspace.Sessions.End(session.Id);
            await workspace.TogglePause(project);
            var paused = await workspace.CleanupContext();
            Check(paused.Observations.Count == 0 && paused.IndependentActivity!.Scopes.Any(s => !s.ObservationKnown), "Paused observation cannot retain cleanup permission");
            Check(files.All(File.Exists), "All unapproved fixture objects remain intact");
            await CloseWorkspace(); workspace = null;
            Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", Path.Combine(box.RunDirectory, "default-data"));
            workspace = new(enableSessionDiscovery: false);
            Check((await workspace.CleanupContext()).Contract == CleanupContract.StrictActivity, "Ordinary product Workspace retains strict activity until per-rule qualification");
            passed = true;
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            try { if (workspace is not null) await CloseWorkspace(); }
            catch (Exception error) { passed = false; failure += "\nShutdown: " + error; throw; }
            finally
            {
                Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", savedData);
                File.WriteAllText(Path.Combine(output, "object-contract.json"), JsonSerializer.Serialize(new
                {
                    passed, failure, checks, consumerEvidence, productionExecutionQualified, fixtureRoot = box.RunDirectory,
                    componentOnly = true, installedUiAcceptance = false,
                    limitation = "Real Workspace/Dispatcher/consumer/observer protection checks; no positive production cleanup on this host with active Node consumers. No consumer absence injection. Generated npm-shaped fixtures do not qualify npm content/index compatibility or the benchmark rule."
                }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
        }

        async Task CloseWorkspace()
        {
            try { await workspace!.ShutdownAsync(); }
            catch (AggregateException error) when (expectedRejection && error.InnerExceptions.Count == 1 &&
                error.InnerExceptions[0] is InvalidOperationException && workspace!.CleanupTask is { IsFaulted: true })
            {
                // Workspace intentionally reports earlier task failures while draining shutdown.
                checks.Add("Shutdown drains and reports the earlier rejected cleanup task");
                expectedRejection = false;
            }
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        if (!condition()) throw new TimeoutException("Real observation did not reach the expected state.");
    }
}
