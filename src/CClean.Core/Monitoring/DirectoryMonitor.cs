using System.Threading.Channels;

namespace CClean.Core.Monitoring;

public sealed class DirectoryMonitor : IDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly Channel<bool> hints;
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly string root;
    private readonly bool absolutePaths;
    private readonly object sync = new();
    private DirectorySnapshot snapshot = new(new Dictionary<string, Files.FileSnapshot>(), ["Initial scan pending"], DateTimeOffset.MinValue);
    private long applicationGaps, nativeGaps, scanGaps, watcherErrors, revision;
    private int disposed;
    private long lastActivityTicks;
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
            IncludeSubdirectories = true, InternalBufferSize = 4096,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
        };
        void Changed(object sender, FileSystemEventArgs args) { Interlocked.Exchange(ref lastActivityTicks, DateTimeOffset.UtcNow.Ticks); beforeNotification?.Invoke(); Hint(); }
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
        Hint();
    }

    private void Hint()
    {
        if (!hints.Writer.TryWrite(true)) Interlocked.Increment(ref applicationGaps);
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
                BeforeScan?.Invoke();
                var next = DirectoryScanner.Scan(root, stop.Token, previous: Snapshot.Files, absolutePaths: absolutePaths);
                if (next.Gaps.Count != 0) Interlocked.Add(ref scanGaps, next.Gaps.Count);
                lock (sync) snapshot = next;
                Interlocked.Increment(ref revision);
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
        }
    }

    public void Rescan() => Hint();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            watcher.Dispose();
            stop.Cancel();
            worker.GetAwaiter().GetResult();
        }
        finally { stop.Dispose(); }
    }
}
