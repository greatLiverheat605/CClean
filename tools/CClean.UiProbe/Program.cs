using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CClean.App.Controls;
using CClean.Core.History;
using CClean.Core.Storage;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--workspace-capacity", var sampleRoot, var dataRoot, var reportPath])
        { WorkspaceCapacityProbe.Run(sampleRoot, dataRoot, reportPath); return; }
        if (args is ["--single-instance-child", var childData, var childReport])
        { WorkbenchLifecycleProbe.InstanceChild(childData, childReport); return; }
        var output = Path.GetFullPath(Path.Combine("artifacts", "ui-probe-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")));
        Directory.CreateDirectory(output);
        if (args is ["--cleanup-preview-lifecycle"])
        {
            var previewApp = ScopeLifecycleProbe.CreateApplication();
            previewApp.Dispatcher.BeginInvoke(async () =>
            {
                try { await CleanupPreviewLifecycleProbe.Run(output); Console.WriteLine(Path.Combine(output, "cleanup-preview-lifecycle.json")); }
                catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
                finally { previewApp.Shutdown(Environment.ExitCode); }
            });
            previewApp.Run(); return;
        }
        if (args is ["--object-contract"])
        {
            var contractApp = ScopeLifecycleProbe.CreateApplication();
            contractApp.Dispatcher.BeginInvoke(async () =>
            {
                try { await ObjectContractProbe.Run(output); Console.WriteLine(Path.Combine(output, "object-contract.json")); }
                catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
                finally { contractApp.Shutdown(Environment.ExitCode); }
            });
            contractApp.Run();
            return;
        }
        if (args is ["--history-desktop"])
        {
            HistoryPagingProbe.RunDesktop(ScopeLifecycleProbe.CreateApplication(), output);
            return;
        }
        if (args is ["--history-paging"])
        {
            var historyApp = ScopeLifecycleProbe.CreateApplication();
            historyApp.Dispatcher.BeginInvoke(async () =>
            {
                try { await HistoryPagingProbe.Run(output); Console.WriteLine(Path.Combine(output, "history-paging.json")); }
                catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
                finally { historyApp.Shutdown(Environment.ExitCode); }
            });
            historyApp.Run();
            return;
        }
        if (args is ["--preferences"])
        {
            var preferencesApp = ScopeLifecycleProbe.CreateApplication();
            preferencesApp.Dispatcher.BeginInvoke(async () =>
            {
                try { await PreferencesProbe.Run(output); Console.WriteLine(Path.Combine(output, "preferences.json")); }
                catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
                finally { preferencesApp.Shutdown(Environment.ExitCode); }
            });
            preferencesApp.Run();
            return;
        }
        if (args is ["--workbench-lifecycle"] or ["--workbench-desktop"])
        {
            WorkbenchLifecycleProbe.Run(ScopeLifecycleProbe.CreateApplication(), output, args[0] == "--workbench-desktop");
            return;
        }
        if (args is ["--workspace-shutdown"])
        {
            var shutdownApp = ScopeLifecycleProbe.CreateApplication();
            shutdownApp.Dispatcher.BeginInvoke(async () =>
            {
                try { await WorkspaceShutdownProbe.Run(output); Console.WriteLine(Path.Combine(output, "workspace-shutdown.json")); }
                catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
                finally { shutdownApp.Shutdown(Environment.ExitCode); }
            });
            shutdownApp.Run();
            return;
        }
        if (args.Contains("--scope-lifecycle"))
        {
            if (args.Length != 1)
            {
                var error = new { mode = "scope-lifecycle", passed = false, failure = "Use --scope-lifecycle alone; desktop and other probe modes are not allowed." };
                File.WriteAllText(Path.Combine(output, "scope-lifecycle.json"), JsonSerializer.Serialize(error), System.Text.Encoding.UTF8);
                Console.WriteLine(JsonSerializer.Serialize(error)); Environment.ExitCode = 2; return;
            }
            try
            {
                var scopeApp = ScopeLifecycleProbe.CreateApplication();
                ScopeLifecycleProbe.Run(scopeApp, output);
            }
            catch (Exception failure)
            {
                var report = new { mode = "scope-lifecycle", passed = false, failure = failure.ToString() };
                File.WriteAllText(Path.Combine(output, "scope-lifecycle.json"), JsonSerializer.Serialize(report), System.Text.Encoding.UTF8);
                Console.WriteLine(JsonSerializer.Serialize(report)); Environment.ExitCode = 1;
            }
            return;
        }
        if (args.Contains("--latency"))
        {
            LatencyProbe.Run(ScopeLifecycleProbe.CreateApplication(), output);
            return;
        }
        var app = new CClean.App.App(); app.InitializeComponent();
        if (args.Contains("--desktop"))
        {
            void Report(Window window)
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                var entry = JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, dpiX = dpi.DpiScaleX, dpiY = dpi.DpiScaleY, width = window.ActualWidth, height = window.ActualHeight });
                File.AppendAllText(Path.Combine(output, "desktop-dpi.jsonl"), entry + "\n", System.Text.Encoding.UTF8);
            }
            var attached = false;
            app.Activated += (_, _) =>
            {
                if (attached || app.MainWindow is not Window window) return;
                attached = true; Report(window); window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(() => Report(window));
            };
            Console.WriteLine(output); app.Run(); return;
        }
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var changes = Enumerable.Range(0, 500).Select(i => new StoredChange(i, since.AddSeconds(i * 7), "synthetic", $"D:/synthetic/{i}/file.bin", i.ToString(), 0, 100, "新增")).ToArray();
        var reports = new List<object>();
        foreach (var scale in new[] { 1.25, 1.5, 2.0 })
        {
            var canvas = new TimelineCanvas();
            var durations = new List<double>();
            RenderTargetBitmap? bitmap = null;
            for (var iteration = 0; iteration < 101; iteration++)
            {
                var watch = Stopwatch.StartNew();
                var result = TimelineQuery.Build(changes, [], [], since, since.AddHours(1), since);
                if (result.Nodes.Count != 500) throw new InvalidOperationException("Expected 500 nodes.");
                canvas.Load(result, [], [], since, since.AddHours(1));
                canvas.Measure(new Size(960, double.PositiveInfinity));
                canvas.Arrange(new Rect(0, 0, 960, canvas.DesiredSize.Height));
                canvas.UpdateLayout();
                bitmap = new RenderTargetBitmap((int)(960 * scale), (int)(canvas.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(canvas);
                watch.Stop(); if (iteration > 0) durations.Add(watch.Elapsed.TotalMilliseconds);
            }
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap!));
            using (var stream = File.Create(Path.Combine(output, $"timeline-{scale}.png"))) encoder.Save(stream);
            durations.Sort();
            reports.Add(new { scale, nodes = 500, iterations = durations.Count, p95Milliseconds = durations[94], maxMilliseconds = durations[^1], heightDip = canvas.ActualHeight });
        }
        using var process = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { mode = "Offscreen WPF raster scaling; not system DPI or event-to-screen latency", os = Environment.OSVersion.ToString(), logicalProcessors = Environment.ProcessorCount, peakWorkingSet = process.PeakWorkingSet64, reports }, new JsonSerializerOptions { WriteIndented = true }), System.Text.Encoding.UTF8);
        Console.WriteLine(output);
    }
}
