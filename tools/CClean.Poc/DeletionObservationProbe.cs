using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Files;
using CClean.Core.Monitoring;
using CClean.Core.Sessions;
using Microsoft.Win32.SafeHandles;

namespace CClean.Poc;

// Fixture evidence only. No collected notification grants a production deletion exemption.
internal static class DeletionObservationProbe
{
    internal static void Run(ExperimentSandbox sandbox, List<object> checks)
    {
        if (sandbox.Budget < 4 * 1024 * 1024)
            throw new ExperimentRefusedException("Observation probe requires an explicit 4 MiB fixture/audit budget.", 2);
        var usnParser = UsnDiagnosticReader.ParserSelfCheck();
        var nativeParser = NativeDirectoryNotifications.ParserSelfCheck();
        checks.Add(new { name = "observation-parser-boundaries", status = usnParser.Count == 0 && nativeParser.All(c => c.Passed) ? "passed" : "failed", usnParser, nativeParser });
        if (usnParser.Count != 0 || nativeParser.Any(c => !c.Passed)) throw new InvalidOperationException("Diagnostic parser self-check failed.");

        var failures = new List<string>();
        void Scenario(string name, Action<Observation> action)
        {
            using var observation = new Observation(sandbox, name);
            string? error = null;
            try { action(observation); }
            catch (Exception failure) { error = failure.ToString(); failures.Add(name); }
            observation.Stop();
            if (observation.Armed && !observation.NativeExited)
            { error ??= "Extended notification worker did not exit within its bounded shutdown."; failures.Add(name); }
            checks.Add(new { name = "observation-" + name, status = error is null ? "passed" : "failed", error,
                observation.Root, observation.Parent, observation.Armed, observation.Identities,
                observation.Stages, observation.StopMilliseconds, observation.NativeExited, observation.NativeExitReason,
                nativeGaps = observation.Native.Gaps, fswGaps = observation.Raw.Gaps,
                observation.JournalIntervalsComplete,
                evidenceStatus = observation.Armed && observation.Native.TotalGaps == 0 && observation.Raw.Gaps.Count == 0 && observation.JournalIntervalsComplete
                    ? "diagnostic-data-collected-source-unproven" : "capability-or-continuity-gap",
                statusMeaning = "passed means controlled actions and stated characteristic assertions completed; it is not product acceptance or attribution proof",
                filters = new { productionAndRaw = "FileName|DirectoryName|Size|LastWrite; 4096-byte buffer", native = "0x17F; 65536-byte buffer; broader attribute/access/security coverage" },
                processAttributionProven = false, productionExemptionEnabled = false,
                limitation = "Controlled independent operations in this PoC process, not real user-process attribution. Stage settling is a bounded sampling delay, not proof of quiescence. Synthetic old mtimes establish only test eligibility." });
        }

        Scenario("executor-self-stop", o =>
        {
            var paths = o.Samples;
            var plan = CleanupPlanner.Preview(paths, o.Context());
            if (plan.EligibleCount != 3) throw new InvalidOperationException("Expected three eligible synthetic fixtures: " + string.Join("; ", plan.Items.Select(i => i.Reason)));
            using var journal = new CleanupJournal(o.Data);
            var completed = 0;
            var executor = new CleanupExecutor(journal)
            {
                TestCheckpoint = point =>
                {
                    if (point is "after-plan" or "before-delete") o.Capture(point, settle: false);
                    if (point == "after-delete") o.Capture("delete-marked-handle-still-open", settle: false);
                    if (point != "after-result") return;
                    completed++;
                    if (!SpinWait.SpinUntil(() => o.Monitor.Snapshot.Files.Count == 3 - completed, TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Production watcher did not observe fixture deletion.");
                    o.Capture("handle-closed-and-deleted-audit-persisted");
                }
            };
            var result = executor.ExecuteApproved(plan, () => Task.FromResult(o.Context())).GetAwaiter().GetResult();
            o.Capture("executor-returned", detail: new { result, audit = journal.Events(plan.Id.ToString()),
                remaining = paths.Select(p => new { path = p, exists = File.Exists(p) }).ToArray() });
            if (result.State != "NeedsPreview" || result.Deleted != 1 || paths.Skip(1).Any(p => !File.Exists(p)))
                throw new InvalidOperationException("Known production self-stop characteristic changed; review evidence before proceeding.");
        });
        Scenario("parent-time", o =>
        {
            var requested = DateTime.UtcNow.AddMinutes(-5);
            Directory.SetLastWriteTimeUtc(o.Parent, requested);
            o.Capture("direct-parent-last-write-no-delete", detail: new { requested, actual = Directory.GetLastWriteTimeUtc(o.Parent) });
        });
        Scenario("parent-attributes", o =>
        {
            var before = File.GetAttributes(o.Parent);
            try
            {
                File.SetAttributes(o.Parent, before ^ FileAttributes.Hidden);
                o.Capture("direct-parent-attributes-no-delete");
            }
            finally { File.SetAttributes(o.Parent, before); }
            o.Capture("parent-attributes-restored");
        });
        Scenario("transient", o =>
        {
            var path = o.NewSample("transient", 16);
            o.Remember(path);
            var deletion = VerifiedFileDeletion.DeleteFixture(sandbox, WindowsFileMetadata.Read(path));
            if (!deletion.DeleteMarked || File.Exists(path)) throw new InvalidOperationException("Transient fixture deletion failed.");
            o.Capture("entered-range-then-deleted-finally-absent", detail: new { path, finalExists = File.Exists(path), deletion,
                intermediateScanPrevented = false });
        });
        Scenario("parent-open-handle", o =>
        {
            using (var handle = WindowsFileMetadata.Open(o.Parent, access: 0x180, share: 7))
            {
                var first = DateTime.UtcNow.AddMinutes(-4);
                SetTime(handle, first);
                o.Capture("first-basic-change-handle-open", detail: new { requested = first, actual = Directory.GetLastWriteTimeUtc(o.Parent) });
                var second = DateTime.UtcNow.AddMinutes(-3);
                SetTime(handle, second);
                o.Capture("repeat-basic-change-same-handle-open", detail: new { requested = second, actual = Directory.GetLastWriteTimeUtc(o.Parent) });
            }
            o.Capture("parent-handle-closed");
        });
        Scenario("file-open-handle", o =>
        {
            var path = o.Samples[0];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.WriteByte(0x51); stream.Flush(true);
                o.Capture("first-same-length-write-handle-open", detail: new { writtenByte = 0x51, flushed = true, position = 0, length = stream.Length });
                stream.Position = 0; stream.WriteByte(0x52); stream.Flush(true);
                o.Capture("repeat-same-length-write-same-handle-open", detail: new { writtenByte = 0x52, flushed = true, position = 0, length = stream.Length });
            }
            o.Capture("file-handle-closed", detail: new { path, length = new FileInfo(path).Length,
                finalByte = File.ReadAllBytes(path)[0], onlyFixtureContentRead = true });
        });
        Scenario("nested-move", o =>
        {
            Directory.Move(o.Staging, Path.Combine(o.Parent, "moved-in"));
            o.Capture("nested-directory-moved-in");
            Directory.Move(Path.Combine(o.Parent, "moved-in"), o.Staging);
            o.Capture("nested-directory-moved-out");
        });
        Scenario("idle-cancel", o => o.Capture("idle-no-injected-operation"));
        if (failures.Count != 0) throw new InvalidOperationException("Observation scenarios failed: " + string.Join(", ", failures));
    }

    private static void SetTime(SafeFileHandle handle, DateTime value)
    {
        var time = value.ToFileTimeUtc();
        if (!SetFileTime(handle, IntPtr.Zero, IntPtr.Zero, ref time)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private sealed class Observation : IDisposable
    {
        private readonly ExperimentSandbox sandbox;
        private readonly string volume;
        private UsnDiagnosticReader.QueryResult previous;
        private int nativeOffset, rawOffset, sampleNumber;
        private bool stopped;
        internal string Root { get; }
        internal string Parent { get; }
        internal string Data { get; }
        internal string Staging { get; }
        internal string[] Samples { get; }
        internal DirectoryMonitor Monitor { get; }
        internal NativeDirectoryNotifications Native { get; }
        internal RawNotifications Raw { get; }
        internal bool Armed { get; }
        internal List<object> Identities { get; } = [];
        private readonly List<UsnDiagnosticReader.FileIdentity> known = [];
        internal List<object> Stages { get; } = [];
        internal bool JournalIntervalsComplete { get; private set; } = true;
        internal long StopMilliseconds { get; private set; }
        internal bool NativeExited => Native.Exited;
        internal string NativeExitReason => Native.ExitReason;

        internal Observation(ExperimentSandbox sandbox, string name)
        {
            this.sandbox = sandbox;
            Root = Path.Combine(sandbox.RunDirectory, name, "cache");
            Parent = Path.Combine(Root, "_cacache", "content-v2");
            Data = Path.Combine(sandbox.RunDirectory, name, "audit");
            Staging = Path.Combine(sandbox.RunDirectory, name, "staging");
            Directory.CreateDirectory(Parent);
            Directory.CreateDirectory(Path.Combine(Staging, "nested"));
            Samples = Enumerable.Range(0, 3).Select(i => NewSample("sample-" + i, 16)).ToArray();
            var moved = sandbox.CreateFixture(name + "-moved-sample", 16);
            File.Move(moved, Path.Combine(Staging, "nested", "move-sample"));
            // All initial setup handles close before recording journal cursors or arming watchers.
            foreach (var path in new[] { Root, Path.GetDirectoryName(Parent)!, Parent, Staging, Path.Combine(Staging, "nested"), Path.Combine(Staging, "nested", "move-sample") }.Concat(Samples)) Remember(path);
            DirectoryScanner.Scan(Root);
            volume = Path.GetPathRoot(Root)!;
            Native = new(Root);
            Raw = new(Root);
            Monitor = new(Root, absolutePaths: true);
            Armed = Native.WaitUntilArmed(TimeSpan.FromSeconds(3));
            if (!SpinWait.SpinUntil(() => Monitor.Revision > 0, TimeSpan.FromSeconds(10)))
            { Stop(); throw new TimeoutException("Initial production watcher scan did not finish."); }
            previous = UsnDiagnosticReader.Query(volume);
            Capture("baseline", settle: false);
        }

        internal string NewSample(string name, int bytes)
        {
            var source = sandbox.CreateFixture(Path.GetFileName(Path.GetDirectoryName(Root)) + "-" + sampleNumber++ + "-" + name, bytes);
            var path = Path.Combine(Parent, name);
            File.Move(source, path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
            return path;
        }
        internal void Remember(string path)
        {
            var identity = UsnDiagnosticReader.ReadFileIdentity(path);
            Identities.Add(new { observedUtc = DateTimeOffset.UtcNow, identity });
            if (identity.Identity is { } item) known.Add(item);
        }
        internal CleanupContext Context()
        {
            var active = Monitor.LastActivity > DateTimeOffset.UtcNow.AddMinutes(-30);
            return new(1, [new(Root, CacheRule.NpmContent.Id, 1, "11.19.0", true, true)],
                new ActivitySnapshot(1, [new(Root, active, Monitor.HasContinuousObservation,
                    active ? "近期目录变化，保持活动保护" : "隔离合成对象，仅用于协议诊断")]), [Data]);
        }
        internal void Capture(string name, bool settle = true, object? detail = null)
        {
            if (settle) Thread.Sleep(250);
            var capturedUtc = DateTimeOffset.UtcNow;
            var next = UsnDiagnosticReader.Query(volume);
            UsnDiagnosticReader.ReadResult? read = null;
            if (previous.State is { } start && next.State is { } end && start.JournalId == end.JournalId)
                read = UsnDiagnosticReader.Read(volume, end, start.NextUsn, end.NextUsn, known,
                    new(MaxRecords: 16384));
            JournalIntervalsComplete &= read?.IntervalComplete == true;
            var native = Native.Snapshot();
            var raw = Raw.Snapshot();
            Stages.Add(new { name, capturedUtc, monotonicTicks = Stopwatch.GetTimestamp(), monotonicFrequency = Stopwatch.Frequency,
                settleMilliseconds = settle ? 250 : 0, detail,
                journalStart = previous, journalStop = next, journalRead = read,
                intervalUnavailable = read is null, native = native.Skip(nativeOffset).ToArray(),
                fsw = raw.Skip(rawOffset).ToArray(), nativeGaps = Native.Gaps, fswGaps = Raw.Gaps,
                production = new { Monitor.Revision, Monitor.LastActivity, Monitor.HasContinuousObservation,
                    Monitor.ApplicationGaps, Monitor.NativeGaps, Monitor.ScanGaps, Monitor.WatcherErrors,
                    fileCount = Monitor.Snapshot.Files.Count, snapshotGaps = Monitor.Snapshot.Gaps } });
            nativeOffset = native.Count; rawOffset = raw.Count; previous = next;
        }
        internal void Stop()
        {
            if (stopped) return;
            stopped = true;
            var timer = Stopwatch.StartNew();
            Native.Dispose(); Raw.Dispose(); Monitor.Dispose();
            StopMilliseconds = timer.ElapsedMilliseconds;
        }
        public void Dispose() => Stop();
    }

    private sealed record RawEvent(long Seq, DateTimeOffset ReceiptUtc, string Kind, string? Name, string? OldName);
    private sealed class RawNotifications : IDisposable
    {
        private readonly FileSystemWatcher watcher;
        private readonly object sync = new();
        private readonly List<RawEvent> events = [];
        private readonly List<string> gaps = [];
        private long sequence;
        internal RawNotifications(string root)
        {
            watcher = new(root) { IncludeSubdirectories = true, InternalBufferSize = 4096,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
            void Add(FileSystemEventArgs e, string? old = null)
            {
                lock (sync)
                {
                    if (events.Count < 4096) events.Add(new(++sequence, DateTimeOffset.UtcNow, e.ChangeType.ToString(), e.Name, old));
                    else if (gaps.Count < 32) gaps.Add("bounded application event collection exhausted");
                }
            }
            watcher.Created += (_, e) => Add(e); watcher.Changed += (_, e) => Add(e); watcher.Deleted += (_, e) => Add(e);
            watcher.Renamed += (_, e) => Add(e, e.OldName);
            watcher.Error += (_, e) => { lock (sync) if (gaps.Count < 32) gaps.Add(e.GetException().ToString()); };
            watcher.EnableRaisingEvents = true;
        }
        internal IReadOnlyList<RawEvent> Snapshot() { lock (sync) return events.ToArray(); }
        internal IReadOnlyList<string> Gaps { get { lock (sync) return gaps.ToArray(); } }
        public void Dispose() => watcher.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileTime(SafeFileHandle handle, IntPtr creation, IntPtr access, ref long write);
}
