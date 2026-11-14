using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CClean.Poc;

internal sealed record NativeDirectoryNotification(long Seq, DateTimeOffset ReceiptUtc, uint Action,
    [property: JsonIgnore] ulong FileId, [property: JsonIgnore] ulong ParentFileId, string Name,
    [property: JsonIgnore] long CreationTimeFileTime, [property: JsonIgnore] long LastModificationTimeFileTime,
    [property: JsonIgnore] long LastChangeTimeFileTime, [property: JsonIgnore] long LastAccessTimeFileTime, long AllocatedLength, long FileSize,
    uint FileAttributes, uint ReparseTagOrEaSize)
{
    // Exact strings survive JSON consumers whose numbers cannot represent every 64-bit file identifier.
    public string FileIdHex => $"0x{FileId:X16}";
    public string ParentFileIdHex => $"0x{ParentFileId:X16}";
    public string CreationTimeFileTimeDecimal => CreationTimeFileTime.ToString(CultureInfo.InvariantCulture);
    public string LastModificationTimeFileTimeDecimal => LastModificationTimeFileTime.ToString(CultureInfo.InvariantCulture);
    public string LastChangeTimeFileTimeDecimal => LastChangeTimeFileTime.ToString(CultureInfo.InvariantCulture);
    public string LastAccessTimeFileTimeDecimal => LastAccessTimeFileTime.ToString(CultureInfo.InvariantCulture);
}

internal sealed record NativeNotificationGap(DateTimeOffset Utc, string Stage, int? NativeError, string Detail);
internal sealed record NativeParserCheck(string Name, bool Passed, string Detail);

// Isolated fixture diagnostics only. Extended notifications have no PID or process attribution field.
internal sealed class NativeDirectoryNotifications : IDisposable
{
    internal const int BufferBytes = 64 * 1024;
    internal const int EventLimit = 4096;
    private const int HeaderBytes = 84;
    private const int ErrorOperationAborted = 995, ErrorIoPending = 997, ErrorNotifyEnumDir = 1022, ErrorNotFound = 1168;
    private static readonly Encoding StrictUtf16 = new UnicodeEncoding(false, false, true);
    private readonly object ioGate = new(), eventsGate = new();
    private readonly List<NativeDirectoryNotification> events = [];
    private readonly List<NativeNotificationGap> gaps = [];
    private readonly TaskCompletionSource<bool> armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread worker;
    private readonly string root;
    private readonly bool subtree;
    private SafeFileHandle? directory;
    private volatile bool stopping, exited;
    private volatile string exitReason = "starting";
    private int disposeRequested;
    private long totalGaps;

    internal NativeDirectoryNotifications(string root, bool subtree = true)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows extended directory notifications are required.");
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("An explicit local fixture directory is required.", nameof(root));
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (this.root.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(this.root)!), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Monitoring a volume root is outside fixture diagnostics.", nameof(root));
        this.subtree = subtree;
        worker = new Thread(Collect) { IsBackground = true, Name = "CClean fixture extended notifications" };
        worker.Start();
    }

    internal bool WaitUntilArmed(TimeSpan timeout) => armed.Task.Wait(timeout) && armed.Task.Result && !stopping && !exited;
    internal IReadOnlyList<NativeDirectoryNotification> Snapshot() { lock (eventsGate) return events.ToArray(); }
    internal IReadOnlyList<NativeNotificationGap> Errors { get { lock (eventsGate) return gaps.ToArray(); } }
    internal IReadOnlyList<NativeNotificationGap> Gaps => Errors;
    internal long TotalGaps { get { lock (eventsGate) return totalGaps; } }
    internal bool Exited => exited;
    internal string ExitReason => exitReason;

    private void Collect()
    {
        SafeFileHandle? handle = null;
        EventWaitHandle? completion = null;
        var buffer = IntPtr.Zero;
        var overlapped = IntPtr.Zero;
        var pending = false;
        try
        {
            // FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES; share read/write/delete; BACKUP_SEMANTICS | OVERLAPPED | OPEN_REPARSE_POINT.
            handle = CreateFileW(root, 0x81, 7, IntPtr.Zero, 3, 0x42200000, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot open fixture directory.");
            if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag tag, 8))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot verify fixture directory attributes.");
            if ((tag.Attributes & 0x10) == 0 || (tag.Attributes & 0x400) != 0)
                throw new IOException("A directory reparse point cannot be used as the fixture notification root.");
            completion = new EventWaitHandle(false, EventResetMode.ManualReset);
            buffer = Marshal.AllocHGlobal(BufferBytes); // HGlobal satisfies the API's DWORD alignment requirement.
            overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<OverlappedState>());
            var copy = new byte[BufferBytes];
            lock (ioGate) directory = handle;
            exitReason = "running";
            while (!stopping)
            {
                completion.Reset();
                Marshal.StructureToPtr(new OverlappedState { EventHandle = completion.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
                bool queued;
                int queueError;
                lock (ioGate)
                {
                    if (stopping) break;
                    // Extended information class = 2. The async byte count is obtained only from GetOverlappedResult.
                    queued = ReadDirectoryChangesExW(handle, buffer, BufferBytes, subtree, 0x17F, IntPtr.Zero, overlapped, IntPtr.Zero, 2);
                    queueError = queued ? 0 : Marshal.GetLastPInvokeError();
                    pending = queued || queueError == ErrorIoPending;
                }
                if (!pending)
                {
                    Gap("queue", queueError, "ReadDirectoryChangesExW could not arm extended notifications.");
                    exitReason = "queue-failed";
                    break;
                }
                armed.TrySetResult(true); // The driver must wait for this before changing the fixture.
                while (!completion.WaitOne(100)) if (stopping) CancelPending();
                var success = GetOverlappedResult(handle, overlapped, out var transferred, true);
                var readError = success ? 0 : Marshal.GetLastPInvokeError();
                pending = false;
                if (!success)
                {
                    if (stopping && readError == ErrorOperationAborted) { exitReason = "cancelled"; break; }
                    Gap("completion", readError, "Extended notification request completed with an error; continuity is unknown.");
                    if (readError == ErrorNotifyEnumDir) continue;
                    exitReason = "completion-failed";
                    break;
                }
                if (transferred == 0)
                {
                    Gap("overflow", null, "Zero-byte completion: native notifications were discarded; no continuity is claimed.");
                    continue;
                }
                if (transferred > BufferBytes)
                {
                    Gap("parse", null, "Native byte count exceeds the allocated 64 KiB buffer.");
                    exitReason = "parse-failed";
                    break;
                }
                Marshal.Copy(buffer, copy, 0, (int)transferred);
                if (!Parse(copy.AsSpan(0, (int)transferred))) break;
            }
            if (exitReason == "running") exitReason = stopping ? "cancelled" : "completed";
        }
        catch (Exception error)
        {
            Gap("collector", error is Win32Exception native ? native.NativeErrorCode : null, error.ToString());
            exitReason = "collector-failed";
        }
        finally
        {
            // Native memory remains owned by this background worker until the outstanding I/O really completes.
            // Dispose never frees it behind the kernel, even if cancellation is delayed by a driver.
            if (pending && handle is not null && !handle.IsInvalid)
            {
                CancelPending();
                if (!GetOverlappedResult(handle, overlapped, out _, true))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != ErrorOperationAborted) Gap("cancel-completion", error, "Outstanding I/O did not complete with expected cancellation.");
                }
            }
            lock (ioGate) { directory = null; handle?.Dispose(); }
            if (overlapped != IntPtr.Zero) Marshal.FreeHGlobal(overlapped);
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            completion?.Dispose();
            exited = true;
            armed.TrySetResult(false); // Opening/arming failure must wake the driver rather than use its full timeout.
        }
    }

    private bool Parse(ReadOnlySpan<byte> batch)
    {
        var offset = 0;
        var receipt = DateTimeOffset.UtcNow;
        try
        {
            while (offset < batch.Length)
            {
                var (next, entry) = DecodeRecord(batch[offset..], events.Count + 1L, receipt);
                lock (eventsGate)
                {
                    if (events.Count == EventLimit)
                    {
                        Gap("event-limit", null, "4096 retained events reached; collector stopped and later notifications are not represented.");
                        exitReason = "event-limit";
                        return false;
                    }
                    events.Add(entry);
                }
                if (next == 0) return true;
                offset += (int)next;
            }
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or DecoderFallbackException or ArgumentException)
        {
            Gap("parse", null, $"Malformed extended notification at byte {offset}: {error.Message}");
            exitReason = "parse-failed";
            return false;
        }
    }

    private static (uint Next, NativeDirectoryNotification Entry) DecodeRecord(ReadOnlySpan<byte> record, long seq, DateTimeOffset receipt)
    {
        // SDK FILE_NOTIFY_EXTENDED_INFORMATION, not FILE_NOTIFY_INFORMATION:
        // https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-file_notify_extended_information
        // DWORDs 0/4; six LARGE_INTEGERs 8..48; attrs/union 56/60; IDs 64/72; byte length 80; UTF-16 name 84.
        if (record.Length < HeaderBytes) throw new InvalidDataException("Truncated 84-byte extended notification header.");
        var next = BinaryPrimitives.ReadUInt32LittleEndian(record);
        var nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(record[80..]);
        if (next != 0 && (next % 4 != 0 || next > record.Length || next < HeaderBytes || record.Length - next < HeaderBytes))
            throw new InvalidDataException("NextEntryOffset is unaligned or cannot contain this record and the following header.");
        var recordLength = next == 0 ? record.Length : (int)next;
        if (nameBytes == 0 || nameBytes % 2 != 0 || nameBytes > recordLength - HeaderBytes)
            throw new InvalidDataException("UTF-16 filename byte length is odd, empty, or exceeds its own record.");
        var name = StrictUtf16.GetString(record.Slice(HeaderBytes, (int)nameBytes));
        if (Path.IsPathRooted(name) || name.IndexOf('\0') >= 0 ||
            name.Split('\\', '/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException("Notification name is not confined to a fixture-relative path.");
        return (next, new(seq, receipt, BinaryPrimitives.ReadUInt32LittleEndian(record[4..]),
            BinaryPrimitives.ReadUInt64LittleEndian(record[64..]), BinaryPrimitives.ReadUInt64LittleEndian(record[72..]), name,
            BinaryPrimitives.ReadInt64LittleEndian(record[8..]), BinaryPrimitives.ReadInt64LittleEndian(record[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(record[24..]), BinaryPrimitives.ReadInt64LittleEndian(record[32..]),
            BinaryPrimitives.ReadInt64LittleEndian(record[40..]), BinaryPrimitives.ReadInt64LittleEndian(record[48..]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[56..]), BinaryPrimitives.ReadUInt32LittleEndian(record[60..])));
    }

    internal static IReadOnlyList<NativeParserCheck> ParserSelfCheck()
    {
        var results = new List<NativeParserCheck>();
        var valid = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(valid.AsSpan(4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(valid.AsSpan(48), 17);
        BinaryPrimitives.WriteUInt64LittleEndian(valid.AsSpan(64), 0xFEDCBA9876543210UL);
        BinaryPrimitives.WriteUInt64LittleEndian(valid.AsSpan(72), 0x0123456789ABCDEFUL);
        BinaryPrimitives.WriteUInt32LittleEndian(valid.AsSpan(80), 2);
        valid[84] = (byte)'x';
        var (_, decoded) = DecodeRecord(valid, 1, DateTimeOffset.UnixEpoch);
        results.Add(new("extended-header-layout", decoded.Action == 1 && decoded.Name == "x" && decoded.FileSize == 17 &&
            decoded.FileIdHex == "0xFEDCBA9876543210" && decoded.ParentFileIdHex == "0x0123456789ABCDEF", "Known 84-byte header offsets and exact 64-bit ID hex strings."));
        Reject("truncated-header", valid[..83]);
        var odd = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(odd.AsSpan(80), 3);
        Reject("odd-utf16-byte-length", odd);
        var outside = (byte[])valid.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(outside, 256);
        Reject("next-offset-outside-buffer", outside);
        var surrogate = (byte[])valid.Clone(); surrogate[84] = 0; surrogate[85] = 0xD8;
        Reject("invalid-utf16-surrogate", surrogate);
        var crossesRecord = new byte[176]; valid.CopyTo(crossesRecord, 0); valid.CopyTo(crossesRecord, 88);
        BinaryPrimitives.WriteUInt32LittleEndian(crossesRecord, 88);
        BinaryPrimitives.WriteUInt32LittleEndian(crossesRecord.AsSpan(80), 6);
        Reject("filename-crosses-own-record", crossesRecord);
        return results;

        void Reject(string name, byte[] bytes)
        {
            try { DecodeRecord(bytes, 1, DateTimeOffset.UnixEpoch); results.Add(new(name, false, "Malformed record was accepted.")); }
            catch (Exception error) when (error is InvalidDataException or DecoderFallbackException or ArgumentException)
            { results.Add(new(name, true, error.Message)); }
        }
    }

    private void Gap(string stage, int? error, string detail)
    {
        lock (eventsGate)
        {
            totalGaps++;
            if (gaps.Count < 256) gaps.Add(new(DateTimeOffset.UtcNow, stage, error, detail));
            else gaps[^1] = new(DateTimeOffset.UtcNow, "gap-record-limit", null, $"Gap detail limit reached; total observed gaps: {totalGaps}.");
        }
    }

    private void CancelPending()
    {
        lock (ioGate)
        {
            if (directory is null) return;
            if (!CancelIoEx(directory, IntPtr.Zero))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorNotFound) Gap("cancel", error, "CancelIoEx failed; native I/O may still be pending.");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeRequested, 1) != 0) return;
        stopping = true;
        CancelPending();
        if (!worker.Join(TimeSpan.FromSeconds(3)))
            Gap("dispose-timeout", null, "Collector did not exit within 3 seconds after cancellation. Exited=false; its background worker retains native memory until completion.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OverlappedState
    {
        public IntPtr Internal, InternalHigh;
        public uint Offset, OffsetHigh;
        public IntPtr EventHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadDirectoryChangesExW(SafeFileHandle directory, IntPtr buffer, uint bufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool subtree, uint filter, IntPtr bytesReturned, IntPtr overlapped, IntPtr completion, int informationClass);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
}
