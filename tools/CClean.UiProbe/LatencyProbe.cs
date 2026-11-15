using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CClean.App.Controls;
using CClean.App.Views;
using CClean.Core.Experiments;
using CClean.Core.Projects;

internal static class LatencyProbe
{
    public static void Run(Application app, string output)
    {
        using var box = new ExperimentSandbox(Path.Combine(output, "sandbox"));
        var project = Path.Combine(box.RunDirectory, "project"); Directory.CreateDirectory(project);
        var data = Path.Combine(box.RunDirectory, "data"); Directory.CreateDirectory(data);
        new ProjectRegistry(data).Register("延迟合成项目", project, "通用工具");
        var previousData = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR");
        Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", data);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new CClean.App.MainWindow(enableSessionDiscovery: false);
        app.MainWindow = window;
        var results = new List<double>();
        string? failure = null;
        var forcedProbeShutdown = false;
        app.Exit += (_, e) =>
        {
            results.Sort();
            var p95 = results.Count == 0 ? (double?)null : results[(int)Math.Ceiling(results.Count * .95) - 1];
            var passed = failure is null && results.Count == 30 && p95 <= 2000 && window.ExitReady && !forcedProbeShutdown;
            var report = new
            {
                mode = "real file write -> watcher -> SQLite -> development activity project timeline OnRender; 30 sequential synthetic small-file events; OS presentation and human input not measured",
                route = "开发活动 -> 合成项目模块按钮 -> TimelineContent",
                fixture = box.RunDirectory, data, project, count = results.Count,
                p95Milliseconds = p95, maximumMilliseconds = results.Count == 0 ? (double?)null : results[^1], failure,
                safeExitCompleted = window.ExitReady, forcedProbeShutdown, passed
            };
            File.WriteAllText(Path.Combine(output, "latency.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), System.Text.Encoding.UTF8);
            Console.WriteLine(JsonSerializer.Serialize(report));
            Environment.ExitCode = e.ApplicationExitCode = passed ? 0 : 1;
        };
        var attached = false;
        window.Loaded += async (_, _) =>
        {
            if (attached) return;
            attached = true;
            try
            {
                var host = (ContentControl)window.FindName("PageContent");
                await Until(() => host.Content is OverviewView && window.Workspace is { } workspace &&
                    workspace.DataDirectory == data && workspace.Monitors.Count == 1 &&
                    workspace.Monitors.Values.All(m => m.HasContinuousObservation), "Isolated workspace did not establish its watcher baseline.");
                var navigation = (ListBox)window.FindName("Navigation");
                navigation.SelectedItem = navigation.Items.Cast<ListBoxItem>().Single(i => Equals(i.Content, "开发活动"));
                await Until(() => host.Content is ProjectWorkbenchView, "Development activity workspace did not open.");
                var workbench = (ProjectWorkbenchView)host.Content;
                var modules = (ItemsControl)workbench.FindName("Modules");
                Button? moduleButton = null;
                await Until(() =>
                {
                    window.UpdateLayout();
                    moduleButton = Descendants<Button>(modules).FirstOrDefault(b => b.Tag is ProjectModule module &&
                        module.Root.Equals(project, StringComparison.OrdinalIgnoreCase));
                    return moduleButton is not null;
                }, "Synthetic project module button was not generated.");
                moduleButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, moduleButton));
                await workbench.WaitForPendingOperationsAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if (((ContentControl)workbench.FindName("TimelineContent")).Content is not TimelineView view)
                    throw new IOException("Project TimelineContent did not contain a timeline.");
                var graph = (TimelineCanvas)view.FindName("Graph");
                await Until(() => view.IsVisible && graph.IsVisible, "Project timeline graph did not become visible.");
                await Dispatcher.Yield(DispatcherPriority.Render);
                for (var i = 0; i < 30; i++)
                {
                    var path = Path.Combine(project, $"event-{i:D3}.bin");
                    var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Rendered(IReadOnlyList<CClean.Core.History.TimelineNode> nodes)
                    { if (nodes.Any(n => n.Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))) frame.TrySetResult(); }
                    graph.FrameRendered += Rendered;
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        await File.WriteAllBytesAsync(path, new byte[100]);
                        await frame.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        results.Add(watch.Elapsed.TotalMilliseconds);
                    }
                    finally { graph.FrameRendered -= Rendered; }
                    await Task.Delay(150);
                }
            }
            catch (Exception error) { failure = error.ToString(); }
            finally
            {
                try
                {
                    await window.RequestExit();
                    if (!window.ExitReady) throw new IOException("MainWindow did not complete safe exit.");
                }
                catch (Exception error)
                {
                    failure = (failure is null ? "" : failure + "\n") + "Exit: " + error;
                    forcedProbeShutdown = true;
                    app.Shutdown(1); // Isolated probe only; the report explicitly fails an incomplete drain.
                }
            }
        };
        try { app.Run(window); }
        finally { Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", previousData); }
    }

    private static async Task Until(Func<bool> ready, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(failure);
            await Task.Delay(25);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
