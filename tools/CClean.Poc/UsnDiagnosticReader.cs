using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using CClean.Core.Files;
using Microsoft.Win32.SafeHandles;

namespace CClean.Poc;

// Isolation diagnostics only. Neither a matching record nor a complete interval proves its process of origin.
internal static class UsnDiagnosticReader
{
    private const uint QueryUsnJournal = 0x000900f4;
    private const uint ReadUsnJournal = 0x000900bb;
    private const uint ReadFileUsnData = 0x000900eb;
    private static readonly Encoding StrictUtf16 = new UnicodeEncoding(false, false, true);

    internal sealed record FileIdentity(string Path, string VolumePath, string CoreIdentity,
        [property: JsonIgnore] ulong VolumeSerial64, string FileId128,
        [property: JsonIgnore] ulong FileReference64, uint VolumeSerial32,
        [property: JsonIgnore] long CreationTime, bool IsDirectory, bool IsReparsePoint,
        FileUsnIdentity FileUsnBridge, FileUsnIdentity FileUsnBridge128)
    {
        public string VolumeSerial64Hex => VolumeSerial64.ToString("X16", CultureInfo.InvariantCulture);
        public string FileReference64Hex => FileReference64.ToString("X16", CultureInfo.InvariantCulture);
        public string CreationTimeDecimal => CreationTime.ToString(CultureInfo.InvariantCulture);
        public bool LegacyReferenceBridgeProven => FileUsnBridge.Status == "ok" &&
            FileUsnBridge.MajorVersion == 2 && FileUsnBridge.MatchesNativeIdentity == true;
        public bool Full128BridgeProven => FileUsnBridge128.Status == "ok" &&
            FileUsnBridge128.MajorVersion == 3 && FileUsnBridge128.MatchesNativeIdentity == true;
    }
    internal sealed record FileUsnIdentity(string Status, int? Win32Error, string? Error,
        ushort? MajorVersion, string? FileId, string? ParentFileId, string? FileIdFormat,
        bool? MatchesNativeIdentity, ushort RequestedMajorVersion)
    {
        public string Limitation => "Identity fields only: READ_FILE_USN_DATA timestamp, reason and SourceInfo are not valid activity evidence. A V3 match does not prove a V2/64-bit bridge.";
    }
    internal sealed record IdentityResult(string Status, int? Win32Error, string? Error, FileIdentity? Identity);
    internal sealed record OpenAttempt(uint DesiredAccess, int? Win32Error);
    internal sealed record JournalState([property: JsonIgnore] ulong JournalId,
        [property: JsonIgnore] long FirstUsn, [property: JsonIgnore] long NextUsn,
        [property: JsonIgnore] long LowestValidUsn, [property: JsonIgnore] long MaxUsn,
        [property: JsonIgnore] ulong MaximumSize, [property: JsonIgnore] ulong AllocationDelta,
        ushort? MinSupportedMajorVersion, ushort? MaxSupportedMajorVersion)
    {
        public string JournalIdHex => JournalId.ToString("X16", CultureInfo.InvariantCulture);
        public string FirstUsnDecimal => FirstUsn.ToString(CultureInfo.InvariantCulture);
        public string NextUsnDecimal => NextUsn.ToString(CultureInfo.InvariantCulture);
        public string LowestValidUsnDecimal => LowestValidUsn.ToString(CultureInfo.InvariantCulture);
        public string MaxUsnDecimal => MaxUsn.ToString(CultureInfo.InvariantCulture);
        public string MaximumSizeDecimal => MaximumSize.ToString(CultureInfo.InvariantCulture);
        public string AllocationDeltaDecimal => AllocationDelta.ToString(CultureInfo.InvariantCulture);
    }
    internal sealed record QueryResult(string Status, int? Win32Error, string? Error, string VolumePath,
        string OpenAccess, IReadOnlyList<OpenAttempt> OpenAttempts, JournalState? State);
    internal sealed record ReadLimits(long MaxBytes = 4 * 1024 * 1024, int MaxRecords = 20_000,
        int MaxMilliseconds = 2000, int BufferBytes = 64 * 1024);
    internal sealed record JournalRecord(ushort MajorVersion, ushort MinorVersion, string FileId,
        string ParentFileId, string FileIdFormat, [property: JsonIgnore] long Usn,
        [property: JsonIgnore] long TimestampFileTimeUtc, uint Reason, uint SourceInfo, string Name, string Match)
    {
        public string UsnDecimal => Usn.ToString(CultureInfo.InvariantCulture);
        public string TimestampFileTimeUtcDecimal => TimestampFileTimeUtc.ToString(CultureInfo.InvariantCulture);
    }
    internal sealed record ReadResult
    {
        public string Status { get; init; } = "not-started";
        public int? Win32Error { get; init; }
        public string? Error { get; init; }
        public string VolumePath { get; init; } = "";
        public string OpenAccess { get; init; } = "none";
        public IReadOnlyList<OpenAttempt> OpenAttempts { get; init; } = [];
        public JournalState? Before { get; init; }
        public JournalState? After { get; init; }
        [JsonIgnore] public long StartUsn { get; init; }
        [JsonIgnore] public long StopNextUsn { get; init; }
        [JsonIgnore] public long NextCursor { get; init; }
        public string StartUsnDecimal => StartUsn.ToString(CultureInfo.InvariantCulture);
        public string StopNextUsnDecimal => StopNextUsn.ToString(CultureInfo.InvariantCulture);
        public string NextCursorDecimal => NextCursor.ToString(CultureInfo.InvariantCulture);
        public long BytesRead { get; init; }
        public int RecordsRead { get; init; }
        public int MatchedRecords { get; init; }
        public int UnmatchedRecords { get; init; }
        public int UnknownParentRecords { get; init; }
        public int FixtureIdentitiesWithout64Bridge { get; init; }
        public int FixtureIdentitiesWithout128Bridge { get; init; }
        public int RecordsAtOrAfterStop { get; init; }
        public IReadOnlyList<JournalRecord> Records { get; init; } = [];
        public long ElapsedMilliseconds { get; init; }
        public bool IntervalComplete { get; init; }
        public bool FixtureCoverageProven => false;
        public bool ProcessAttributionProven => false;
        public bool SynchronousCallMayExceedTimeBudget => true;
        public string CoverageLimit => "Only initially supplied identities are mapped. Unmatched/unknown-parent records are not proven outside the fixture; no such names are retained. A complete interval does not prove source or exclude coalesced changes.";
    }

    internal static IdentityResult ReadFileIdentity(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (full.EndsWith(':')) full += Path.DirectorySeparatorChar;
            using var handle = WindowsFileMetadata.Open(full); // read attributes, share read/write/delete, no reparse follow
            return ReadFileIdentity(handle, full);
        }
        catch (Exception error) when (IsDiagnosticError(error))
        { return new("identity-error", Win32Code(error), error.Message, null); }
    }

    // Caller retains ownership. All three representations are obtained from this exact handle, not a reopened path.
    internal static IdentityResult ReadFileIdentity(SafeFileHandle handle, string observedPath)
    {
        try
        {
            var snapshot = WindowsFileMetadata.Read(handle, observedPath);
            var id = new byte[24];
            if (!GetFileInformationByHandleEx(handle, 18, id, (uint)id.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var legacy = new byte[52];
            if (!GetFileInformationByHandle(handle, legacy)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var prefix = Convert.ToHexString(id) + ":";
            if (snapshot.Identity is null || !snapshot.Identity.StartsWith(prefix, StringComparison.Ordinal) ||
                !long.TryParse(snapshot.Identity.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var created))
                return new("identity-mismatch", null, "Core identity and FILE_ID_INFO from the same handle differ.", null);
            var legacyCreated = ((ulong)U32(legacy, 8) << 32) | U32(legacy, 4);
            if (unchecked((long)legacyCreated) != created)
                return new("identity-mismatch", null, "Creation times from the same handle differ.", null);
            var volume = NormalizeVolume(Path.GetPathRoot(Path.GetFullPath(observedPath))!);
            var id128 = Convert.ToHexString(id.AsSpan(8, 16));
            var reference64 = ((ulong)U32(legacy, 44) << 32) | U32(legacy, 48);
            // Separate exact-version requests preserve both outcomes instead of silently choosing one representation.
            var bridge = ReadFileUsnIdentity(handle, reference64, id128, 2);
            var bridge128 = ReadFileUsnIdentity(handle, reference64, id128, 3);
            return new("ok", null, null, new(observedPath, volume, snapshot.Identity,
                U64(id, 0), id128, reference64, U32(legacy, 28), created,
                snapshot.Attributes.HasFlag(FileAttributes.Directory), snapshot.Attributes.HasFlag(FileAttributes.ReparsePoint), bridge, bridge128));
        }
        catch (Exception error) when (IsDiagnosticError(error))
        { return new("identity-error", Win32Code(error), error.Message, null); }
    }

    internal static QueryResult Query(string volumePath)
    {
        var attempts = new List<OpenAttempt>(2);
        var access = "none";
        try
        {
            var volume = NormalizeVolume(volumePath);
            EnsureNtfs(volume);
            using var handle = OpenVolume(volume, attempts, out access);
            return new("ok", null, null, volume, access, attempts, QueryHandle(handle));
        }
        catch (Exception error) when (IsDiagnosticError(error))
        { return new("query-error", Win32Code(error), error.Message, volumePath, access, attempts, null); }
    }

    internal static ReadResult Read(string volumePath, JournalState expected, long startUsn, long stopNextUsn,
        IReadOnlyCollection<FileIdentity> fixtureIdentities, ReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        limits ??= new();
        var timer = Stopwatch.StartNew();
        var attempts = new List<OpenAttempt>(2);
        var records = new List<JournalRecord>();
        var volume = volumePath;
        var access = "none";
        JournalState? before = null, after = null;
        long cursor = startUsn, bytesRead = 0;
        int seen = 0, unmatched = 0, unknownParent = 0, afterStop = 0, unbridged = 0, unbridged128 = 0;
        ReadResult Finish(string status, string? error = null, int? code = null) => new()
        {
            Status = status, Error = error, Win32Error = code, VolumePath = volume, OpenAccess = access,
            OpenAttempts = attempts.ToArray(), Before = before, After = after, StartUsn = startUsn,
            StopNextUsn = stopNextUsn, NextCursor = cursor, BytesRead = bytesRead, RecordsRead = seen,
            MatchedRecords = records.Count, UnmatchedRecords = unmatched, UnknownParentRecords = unknownParent,
            FixtureIdentitiesWithout64Bridge = unbridged,
            FixtureIdentitiesWithout128Bridge = unbridged128,
            RecordsAtOrAfterStop = afterStop, Records = records.ToArray(),
            ElapsedMilliseconds = timer.ElapsedMilliseconds, IntervalComplete = status == "ok"
        };
        string? BudgetStatus() => cancellationToken.IsCancellationRequested ? "cancelled" :
            timer.ElapsedMilliseconds >= limits.MaxMilliseconds ? "time-limit" : null;
        try
        {
            if (limits.MaxBytes is < 4096 or > 64 * 1024 * 1024 || limits.MaxRecords is < 1 or > 1_000_000 ||
                limits.MaxMilliseconds is < 1 or > 60_000 || limits.BufferBytes is < 4096 or > 1024 * 1024)
                return Finish("invalid-input", "Read budget must be bounded: 4 KiB..64 MiB, 1..1,000,000 records, 1..60,000 ms, 4 KiB..1 MiB buffer.");
            if (fixtureIdentities.Count is < 1 or > 4096 || startUsn < 0 || stopNextUsn < startUsn || stopNextUsn > expected.NextUsn)
                return Finish("invalid-input", "Require 1..4096 fixture identities and an explicit interval within the captured NextUsn.");
            volume = NormalizeVolume(volumePath);
            EnsureNtfs(volume);
            if (fixtureIdentities.Any(i => !i.VolumePath.Equals(volume, StringComparison.OrdinalIgnoreCase)))
                return Finish("invalid-input", "Every supplied identity must belong to this volume.");
            if (fixtureIdentities.Select(i => i.VolumeSerial64).Distinct().Count() != 1)
                return Finish("invalid-input", "Fixture identities contain different volume serials.");
            unbridged = fixtureIdentities.Count(i => !i.LegacyReferenceBridgeProven);
            unbridged128 = fixtureIdentities.Count(i => !i.Full128BridgeProven);
            var ids2 = fixtureIdentities.Where(i => i.LegacyReferenceBridgeProven)
                .Select(i => i.FileReference64.ToString("X16", CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
            var ids3 = fixtureIdentities.Where(i => i.Full128BridgeProven)
                .Select(i => i.FileId128).ToHashSet(StringComparer.Ordinal);
            if (BudgetStatus() is { } initialBudget) return Finish(initialBudget);
            using var handle = OpenVolume(volume, attempts, out access);
            before = QueryHandle(handle);
            if (BudgetStatus() is { } queryBudget) return Finish(queryBudget);
            if (ValidateState(before, expected, startUsn, stopNextUsn) is { } initialGap)
                return Finish(initialGap.Status, initialGap.Error);
            var buffer = new byte[(int)Math.Min(limits.BufferBytes, limits.MaxBytes)];
            while (cursor < stopNextUsn)
            {
                if (BudgetStatus() is { } budget) return Finish(budget);
                if (seen >= limits.MaxRecords) return Finish("record-limit", "Record budget reached before completing the interval.");
                var remaining = limits.MaxBytes - bytesRead;
                if (remaining < 4096) return Finish("byte-limit", "Byte budget has less than one bounded read buffer remaining.");
                var requestedBytes = (uint)Math.Min(buffer.Length, remaining);
                // READ_USN_JOURNAL_DATA_V1 if supported, otherwise V0. No reason filtering and no wait for future records.
                var request = new byte[before.MaxSupportedMajorVersion is null ? 40 : 48];
                BinaryPrimitives.WriteInt64LittleEndian(request, cursor);
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), uint.MaxValue);
                // ReturnOnlyOnClose = 0, Timeout = 0, BytesToWaitFor = 0 (zero-filled).
                BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(32), expected.JournalId);
                if (request.Length == 48)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(40), 2);
                    BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(42), before.MaxSupportedMajorVersion!.Value);
                }
                if (!DeviceIoControl(handle, ReadUsnJournal, request, (uint)request.Length, buffer, requestedBytes, out var returned, IntPtr.Zero))
                {
                    var code = Marshal.GetLastWin32Error();
                    return Finish(code == 1181 ? "journal-gap" : code is 1178 or 1179 ? "journal-unavailable" : "read-error",
                        "FSCTL_READ_USN_JOURNAL failed: " + new Win32Exception(code).Message, code);
                }
                bytesRead += returned;
                if (returned < 8 || returned > requestedBytes) return Finish("invalid-buffer", "Native result length is outside the requested buffer.");
                var next = I64(buffer, 0);
                if (next <= cursor) return Finish("cursor-no-progress", "Native next cursor failed to advance inside a nonempty requested interval.");
                var offset = 8;
                var previous = cursor - 1;
                while (offset < returned)
                {
                    if (BudgetStatus() is { } parseBudget) return Finish(parseBudget);
                    if (seen >= limits.MaxRecords) return Finish("record-limit", "Record budget reached while parsing a native buffer.");
                    if (!TryParseRecord(buffer.AsSpan(offset, checked((int)returned - offset)), out var item, out var parseStatus, out var parseError))
                        return Finish(parseStatus, parseError);
                    if (item.Usn < cursor || item.Usn <= previous || item.Usn >= next)
                        return Finish("invalid-cursor", "Record USNs are outside the returned interval or are not strictly ordered.");
                    previous = item.Usn;
                    seen++;
                    if (item.Usn >= stopNextUsn) afterStop++;
                    else
                    {
                        var known = item.Major == 2 ? ids2 : ids3;
                        var objectKnown = known.Contains(item.FileId);
                        var parentKnown = known.Contains(item.ParentId);
                        if (!parentKnown) unknownParent++;
                        if (objectKnown || parentKnown)
                        {
                            var name = StrictUtf16.GetString(buffer, offset + item.NameOffset, item.NameLength);
                            if (name.Contains('\0')) return Finish("invalid-record", "Matched record name contains a NUL character.");
                            records.Add(new(item.Major, item.Minor, item.FileId, item.ParentId,
                                item.Major == 2 ? "FRN64-X16" : "FILE_ID_128-raw-hex", item.Usn, item.Timestamp,
                                item.Reason, item.SourceInfo, name,
                                objectKnown && parentKnown ? "known-object-and-parent" : objectKnown ? "known-object" : "known-parent"));
                        }
                        else unmatched++; // Do not decode or retain names of records with no known fixture identity.
                    }
                    offset += item.Length;
                }
                cursor = next;
                if (BudgetStatus() is { } readBudget) return Finish(readBudget);
                after = QueryHandle(handle);
                if (BudgetStatus() is { } afterBudget) return Finish(afterBudget);
                if (ValidateState(after, expected, startUsn, stopNextUsn) is { } gap)
                    return Finish(gap.Status, gap.Error);
                if (cursor > after.NextUsn) return Finish("invalid-cursor", "Read cursor is ahead of the journal's current NextUsn.");
            }
            // Even an empty interval must recheck the journal generation and validity.
            after ??= QueryHandle(handle);
            if (BudgetStatus() is { } finalBudget) return Finish(finalBudget);
            if (ValidateState(after, expected, startUsn, stopNextUsn) is { } finalGap)
                return Finish(finalGap.Status, finalGap.Error);
            return Finish("ok");
        }
        catch (Exception error) when (IsDiagnosticError(error))
        { return Finish("diagnostic-error", error.Message, Win32Code(error)); }
    }

    private static (string Status, string Error)? ValidateState(JournalState actual, JournalState expected, long start, long stop)
    {
        if (actual.JournalId != expected.JournalId) return ("journal-changed", "JournalId differs from the captured generation.");
        if (actual.FirstUsn < 0 || actual.LowestValidUsn < 0 || actual.NextUsn < actual.FirstUsn || actual.NextUsn < actual.LowestValidUsn)
            return ("invalid-journal-state", "Journal bounds are invalid.");
        if (start < actual.FirstUsn || start < actual.LowestValidUsn)
            return ("journal-gap", "Requested start has been trimmed or is below LowestValidUsn.");
        if (actual.NextUsn < stop || actual.NextUsn < expected.NextUsn)
            return ("journal-regressed", "Journal NextUsn regressed relative to the captured interval.");
        if (actual.MaxSupportedMajorVersion is < 2 || actual.MinSupportedMajorVersion is > 3)
            return ("unsupported-journal-version", "Journal reports no supported V2/V3 record range.");
        return null;
    }

    private readonly record struct RawRecord(int Length, ushort Major, ushort Minor, string FileId, string ParentId,
        long Usn, long Timestamp, uint Reason, uint SourceInfo, int NameOffset, int NameLength);

    private static bool TryParseRecord(ReadOnlySpan<byte> bytes, out RawRecord item, out string status, out string? error)
    {
        item = default;
        status = "invalid-record";
        error = "USN record header, length, name, timestamp or version is invalid.";
        if (bytes.Length < 8) return false;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        if (length < 8 || length > bytes.Length || length % 8 != 0) return false;
        if (major is not (2 or 3) || minor != 0)
        {
            status = "unsupported-record-version";
            error = $"USN record version {major}.{minor} is unsupported; interval coverage has a gap.";
            return false;
        }
        var minimum = major == 2 ? 60 : 76;
        if (length < minimum) return false;
        var usnOffset = major == 2 ? 24 : 40;
        var usn = BinaryPrimitives.ReadInt64LittleEndian(bytes[usnOffset..]);
        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(bytes[(usnOffset + 8)..]);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(minimum - 4)..]);
        var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(minimum - 2)..]);
        if (usn < 0 || timestamp < 0 || timestamp > 2650467743999999999L || nameLength % 2 != 0 ||
            nameOffset < minimum || nameOffset % 2 != 0 || (long)nameOffset + nameLength > length) return false;
        var fileId = major == 2 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]).ToString("X16", CultureInfo.InvariantCulture) : Convert.ToHexString(bytes.Slice(8, 16));
        var parent = major == 2 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]).ToString("X16", CultureInfo.InvariantCulture) : Convert.ToHexString(bytes.Slice(24, 16));
        item = new((int)length, major, minor, fileId, parent, usn, timestamp,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[(usnOffset + 16)..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[(usnOffset + 20)..]), nameOffset, nameLength);
        error = null;
        return true;
    }

    private static FileUsnIdentity ReadFileUsnIdentity(SafeFileHandle handle, ulong reference64, string id128, ushort requestedMajor)
    {
        var request = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(request, requestedMajor);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), requestedMajor);
        var bytes = new byte[4096];
        if (!DeviceIoControl(handle, ReadFileUsnData, request, (uint)request.Length, bytes, (uint)bytes.Length, out var returned, IntPtr.Zero))
            return new("read-error", Marshal.GetLastWin32Error(), "FSCTL_READ_FILE_USN_DATA exact-version identity bridge failed.", null, null, null, null, null, requestedMajor);
        if (returned < 8 || returned > bytes.Length) return new("invalid-buffer", null, "File USN identity result is truncated.", null, null, null, null, null, requestedMajor);
        // The API declares timestamp/reason/SourceInfo invalid. Only fixed identity fields are consumed here.
        var length = U32(bytes, 0);
        var major = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        var minor = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
        if (major is not (2 or 3) || minor != 0)
            return new("unsupported-record-version", null, $"File USN identity version {major}.{minor} unsupported.", major, null, null, null, null, requestedMajor);
        var minimum = major == 2 ? 60 : 76;
        if (length < minimum || length > returned)
            return new("invalid-record", null, "File USN identity record length invalid.", major, null, null, null, null, requestedMajor);
        // This bridge consumes fixed identity fields only; file-name layout is not activity evidence either.
        var fileId = major == 2 ? U64(bytes, 8).ToString("X16", CultureInfo.InvariantCulture) : Convert.ToHexString(bytes.AsSpan(8, 16));
        var parentId = major == 2 ? U64(bytes, 16).ToString("X16", CultureInfo.InvariantCulture) : Convert.ToHexString(bytes.AsSpan(24, 16));
        var matches = fileId == (major == 2 ? reference64.ToString("X16", CultureInfo.InvariantCulture) : id128);
        var versionMatches = major == requestedMajor;
        return new(!versionMatches ? "unexpected-version" : matches ? "ok" : "identity-mismatch", null,
            !versionMatches ? $"Requested only V{requestedMajor}, received V{major}." : matches ? null : "File USN ID differs from the same handle's native identity.",
            major, fileId, parentId, major == 2 ? "FRN64-X16" : "FILE_ID_128-raw-hex", matches, requestedMajor);
    }

    // Pure in-memory checks: no journal, filesystem writes or native calls. An empty list means all assertions passed.
    internal static IReadOnlyList<string> ParserSelfCheck()
    {
        var failures = new List<string>();
        void Check(string name, bool passed) { if (!passed) failures.Add(name); }
        static byte[] Record(ushort major)
        {
            var minimum = major == 2 ? 60 : 76;
            var bytes = new byte[(minimum + 2 + 7) / 8 * 8];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), major);
            var usnOffset = major == 2 ? 24 : 40;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 0x123456789abcdef0UL);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(major == 2 ? 16 : 24), 0x42);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(usnOffset), 100);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(usnOffset + 8), 133000000000000000);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(usnOffset + 16), 0x80000200);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(minimum - 4), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(minimum - 2), (ushort)minimum);
            bytes[minimum] = (byte)'x';
            return bytes;
        }
        foreach (ushort version in new ushort[] { 2, 3 })
        {
            var bytes = Record(version);
            Check($"v{version}-valid", TryParseRecord(bytes, out var parsed, out _, out _) && parsed.Major == version && parsed.Usn == 100 && parsed.Reason == 0x80000200 && parsed.NameLength == 2);
            Check($"v{version}-truncated", !TryParseRecord(bytes.AsSpan(0, bytes.Length - 1), out _, out _, out _));
            bytes[version == 2 ? 56 : 72] = 3;
            Check($"v{version}-odd-name", !TryParseRecord(bytes, out _, out _, out _));
            bytes = Record(version);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(version == 2 ? 58 : 74), (ushort)bytes.Length);
            Check($"v{version}-name-outside-record", !TryParseRecord(bytes, out _, out _, out _));
        }
        var unsupported = Record(2);
        BinaryPrimitives.WriteUInt16LittleEndian(unsupported.AsSpan(4), 4);
        Check("unsupported-version-is-gap", !TryParseRecord(unsupported, out _, out var unsupportedStatus, out _) && unsupportedStatus == "unsupported-record-version");
        Check("short-header", !TryParseRecord(new byte[7], out _, out _, out _));
        var invalidTime = Record(2);
        BinaryPrimitives.WriteInt64LittleEndian(invalidTime.AsSpan(32), -1);
        Check("invalid-time", !TryParseRecord(invalidTime, out _, out _, out _));
        var state = new JournalState(1, 10, 100, 10, long.MaxValue, 4096, 4096, 2, 3);
        Check("state-valid", ValidateState(state, state, 10, 100) is null);
        Check("generation-change", ValidateState(state with { JournalId = 2 }, state, 10, 100)?.Status == "journal-changed");
        Check("trimmed-start", ValidateState(state with { FirstUsn = 11 }, state, 10, 100)?.Status == "journal-gap");
        Check("lowest-valid-gap", ValidateState(state with { LowestValidUsn = 11 }, state, 10, 100)?.Status == "journal-gap");
        Check("cursor-regression", ValidateState(state with { NextUsn = 99 }, state, 10, 100)?.Status == "journal-regressed");
        return failures;
    }

    private static JournalState QueryHandle(SafeFileHandle handle)
    {
        var bytes = new byte[80];
        if (!DeviceIoControl(handle, QueryUsnJournal, null, 0, bytes, (uint)bytes.Length, out var returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_QUERY_USN_JOURNAL failed.");
        if (returned < 56 || returned > bytes.Length) throw new IOException("USN journal state has an unsupported/truncated length.");
        return new(U64(bytes, 0), I64(bytes, 8), I64(bytes, 16), I64(bytes, 24), I64(bytes, 32), U64(bytes, 40), U64(bytes, 48),
            returned >= 60 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(56)) : null,
            returned >= 60 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(58)) : null);
    }

    private static SafeFileHandle OpenVolume(string volume, List<OpenAttempt> attempts, out string access)
    {
        access = "none";
        var lastCode = 0;
        foreach (var desiredAccess in new uint[] { 1, 0 })
        {
            // FILE_READ_DATA or zero desired access; OPEN_EXISTING; never GENERIC_WRITE or journal control writes.
            var handle = CreateFileW(volume, desiredAccess, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                attempts.Add(new(desiredAccess, null));
                access = desiredAccess == 1 ? "FILE_READ_DATA" : "zero-desired-access";
                return handle;
            }
            lastCode = Marshal.GetLastWin32Error();
            attempts.Add(new(desiredAccess, lastCode));
            handle.Dispose();
        }
        throw new Win32Exception(lastCode, "Read-only volume open failed; no elevation was attempted.");
    }

    private static string NormalizeVolume(string path)
    {
        var value = path.TrimEnd('\\');
        if (value.StartsWith("\\\\.\\", StringComparison.Ordinal)) value = value[4..];
        if (value.Length != 2 || !char.IsAsciiLetter(value[0]) || value[1] != ':')
            throw new ArgumentException("Only an explicit local drive root or \\\\.\\X: volume is accepted.");
        return "\\\\.\\" + char.ToUpperInvariant(value[0]) + ":";
    }

    private static void EnsureNtfs(string volume)
    {
        if (!new DriveInfo(volume[4..] + "\\").DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This diagnostic only reads an existing NTFS journal.");
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    private static bool IsDiagnosticError(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or NotSupportedException;
    private static int? Win32Code(Exception error) => error is Win32Exception native ? native.NativeErrorCode :
        error is IOException && (error.HResult & unchecked((int)0xffff0000)) == unchecked((int)0x80070000) ? error.HResult & 0xffff : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, [In] byte[]? input, uint inputSize,
        [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, [Out] byte[] information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, [Out] byte[] information);
}
