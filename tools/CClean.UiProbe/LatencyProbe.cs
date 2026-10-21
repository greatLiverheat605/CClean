using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
        Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", data);
        app.StartupUri = new Uri("/CClean.App;component/MainWindow.xaml", UriKind.Relative);
        var attached = false;
        app.Activated += async (_, _) =>
        {
            if (attached || app.MainWindow is not CClean.App.MainWindow window) return; attached = true;
            var results = new List<double>(); string? failure = null;
            try
            {
                var host = (ContentControl)window.FindName("PageContent");
                for (var attempt = 0; attempt < 100 && host.Content is not OverviewView; attempt++) await Task.Delay(100);
                if (host.Content is not OverviewView) throw new IOException("Workspace did not initialize.");
                ((ListBox)window.FindName("Navigation")).SelectedIndex = 1;
                await Task.Delay(1000);
                if (host.Content is not TimelineView view) throw new IOException("Timeline is not visible.");
                var graph = (TimelineCanvas)view.FindName("Graph");
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
            catch (Exception error) { failure = error.Message; }
            results.Sort();
            var report = new { mode = "real file write -> watcher -> SQLite -> timeline OnRender; 30 sequential small-file events; OS presentation not measured", count = results.Count,
                p95Milliseconds = results.Count == 0 ? (double?)null : results[(int)Math.Ceiling(results.Count * .95) - 1], maximumMilliseconds = results.LastOrDefault(), failure,
                passed = failure is null && results.Count == 30 && results[28] <= 2000 };
            File.WriteAllText(Path.Combine(output, "latency.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), System.Text.Encoding.UTF8);
            Console.WriteLine(JsonSerializer.Serialize(report)); window.Close(); app.Shutdown(report.passed ? 0 : 1);
        };
        app.Run();
    }
}
