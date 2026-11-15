using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CClean.App;
using CClean.App.ViewModels;
using CClean.Core.Experiments;

internal static class PreferencesProbe
{
    private sealed record ProbeCheck(string name, bool passed, object? detail);
    private static readonly string[] RequiredChecks =
    [
        "missing-parent-default-only", "missing-file-default-only", "normal-save-load-including-utf8-bom",
        "exact-16kib-valid", "malformed-json-preserved", "null-json-preserved", "oversized-json-preserved",
        "exclusive-read-error-preserved", "access-error-is-not-default", "main-window-visible-error-before-workspace",
        "temporary-theme-cannot-overwrite-bad-preferences", "navigation-keeps-configuration-error",
        "configuration-error-safe-exit", "final-bad-file-bytes-intact-no-workspace"
    ];

    internal static async Task<object> Run(string output)
    {
        Application.Current.Dispatcher.VerifyAccess();
        using var box = new ExperimentSandbox(Path.Combine(output, "preferences-sandbox"));
        var previousData = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR");
        var started = DateTimeOffset.UtcNow;
        var checks = new List<ProbeCheck>();
        var errors = new List<string>();
        MainWindow? window = null;
        var reportPath = Path.Combine(output, "preferences.json");
        object Report(string? failure) => new
        {
            mode = "isolated bounded preferences and WPF initialization",
            componentOnly = true, installedUiAcceptance = false,
            startedUtc = started, finishedUtc = DateTimeOffset.UtcNow, fixtureRoot = box.RunDirectory,
            sessionDiscovery = "disabled", checks, requiredChecks = RequiredChecks, errors,
            passed = failure is null && errors.Count == 0 && checks.Select(c => c.name).SequenceEqual(RequiredChecks) && checks.All(c => c.passed),
            safeExitCompleted = window?.ExitReady == true, workspaceConstructed = window?.Workspace is not null, failure,
            limitation = "Programmatic WPF control checks only; no UI input, installed-product acceptance or existing user directory access."
        };
        void Save(object report) => File.WriteAllText(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        void Check(string name, bool passed, object? detail = null)
        {
            checks.Add(new(name, passed, detail));
            if (!passed) throw new InvalidOperationException(name);
        }
        string Data(string name)
        {
            var directory = Path.Combine(box.RunDirectory, name);
            Directory.CreateDirectory(directory);
            return directory;
        }
        string Write(string data, string text)
        {
            var path = Path.Combine(data, "preferences.json");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        void Reject<T>(string name, string text) where T : Exception
        {
            var data = Data(name);
            var path = Write(data, text);
            var before = Hash(path);
            Exception? failure = null;
            try { AppPreferences.Load(data); }
            catch (Exception error) { failure = error; }
            Check(name, failure is T && Hash(path) == before,
                new { error = failure?.GetType().Name, bytes = new FileInfo(path).Length, originalPreserved = Hash(path) == before });
        }
        try
        {
            var missingParent = Path.Combine(box.RunDirectory, "absent-parent", "data");
            var defaults = AppPreferences.Load(missingParent);
            Check("missing-parent-default-only", !defaults.Dark && defaults.DiscoveryRoot == "" && !Directory.Exists(missingParent));
            var missingFile = Data("missing-file");
            defaults = AppPreferences.Load(missingFile);
            Check("missing-file-default-only", !defaults.Dark && defaults.DiscoveryRoot == "" && !Directory.EnumerateFileSystemEntries(missingFile).Any());

            var normal = Data("normal-save-protocol");
            var expectedRoot = Path.Combine(box.RunDirectory, "中文项目");
            new AppPreferences { Dark = true, DiscoveryRoot = expectedRoot }.Save(normal);
            var loaded = AppPreferences.Load(normal);
            Check("normal-save-load-including-utf8-bom", loaded.Dark && loaded.DiscoveryRoot == expectedRoot,
                new { bytes = new FileInfo(Path.Combine(normal, "preferences.json")).Length });
            var bounded = Data("exact-limit");
            Write(bounded, "{}" + new string(' ', 16_382));
            Check("exact-16kib-valid", !AppPreferences.Load(bounded).Dark);

            Reject<JsonException>("malformed-json-preserved", "{not-json");
            Reject<JsonException>("null-json-preserved", "null");
            Reject<IOException>("oversized-json-preserved", "{}" + new string(' ', 16_383));
            var locked = Data("exclusive-reader");
            var lockedPath = Write(locked, "{\"Dark\":true}");
            var lockedBefore = Hash(lockedPath);
            Exception? lockedError = null;
            using (var lease = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { AppPreferences.Load(locked); }
                catch (Exception error) { lockedError = error; }
            }
            Check("exclusive-read-error-preserved", lockedError is IOException && Hash(lockedPath) == lockedBefore,
                new { error = lockedError?.GetType().Name, originalPreserved = Hash(lockedPath) == lockedBefore });
            var directoryFile = Data("directory-instead-of-file");
            Directory.CreateDirectory(Path.Combine(directoryFile, "preferences.json"));
            Exception? accessError = null;
            try { AppPreferences.Load(directoryFile); }
            catch (Exception error) { accessError = error; }
            Check("access-error-is-not-default", accessError is UnauthorizedAccessException or IOException,
                new { error = accessError?.GetType().Name });

            var windowData = Data("main-window-error");
            var badPath = Write(windowData, "{broken-preferences");
            var badBytes = File.ReadAllBytes(badPath);
            var badHash = Hash(badPath);
            Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", windowData);
            window = new MainWindow(enableSessionDiscovery: false);
            Application.Current.MainWindow = window;
            // Invoke the real handler without showing a desktop window or invoking the tray Loaded handler.
            Invoke(window, "OnLoaded");
            var title = (TextBlock)window.FindName("PageTitle");
            var host = (ContentControl)window.FindName("PageContent");
            var status = (TextBlock)window.FindName("Status");
            await Until(() => title.Text == "本地配置不可用" && host.Content is TextBlock);
            Check("main-window-visible-error-before-workspace", window.Workspace is null &&
                host.Content is TextBlock errorText && errorText.Text.Contains("无法载入本地配置", StringComparison.Ordinal) &&
                errorText.Text.Contains("修复后重新启动", StringComparison.Ordinal) && status.Text.Contains("原配置文件未覆盖", StringComparison.Ordinal));
            Invoke(window, "ToggleTheme");
            Check("temporary-theme-cannot-overwrite-bad-preferences", window.Workspace is null && Hash(badPath) == badHash &&
                Directory.EnumerateFileSystemEntries(windowData).SequenceEqual([badPath]) &&
                status.Text.Contains("临时切换", StringComparison.Ordinal));
            var navigation = (ListBox)window.FindName("Navigation");
            foreach (var index in new[] { 1, 2, 0 }) navigation.SelectedIndex = index;
            Invoke(window, "OpenSettings");
            Check("navigation-keeps-configuration-error", title.Text == "本地配置不可用" && host.Content is TextBlock remaining &&
                remaining.Text.Contains("无法载入本地配置", StringComparison.Ordinal) && window.Workspace is null && Hash(badPath) == badHash);
            await window.RequestExit().WaitAsync(TimeSpan.FromSeconds(10));
            Check("configuration-error-safe-exit", window.ExitReady && Hash(badPath) == badHash);
            Check("final-bad-file-bytes-intact-no-workspace", window.Workspace is null && File.ReadAllBytes(badPath).SequenceEqual(badBytes) &&
                Directory.EnumerateFileSystemEntries(windowData).SequenceEqual([badPath]),
                new { originalBytes = badBytes.Length, currentBytes = new FileInfo(badPath).Length, sha256 = Hash(badPath), workspaceConstructed = window.Workspace is not null });
            var report = Report(null);
            Save(report);
            return report;
        }
        catch (Exception error)
        {
            errors.Add(error.ToString());
            Save(Report(error.ToString()));
            throw;
        }
        finally
        {
            try { if (window is not null && !window.ExitReady) await window.RequestExit().WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { Environment.SetEnvironmentVariable("CCLEAN_DATA_DIR", previousData); }
        }
    }

    private static void Invoke(MainWindow window, string method) =>
        (typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, method)).Invoke(window, [window, new RoutedEventArgs()]);

    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The real MainWindow initialization handler did not show its configuration error.");
            await Task.Delay(20);
        }
    }
}
