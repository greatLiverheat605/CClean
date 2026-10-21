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
        var app = new CClean.App.App(); app.InitializeComponent();
        var output = Path.GetFullPath(Path.Combine("artifacts", "ui-probe-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")));
        Directory.CreateDirectory(output);
        if (args.Contains("--latency")) { LatencyProbe.Run(app, output); return; }
        if (args.Contains("--desktop"))
        {
            app.StartupUri = new Uri("/CClean.App;component/MainWindow.xaml", UriKind.Relative);
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
