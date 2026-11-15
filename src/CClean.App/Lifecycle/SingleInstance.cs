using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Threading;

namespace CClean.App.Lifecycle;

// Acquire and dispose on the UI thread, before constructing a Workspace or opening its database.
internal sealed class SingleInstance : IDisposable
{
    internal enum StartStatus { Primary, RestoreSignaled, Failed }
    internal sealed record StartResult(StartStatus Status, SingleInstance? Instance, string? Error,
        bool AbandonedMutexRecovered = false);
    internal sealed record ObjectNames(string Mutex, string RestoreEvent, string DataDirectory);

    private readonly Mutex mutex;
    private readonly EventWaitHandle restoreEvent;
    private readonly Dispatcher dispatcher;
    private readonly Action restore;
    private readonly Action<Exception> onError;
    private readonly RegisteredWaitHandle registration;
    private readonly object gate = new();
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private DispatcherOperation? pendingRestore;
    private bool disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle restoreEvent, Dispatcher dispatcher,
        Action restore, Action<Exception> onError)
    {
        this.mutex = mutex;
        this.restoreEvent = restoreEvent;
        this.dispatcher = dispatcher;
        this.restore = restore;
        this.onError = onError;
        registration = ThreadPool.RegisterWaitForSingleObject(restoreEvent, (_, _) => RestoreRequested(),
            null, Timeout.Infinite, executeOnlyOnce: false);
    }

    internal static StartResult TryStart(string dataDirectory, Dispatcher dispatcher, Action restore,
        Action<Exception> onError, TimeSpan? handoffTimeout = null)
    {
        dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(restore);
        ArgumentNullException.ThrowIfNull(onError);
        var timeout = handoffTimeout ?? TimeSpan.FromSeconds(2);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(5))
            return new(StartStatus.Failed, null, "单实例交接等待须在 0～5 秒内。");
        Mutex? mutex = null;
        EventWaitHandle? signal = null;
        var ownsMutex = false;
        var transferred = false;
        var abandoned = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            var sid = identity.User?.Value ?? throw new InvalidOperationException("无法取得当前 Windows 用户身份。");
            var names = BuildNames(dataDirectory, sid, process.SessionId);
            mutex = new Mutex(false, names.Mutex);
            try { ownsMutex = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; abandoned = true; }
            if (!ownsMutex) return SignalPrimary(names.RestoreEvent, timeout);
            signal = new EventWaitHandle(false, EventResetMode.AutoReset, names.RestoreEvent);
            var instance = new SingleInstance(mutex, signal, dispatcher, restore, onError);
            transferred = true;
            return new(StartStatus.Primary, instance, null, abandoned);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            WaitHandleCannotBeOpenedException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Fail closed: failure to establish ownership must never fall through to a second Workspace.
            return new(StartStatus.Failed, null, "无法建立单实例通道，请检查现有 CClean 后重试：" + error.Message);
        }
        finally
        {
            if (!transferred)
            {
                signal?.Dispose();
                if (ownsMutex) mutex!.ReleaseMutex();
                mutex?.Dispose();
            }
        }
    }

    private static StartResult SignalPrimary(string eventName, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(eventName);
                return signal.Set()
                    ? new(StartStatus.RestoreSignaled, null, null)
                    : new(StartStatus.Failed, null, "现有实例未接受恢复信号；未启动第二个工作区。");
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The primary owns the mutex before creating its event. Retry this startup race, never take over.
                if (elapsed.Elapsed >= timeout) break;
                Thread.Sleep(25);
            }
        } while (elapsed.Elapsed < timeout);
        return new(StartStatus.Failed, null, "现有实例仍在启动或退出，恢复通道尚不可用；请稍后重试。未启动第二个工作区。");
    }

    private void RestoreRequested()
    {
        lock (gate)
        {
            if (disposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished ||
                pendingRestore is { Status: DispatcherOperationStatus.Pending }) return;
            try
            {
                pendingRestore = dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    lock (gate)
                    {
                        pendingRestore = null;
                        if (disposed) return;
                    }
                    try { restore(); }
                    catch (Exception error) { onError(error); }
                }));
            }
            catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            { pendingRestore = null; }
        }
    }

    internal static ObjectNames BuildNames(string dataDirectory, string userSid, int sessionId)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || string.IsNullOrWhiteSpace(userSid) || sessionId < 0)
            throw new ArgumentException("数据目录、用户身份和会话必须明确。");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant();
        var material = userSid + "\n" + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + normalized;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        return new(@"Local\CClean." + key + ".Owner", @"Local\CClean." + key + ".Restore", normalized);
    }

    // Pure name-boundary checks only; this does not open handles, create a directory or prove process handoff.
    internal static IReadOnlyList<string> BoundarySelfCheck()
    {
        var failures = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "CClean-Lifecycle-Boundary");
        var names = BuildNames(root, "test-user", 1);
        if (names != BuildNames(Path.Combine(root, ".") + Path.DirectorySeparatorChar, "test-user", 1)) failures.Add("path-normalization");
        if (names != BuildNames(root.ToLowerInvariant(), "test-user", 1)) failures.Add("path-case");
        if (names.Mutex == BuildNames(root, "other-user", 1).Mutex) failures.Add("user-isolation");
        if (names.Mutex == BuildNames(root, "test-user", 2).Mutex) failures.Add("session-isolation");
        if (names.Mutex == BuildNames(root + "-other", "test-user", 1).Mutex) failures.Add("data-isolation");
        if (names.Mutex == names.RestoreEvent || !names.Mutex.StartsWith(@"Local\", StringComparison.Ordinal)) failures.Add("object-separation");
        return failures;
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("单实例锁须由取得它的 UI 线程释放。");
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pendingRestore?.Abort();
            pendingRestore = null;
        }
        // In-flight callbacks only enqueue guarded dispatcher work; none can invoke restore after disposed.
        registration.Unregister(null);
        restoreEvent.Dispose();
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
