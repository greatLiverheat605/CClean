using System.Runtime.ExceptionServices;
using System.Diagnostics;
using System.Threading.Channels;
using System.Collections.Immutable;

namespace CClean.Core.Monitoring;

public sealed record DirectoryNotification(long Sequence, DateTimeOffset Time, WatcherChangeTypes Kind, string Path, string? OldPath);
public sealed record DirectoryObservation(Guid Id, string Root, long Sequence, long DroppedThrough, long ScanGaps,
    bool Reliable, IReadOnlyList<DirectoryNotification> Events);

public sealed class DirectoryMonitor : IDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly Channel<bool> hints;
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly string root;
    private readonly bool absolutePaths;
    private readonly object sync = new();
    // Coordinates only our own snapshot reader and approved deletion; it is not a filesystem lock.
    internal object ScanGate { get; } = new();
    private bool researchScanDeferred;
    private TaskCompletionSource? researchScanCompletion;
    internal Task ResearchScanCompleted => Volatile.Read(ref researchScanCompletion)?.Task ?? Task.CompletedTask;
    // Caller enters ScanGate before changing the scan schedule; only isolated research uses this.
    internal void BeginResearchScanDeferral()
    {
        if (!Monitor.IsEntered(ScanGate) || researchScanDeferred ||
            researchScanCompletion is { Task.IsCompleted: false } || Volatile.Read(ref disposed) != 0)
            throw new InvalidOperationException("Research scan lease unavailable.");
        researchScanDeferred = true;
        researchScanCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal void EndResearchScanDeferral()
    {
        lock (ScanGate)
        {
            if (!researchScanDeferred) throw new InvalidOperationException("Research scan lease was not held.");
            researchScanDeferred = false;
            if (Volatile.Read(ref disposed) == 0) RequestFullScan();
        }
    }
    private DirectorySnapshot snapshot = new(new Dictionary<string, Files.FileSnapshot>(), ["Initial scan pending"], DateTimeOffset.MinValue);
    private long applicationGaps, nativeGaps, scanGaps, watcherErrors, revision;
    private long scanTicks;
    // Set by calibration, watcher failures and lost hints; cleared only when a complete scan starts.
    private int fullScanRequested = 1;
    // Highest notification sequence already reflected in the published snapshot.
    private long appliedThrough;
    private long fullScans, incrementalRefreshes;
    internal long FullScans => Interlocked.Read(ref fullScans);
    internal long IncrementalRefreshes => Interlocked.Read(ref incrementalRefreshes);
    internal TimeSpan ScanTime => TimeSpan.FromSeconds((double)Interlocked.Read(ref scanTicks) / Stopwatch.Frequency);
    private int disposed;
    private long lastActivityTicks;
    private readonly Guid observationId = Guid.NewGuid();
    private ImmutableList<DirectoryNotification> notifications = ImmutableList<DirectoryNotification>.Empty;
    private readonly object notificationSync = new();
    private long notificationSequence, droppedThrough;
    private int notificationCharacters;
    // Bounded evidence, independent of the coalesced scan hints. Lost history never grants permission.
    public DirectoryObservation Observe()
    {
        lock (notificationSync)
            return new(observationId, root, notificationSequence, droppedThrough, ScanGaps, HasContinuousObservation, notifications);
    }
    public bool IsObservationCurrent(DirectoryObservation observation)
    {
        lock (notificationSync)
            return observation.Id == observationId && observation.Sequence == notificationSequence &&
                observation.ScanGaps == ScanGaps && HasContinuousObservation;
    }
    public DateTimeOffset? LastActivity => Interlocked.Read(ref lastActivityTicks) is var ticks && ticks != 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    public long ApplicationGaps => Interlocked.Read(ref applicationGaps);
    public long NativeGaps => Interlocked.Read(ref nativeGaps);
    public long ScanGaps => Interlocked.Read(ref scanGaps);
    public long WatcherErrors => Interlocked.Read(ref watcherErrors);
    public long Revision => Interlocked.Read(ref revision);
    public DirectorySnapshot Snapshot { get { lock (sync) return snapshot; } }
    // A current snapshot does not repair failed native observation or a stopped collector.
    public bool HasContinuousObservation => Volatile.Read(ref disposed) == 0 && !worker.IsCompleted &&
        Revision > 0 && Snapshot.Gaps.Count == 0 && NativeGaps == 0 && ApplicationGaps == 0 && WatcherErrors == 0;
    // Controlled collector-failure test hook, not evidence of a real filesystem failure.
    internal Action? BeforeScan { get; set; }

    // beforeNotification is a controlled probe barrier; it never synthesizes overflow errors.
    public DirectoryMonitor(string root, int queueCapacity = 128, Action? beforeNotification = null, bool absolutePaths = false,
        IReadOnlyDictionary<string, Files.FileSnapshot>? initialFiles = null)
    {
        this.root = Path.GetFullPath(root);
        this.absolutePaths = absolutePaths;
        // A persisted seed only avoids duplicate metadata objects; it is not current coverage.
        if (initialFiles is not null)
            snapshot = new(initialFiles, ["Stored baseline awaiting live scan"], DateTimeOffset.MinValue) { PathsAreAbsolute = absolutePaths };
        using var boundary = new Experiments.DirectoryLease(this.root);
        if (queueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        hints = Channel.CreateBounded<bool>(new BoundedChannelOptions(queueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        watcher = new FileSystemWatcher(this.root)
        {
            // 64 KiB is the documented maximum useful size; a larger buffer reduces native overflows (full rescans).
            IncludeSubdirectories = true, InternalBufferSize = 65536,
            // CreationTime is part of the stored identity and Security changes ChangeTime; incremental refresh needs both.
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite |
                NotifyFilters.Attributes | NotifyFilters.CreationTime | NotifyFilters.Security
        };
        void Changed(object sender, FileSystemEventArgs args)
        {
            lock (notificationSync)
            {
                var time = DateTimeOffset.UtcNow;
                Interlocked.Exchange(ref lastActivityTicks, time.Ticks);
                var entry = new DirectoryNotification(++notificationSequence, time, args.ChangeType, args.FullPath,
                    (args as RenamedEventArgs)?.OldFullPath);
                // Observations share immutable history; later notifications never rewrite an earlier baseline.
                notifications = notifications.Add(entry);
                notificationCharacters += entry.Path.Length + (entry.OldPath?.Length ?? 0);
                while (notifications.Count > 2048 || notificationCharacters > 262144)
                {
                    var removed = notifications[0];
                    notifications = notifications.RemoveAt(0); droppedThrough = removed.Sequence;
                    notificationCharacters -= removed.Path.Length + (removed.OldPath?.Length ?? 0);
                }
            }
            beforeNotification?.Invoke(); Hint();
        }
        watcher.Created += Changed;
        watcher.Changed += Changed;
        watcher.Deleted += Changed;
        watcher.Renamed += (sender, args) => Changed(sender, args);
        watcher.Error += (_, args) => ReportWatcherError(args.GetException());
        // Events arriving during the initial scan schedule another scan.
        watcher.EnableRaisingEvents = true;
        worker = Task.Run(Collect);
        Hint();
    }

    // Shared with controlled error-reporting tests; injection is not evidence of real kernel overflow.
    internal void ReportWatcherError(Exception error)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (error is InternalBufferOverflowException) Interlocked.Increment(ref nativeGaps);
        else
        {
            Interlocked.Increment(ref watcherErrors);
            // Keep the failure in the existing persisted cumulative gap count as well.
            Interlocked.Increment(ref scanGaps);
        }
        RequestFullScan();
    }

    private void RequestFullScan()
    {
        Interlocked.Exchange(ref fullScanRequested, 1);
        Hint();
    }

    private void Hint()
    {
        if (hints.Writer.TryWrite(true)) return;
        Interlocked.Increment(ref applicationGaps);
        // A lost hint never loses the notification itself, but stay conservative and reconcile fully.
        Interlocked.Exchange(ref fullScanRequested, 1);
    }

    private async Task Collect()
    {
        try
        {
            while (await hints.Reader.WaitToReadAsync(stop.Token))
            {
                while (hints.Reader.TryRead(out _)) { }
                await Task.Delay(100, stop.Token);
                stop.Token.ThrowIfCancellationRequested();
                while (hints.Reader.TryRead(out _)) { }
                lock (ScanGate)
                {
                    if (researchScanDeferred) continue;
                    long sequenceAtStart, dropped;
                    ImmutableList<DirectoryNotification> history;
                    lock (notificationSync) { sequenceAtStart = notificationSequence; dropped = droppedThrough; history = notifications; }
                    BeforeScan?.Invoke();
                    var started = Stopwatch.GetTimestamp();
                    DirectorySnapshot? next = null;
                    try
                    {
                        // Incremental refresh only when every notification since the last snapshot is still held.
                        if (Interlocked.Exchange(ref fullScanRequested, 0) == 0 && dropped <= appliedThrough &&
                            researchScanCompletion is not { Task.IsCompleted: false })
                        {
                            var pending = history.Where(e => e.Sequence > appliedThrough && e.Sequence <= sequenceAtStart).ToArray();
                            next = IncrementalRefresh.TryApply(root, Snapshot, pending, absolutePaths, stop.Token);
                            if (next is not null) Interlocked.Increment(ref incrementalRefreshes);
                        }
                        if (next is null)
                        {
                            next = DirectoryScanner.Scan(root, stop.Token, previous: Snapshot.Files, absolutePaths: absolutePaths);
                            Interlocked.Increment(ref fullScans);
                        }
                    }
                    finally
                    {
                        Interlocked.Add(ref scanTicks, Stopwatch.GetTimestamp() - started);
                    }
                    appliedThrough = sequenceAtStart;
                    if (next.Gaps.Count != 0) Interlocked.Add(ref scanGaps, next.Gaps.Count);
                    lock (sync) snapshot = next;
                    Interlocked.Increment(ref revision);
                    lock (notificationSync)
                        if (notificationSequence == sequenceAtStart && !hints.Reader.TryPeek(out _))
                            researchScanCompletion?.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Interlocked.Increment(ref scanGaps);
            lock (sync)
                snapshot = snapshot with { Gaps = new[] { "采集已停止：" + error.Message }.Concat(snapshot.Gaps).Take(1000).ToArray() };
            // Publish the failure to the existing persistence/status loop and leave collection stopped.
            Interlocked.Increment(ref revision);
            researchScanCompletion?.TrySetResult();
        }
    }

    // Calibration: always a complete scan, never an incremental refresh.
    public void Rescan() => RequestFullScan();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var failures = new List<Exception>();
        void Finish(Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(error); }
        }
        // Every stage runs even when an earlier one fails; the worker must finish before its CTS is released.
        Finish(watcher.Dispose);
        Finish(stop.Cancel);
        Finish(() => worker.GetAwaiter().GetResult());
        researchScanCompletion?.TrySetCanceled();
        Finish(stop.Dispose);
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("停止目录观察时发生多个错误。", failures);
    }
}
