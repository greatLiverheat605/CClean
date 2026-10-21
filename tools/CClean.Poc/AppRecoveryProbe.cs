using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using CClean.Core.Experiments;
using CClean.Core.Projects;
using CClean.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CClean.Poc;

internal static class AppRecoveryProbe
{
    private sealed record Change(long Id, string Path, long? Before, long? After, string Kind);
    private sealed record Gap(long Id, DateTimeOffset Time, string Reason);
    private sealed record State(bool Initialized, bool MetadataComplete, Dictionary<string, long> Files, List<Change> Changes, List<Gap> Gaps);

    public static void Run(ExperimentSandbox box, List<object> checks, string appPath)
    {
        var app = ExperimentSandbox.Normalize(appPath);
        var artifacts = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        if (!app.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(app).StartsWith("CClean", StringComparison.OrdinalIgnoreCase) || !app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ExperimentRefusedException("Only a workspace artifacts CClean executable may be started.", 3);
        var appDirectory = Path.GetDirectoryName(app)!;
        using var appLease = new DirectoryLease(appDirectory);
        var binaries = new[] { app, Path.Combine(appDirectory, "CClean.App.dll"), Path.Combine(appDirectory, "CClean.Core.dll") };
        if (binaries.Any(p => !File.Exists(p) || (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) ||
            !File.Exists(Path.Combine(appDirectory, "CClean.App.runtimeconfig.json")) ||
            AssemblyName.GetAssemblyName(binaries[1]).Name != "CClean.App" || AssemblyName.GetAssemblyName(binaries[2]).Name != "CClean.Core" ||
            FileVersionInfo.GetVersionInfo(app).ProductName != "CClean.App")
            throw new ExperimentRefusedException("A published CClean App/Core pair and ordinary apphost are required.", 3);
        if (box.Budget < 1024 * 1024) throw new ExperimentRefusedException("App recovery needs at least 1 MiB fixture/runtime budget.", 2);
        using var hostLock = new FileStream(binaries[0], FileMode.Open, FileAccess.Read, FileShare.Read);
        using var appLock = new FileStream(binaries[1], FileMode.Open, FileAccess.Read, FileShare.Read);
        using var coreLock = new FileStream(binaries[2], FileMode.Open, FileAccess.Read, FileShare.Read);
        string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
        var hashes = binaries.ToDictionary(p => Path.GetFileName(p)!, Hash);
        var version = FileVersionInfo.GetVersionInfo(binaries[1]).ProductVersion;
        checks.Add(new { name = "app-recovery-binary", status = "passed", app, version, hashes });
        var projectRoot = Path.Combine(box.RunDirectory, "project"); var data = Path.Combine(box.RunDirectory, "data");
        Directory.CreateDirectory(projectRoot); using var fixtureLease = new DirectoryLease(projectRoot);
        string Fixture(string name) => Path.Combine(projectRoot, name);
        var expected = new Dictionary<string, long> { ["keep.bin"] = 4096, ["edit.bin"] = 1024, ["gone.bin"] = 2048, ["old-name.bin"] = 512 };
        foreach (var file in expected) File.Move(box.CreateFixture(file.Key, file.Value), Fixture(file.Key));
        var keepHash = Hash(Fixture("keep.bin")); var renamedHash = Hash(Fixture("old-name.bin"));
        var project = new ProjectRegistry(data).Register("App recovery fixture", projectRoot, "通用工具");
        Process Start()
        {
            if (binaries.Any(p => Hash(p) != hashes[Path.GetFileName(p)])) throw new IOException("Published binaries changed during probe.");
            var start = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = appDirectory,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.Environment["CCLEAN_DATA_DIR"] = data;
            return Process.Start(start) ?? throw new IOException("Owned App child did not start.");
        }
        State Wait(Process child, Func<State, bool> ready, string phase)
        {
            var timer = Stopwatch.StartNew(); string last = "database not ready";
            while (timer.Elapsed < TimeSpan.FromSeconds(24))
            {
                if (child.HasExited) throw new IOException($"Owned App exited during {phase}: {child.ExitCode}.");
                if (RuntimeBudget.Measure(data).TotalBytes + Directory.EnumerateFiles(projectRoot).Sum(p => new FileInfo(p).Length) > box.Budget ||
                    new DriveInfo(Path.GetPathRoot(data)!).AvailableFreeSpace < box.Reserve) throw new IOException("Recovery probe storage budget reached.");
                try { var state = Read(data, project.Id, projectRoot); if (ready(state)) return state; last = $"files={state.Files.Count}, changes={state.Changes.Count}, gaps={state.Gaps.Count}"; }
                catch (SqliteException error) { last = error.Message; }
                Thread.Sleep(100);
            }
            throw new TimeoutException($"App recovery {phase} did not converge within the bounded wait: {last}");
        }
        bool Matches(State state) => state.Initialized && state.MetadataComplete && state.Files.Count == expected.Count && expected.All(p => state.Files.GetValueOrDefault(p.Key, -1) == p.Value);
        State baseline; int firstPid, firstExit; bool firstForced;
        using (var first = Start())
        {
            firstPid = first.Id;
            try { baseline = Wait(first, s => Matches(s) && s.Changes.Count == 4 && s.Changes.All(c => c.Kind == "初始存量") && s.Gaps.Count > 0, "baseline"); }
            finally { firstForced = StopOwned(first); }
            firstExit = first.ExitCode;
        }
        if (!firstForced) throw new IOException("First owned App exited before forced termination; crash recovery was not exercised.");
        // Only these named files were created above, beneath a held sandbox directory lease.
        File.WriteAllBytes(Fixture("edit.bin"), new byte[1536]); File.Delete(Fixture("gone.bin"));
        File.Move(Fixture("old-name.bin"), Fixture("renamed.bin"));
        File.Move(box.CreateFixture("added.bin", 768), Fixture("added.bin"));
        expected = new() { ["keep.bin"] = 4096, ["edit.bin"] = 1536, ["renamed.bin"] = 512, ["added.bin"] = 768 };
        var changes = new[] { new Change(0, "edit.bin", 1024, 1536, "变化"), new(0, "gone.bin", 2048, null, "消失"),
            new(0, "old-name.bin", 512, null, "消失"), new(0, "renamed.bin", null, 512, "新增"), new(0, "added.bin", null, 768, "新增") };
        var restart = DateTimeOffset.UtcNow; State recovered; int secondPid, secondExit; bool secondForced;
        using (var second = Start())
        {
            secondPid = second.Id;
            try { recovered = Wait(second, s => Matches(s) && s.Changes.Count == 9 && s.Gaps.Any(g => g.Id > baseline.Gaps.Max(x => x.Id) && g.Time >= restart && g.Reason.Contains("开始或恢复观察")), "restart"); }
            finally { secondForced = StopOwned(second); }
            secondExit = second.ExitCode;
        }
        if (!secondForced) throw new IOException("Second owned App exited before forced termination; crash acceptance is incomplete.");
        var observed = recovered.Changes.Where(c => c.Id > baseline.Changes.Max(x => x.Id)).ToArray();
        if (!recovered.Changes.Take(4).SequenceEqual(baseline.Changes) || changes.Any(e => !observed.Any(c => c.Path == e.Path && c.Before == e.Before && c.After == e.After && c.Kind == e.Kind)) ||
            Hash(Fixture("keep.bin")) != keepHash || Hash(Fixture("renamed.bin")) != renamedHash)
            throw new InvalidOperationException("App restart lost baseline, offline differences, or unchanged fixture content.");
        checks.Add(new { name = "app-process-recovery", status = "passed", version, hashes, projectRoot, data,
            firstPid, firstExit, firstForced, secondPid, secondExit, secondForced,
            abnormalExit = "Forced termination requested for both owned App instances via held Process handles; crash simulation only",
            baseline = baseline.Files, recovered = recovered.Files, offlineDifferences = observed, startupGaps = recovered.Gaps,
            unchangedHashPreserved = true, renamedHashPreserved = true, uiOperated = false });
    }

    private static bool StopOwned(Process child)
    {
        // Never enumerate or look up another PID, and never terminate a process tree.
        var forced = !child.HasExited;
        if (forced) child.Kill();
        if (!child.WaitForExit(5000)) throw new IOException("Owned App child did not terminate.");
        return forced;
    }
    private static State Read(string data, Guid project, string projectRoot)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "index.db"),
            Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        SqliteCommand Command(string sql)
        {
            var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 1;
            command.CommandText = sql; command.Parameters.AddWithValue("$p", project.ToString()); return command;
        }
        using var initialized = Command("SELECT COUNT(*) FROM roots WHERE id=$p AND initialized=1");
        var ready = Convert.ToInt32(initialized.ExecuteScalar()) == 1;
        var files = new Dictionary<string, long>(); var changes = new List<Change>(); var gaps = new List<Gap>(); var complete = true;
        using (var command = Command("SELECT path,logical,identity,json_extract(snapshot,'$.Exclusion') FROM files WHERE project=$p LIMIT 16"))
        using (var reader = command.ExecuteReader()) while (reader.Read())
        { complete &= !reader.IsDBNull(1) && !reader.IsDBNull(2) && reader.IsDBNull(3); files.Add(Path.GetRelativePath(projectRoot, reader.GetString(0)), reader.IsDBNull(1) ? -1 : reader.GetInt64(1)); }
        using (var command = Command("SELECT id,path,before_size,after_size,kind FROM changes WHERE project=$p ORDER BY id LIMIT 32"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) changes.Add(new(reader.GetInt64(0), Path.GetRelativePath(projectRoot, reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetString(4)));
        using (var command = Command("SELECT id,time,reason FROM gaps WHERE project=$p ORDER BY id LIMIT 16"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) gaps.Add(new(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2)));
        return new(ready, complete, files, changes, gaps);
    }
}
