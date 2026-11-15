using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CClean.Core.Experiments;
using CClean.Core.Files;
using Microsoft.Win32.SafeHandles;

namespace CClean.Poc;

// New fixture sharing diagnostics only. No lease here changes production activity or deletion policy.
internal static class WriteLeaseProbe
{
    private const uint ReadData = 0x1, ReadAttributes = 0x80, WriteAttributes = 0x100, WriteData = 0x2, Delete = 0x10000;
    private const uint ShareRead = 1, ShareAll = 7;
    private const int SampleBytes = 16;

    internal static void Run(ExperimentSandbox sandbox, List<object> checks)
    {
        if (sandbox.Budget != 4 * 1024 * 1024)
            throw new ExperimentRefusedException("Write-lease diagnostics require exactly a 4 MiB budget.", 2);
        if (Directory.EnumerateFileSystemEntries(sandbox.RunDirectory).Any())
            throw new ExperimentRefusedException("Write-lease diagnostics require an empty, newly created run directory.", 3);
        using var boundary = new DirectoryLease(sandbox.RunDirectory); // Original shared-read/write ancestor semantics.
        var failures = new List<string>();
        var counterexamples = 0;
        var profileResults = new List<object>();
        LeaseProfile[] profiles = [new("attributes-only-80", ReadAttributes, "FILE_READ_ATTRIBUTES"),
            new("read-data-81", ReadData | ReadAttributes, "FILE_READ_DATA/FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES")];
        foreach (var profile in profiles)
        {
            var profileFailures = new List<string>();
            var profileCounterexamples = 0;
            void Scenario(string name, Func<ScenarioResult> action)
            {
                try
                {
                    var result = action();
                    if (!result.HarnessComplete) { failures.Add(profile.Id + "/" + name); profileFailures.Add(name); }
                    if (result.CounterexampleObserved) { counterexamples++; profileCounterexamples++; }
                    checks.Add(new { name = "write-lease-" + profile.Id + "-" + name, status = result.HarnessComplete ? "passed" : "failed",
                        leaseProfile = ProfileDescription(profile), evidenceStatus = "sharing-diagnostic-only-protocol-unqualified", result.CounterexampleObserved,
                        coverageConclusion = result.CounterexampleObserved ? "insufficient" : "not-established",
                        conclusionScope = "This access/share profile and stated fixture object/operation only; no conclusion about every write constraint.",
                        result.Details, productionExemptionEnabled = false, protocolQualified = false,
                        statusMeaning = "passed means stated fixture operations, observations and release checks completed; allowed mutations are counterexamples, not safety acceptance" });
                }
                catch (Exception error)
                {
                    failures.Add(profile.Id + "/" + name); profileFailures.Add(name);
                    checks.Add(new { name = "write-lease-" + profile.Id + "-" + name, status = "failed", leaseProfile = ProfileDescription(profile), error = error.ToString(),
                        productionExemptionEnabled = false, protocolQualified = false });
                }
            }

            Scenario("parent-share-read-metadata", () => ParentMetadata(sandbox, profile, "parent-read", ShareRead));
            Scenario("parent-share-none-metadata", () => ParentMetadata(sandbox, profile, "parent-none", 0));
            Scenario("parent-share-read-subtree", () => Subtree(sandbox, profile));
            Scenario("leaf-existing-attributes", () => ExistingLeaf(sandbox, profile, "attributes", WriteAttributes));
            Scenario("leaf-existing-data-writer", () => ExistingLeaf(sandbox, profile, "data", WriteData));
            Scenario("leaf-existing-delete-handle", () => ExistingLeaf(sandbox, profile, "delete", Delete));
            Scenario("leaf-consumers-cancel-release", () => LeafCancellation(sandbox, profile));
            profileResults.Add(new { leaseProfile = ProfileDescription(profile), scenarioCount = 7, failedScenarios = profileFailures,
                counterexampleScenarios = profileCounterexamples, coverageConclusion = profileCounterexamples > 0 ? "insufficient" : "not-established",
                conclusionScope = "This profile only; no cross-profile or production coverage conclusion." });
        }
        checks.Add(new { name = "write-lease-summary", status = failures.Count == 0 ? "passed" : "failed",
            scenarioCount = 14, profileCount = profiles.Length, profiles = profileResults, failedScenarios = failures, counterexampleScenarios = counterexamples,
            coverageConclusion = "profile-specific-results-only",
            fixedSampleBytes = SampleBytes, maximumGeneratedLogicalBytes = 1024,
            fixtureBudget = sandbox.Budget, reserveFreeBytes = sandbox.Reserve,
            sandbox.RunDirectory, productionExemptionEnabled = false, protocolQualified = false,
            limitation = "Synchronous, controlled operations in this process on new NTFS fixtures only. Cancellation is a controlled finally-release test, not blocked-I/O cancellation or a latency guarantee. No USN completeness, watcher attribution, user-tool compatibility, production lease, or existing-file qualification is established." });
        if (failures.Count != 0) throw new InvalidOperationException("Write-lease diagnostic gaps: " + string.Join(", ", failures));
    }

    private static ScenarioResult ParentMetadata(ExperimentSandbox sandbox, LeaseProfile profile, string name, uint share)
    {
        var root = Directory.CreateDirectory(Path.Combine(sandbox.RunDirectory, profile.Id, name, "candidate")).FullName;
        var operations = new List<Operation>();
        using var consumer = new HeldHandle(sandbox, root, WriteAttributes | ReadAttributes, ShareAll, "preexisting-parent-attribute-consumer");
        operations.Add(Observe("same-handle-time", "before-lease", root, () => SetTime(consumer, DateTime.UtcNow.AddMinutes(-4))));
        var originalAttributes = File.GetAttributes(root);
        HeldHandle? lease = null;
        object? cancellation = null;
        try
        {
            operations.Add(Observe("acquire-parent-lease", "acquire", root, () =>
            { lease = new(sandbox, root, profile.Access, share, "candidate-parent-only"); return lease.Description(); }));
            if (lease is not null)
            {
                operations.Add(Observe("same-handle-time", "lease-held", root, () => SetTime(consumer, DateTime.UtcNow.AddMinutes(-3))));
                operations.Add(Observe("new-attribute-handle-time", "lease-held", root, () =>
                {
                    using var next = new HeldHandle(sandbox, root, WriteAttributes | ReadAttributes, ShareAll, "new-parent-attribute-consumer");
                    var mutation = SetTime(next, DateTime.UtcNow.AddMinutes(-2)); next.Dispose();
                    return new { handle = next.Description(), mutation };
                }));
                operations.Add(Observe("parent-hidden-toggle", "lease-held", root, () => SetAttributes(root, originalAttributes ^ FileAttributes.Hidden)));
                operations.Add(Observe("directory-reader-open", "lease-held", root, () => OpenOnly(sandbox, root, 1 | ReadAttributes, "directory-list-consumer")));
                cancellation = CancelAndRelease(lease);
            }
        }
        finally { lease?.Dispose(); }
        operations.Add(Observe("same-handle-time", "after-release", root, () => SetTime(consumer, DateTime.UtcNow.AddMinutes(-1))));
        operations.Add(Observe("directory-reader-open", "after-release", root, () => OpenOnly(sandbox, root, 1 | ReadAttributes, "directory-list-consumer")));
        operations.Add(Observe("parent-attributes-restore", "after-release", root, () => SetAttributes(root, originalAttributes)));
        consumer.Dispose();
        return new(lease is not null && Complete(operations) && Recovered(operations),
            operations.Any(o => o.Phase == "lease-held" && o.Name != "directory-reader-open" && o.Succeeded),
            new { root, requestedLease = LeaseRequest(root, profile, share), consumer = consumer.Description(), lease = lease?.Description(), cancellation, operations });
    }

    private static ScenarioResult Subtree(ExperimentSandbox sandbox, LeaseProfile profile)
    {
        var scope = Directory.CreateDirectory(Path.Combine(sandbox.RunDirectory, profile.Id, "subtree")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(scope, "candidate")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(scope, "staging-tree", "a", "b")).FullName;
        var stagingRoot = Path.Combine(scope, "staging-tree");
        var incoming = Path.Combine(root, "moved-in");
        var outgoingRoot = Path.Combine(root, "move-out-tree");
        var outgoing = Directory.CreateDirectory(Path.Combine(outgoingRoot, "a", "b")).FullName;
        var exported = Path.Combine(scope, "exported-tree");
        var writerPath = Sample(sandbox, nested, "held-writer.bin");
        var deletePath = Sample(sandbox, nested, "held-delete.bin");
        var newWriterPath = Sample(sandbox, nested, "new-writer.bin");
        var ordinaryDelete = Sample(sandbox, nested, "path-delete.bin");
        Sample(sandbox, staging, "member.bin");
        Sample(sandbox, outgoing, "member.bin");
        var created = Path.Combine(nested, "created-under-lease.empty");
        var operations = new List<Operation>();
        using var writer = new HeldHandle(sandbox, writerPath, WriteData | ReadAttributes, ShareAll, "preexisting-child-data-consumer");
        using var deletion = new HeldHandle(sandbox, deletePath, Delete | ReadAttributes, ShareAll, "preexisting-child-delete-consumer");
        operations.Add(Observe("same-handle-write", "before-lease", writerPath, () => WriteByte(writer, 0x41)));
        HeldHandle? lease = null;
        object? cancellation = null;
        try
        {
            operations.Add(Observe("acquire-parent-lease", "acquire", root, () =>
            { lease = new(sandbox, root, profile.Access, ShareRead, "candidate-root-only-not-recursive"); return lease.Description(); }));
            if (lease is not null)
            {
                operations.Add(Observe("same-handle-write", "lease-held", writerPath, () => WriteByte(writer, 0x42)));
                operations.Add(Observe("new-child-writer", "lease-held", newWriterPath, () => NewWriter(sandbox, newWriterPath, 0x43)));
                operations.Add(Observe("child-create-empty", "lease-held", created, () => CreateEmpty(sandbox, created)));
                operations.Add(Observe("existing-child-delete", "lease-held", deletePath, () =>
                    DeleteHeld(deletion)));
                operations.Add(Observe("new-child-delete", "lease-held", ordinaryDelete, () => DeleteSample(sandbox, ordinaryDelete)));
                operations.Add(Observe("nested-tree-move-in", "lease-held", incoming, () => MoveTree(sandbox, stagingRoot, incoming)));
                operations.Add(Observe("nested-tree-move-out", "lease-held", outgoingRoot, () => MoveTree(sandbox, outgoingRoot, exported)));
                cancellation = CancelAndRelease(lease);
            }
        }
        finally { lease?.Dispose(); }
        operations.Add(Observe("same-handle-write", "after-release", writerPath, () => WriteByte(writer, 0x44)));
        operations.Add(Observe("new-child-writer", "after-release", newWriterPath, () => NewWriter(sandbox, newWriterPath, 0x45)));
        if (!File.Exists(created)) operations.Add(Observe("child-create-empty", "after-release", created, () => CreateEmpty(sandbox, created)));
        if (File.Exists(deletePath)) operations.Add(Observe("existing-child-delete", "after-release", deletePath, () => DeleteHeld(deletion)));
        if (File.Exists(ordinaryDelete)) operations.Add(Observe("new-child-delete", "after-release", ordinaryDelete, () => DeleteSample(sandbox, ordinaryDelete)));
        if (Directory.Exists(stagingRoot)) operations.Add(Observe("nested-tree-move-in", "after-release", incoming, () => MoveTree(sandbox, stagingRoot, incoming)));
        if (Directory.Exists(outgoingRoot)) operations.Add(Observe("nested-tree-move-out", "after-release", outgoingRoot, () => MoveTree(sandbox, outgoingRoot, exported)));
        writer.Dispose(); deletion.Dispose();
        return new(lease is not null && Complete(operations) && Recovered(operations),
            operations.Any(o => o.Phase == "lease-held" && o.Succeeded),
            new { root, nested, stagingRoot, outgoingRoot, exported, recursiveExclusionClaimed = false, requestedLease = LeaseRequest(root, profile, ShareRead),
                writer = writer.Description(), deletion = deletion.Description(), lease = lease?.Description(), cancellation, operations });
    }

    private static ScenarioResult ExistingLeaf(ExperimentSandbox sandbox, LeaseProfile profile, string name, uint consumerAccess)
    {
        var root = Directory.CreateDirectory(Path.Combine(sandbox.RunDirectory, profile.Id, "existing-" + name, "candidate")).FullName;
        var path = Sample(sandbox, root, "sample.bin");
        var operations = new List<Operation>();
        using var consumer = new HeldHandle(sandbox, path, consumerAccess | ReadAttributes, ShareAll, "preexisting-leaf-" + name);
        if (consumerAccess == WriteAttributes)
            operations.Add(Observe("same-handle-time", "before-lease", path, () => SetTime(consumer, DateTime.UtcNow.AddMinutes(-4))));
        if (consumerAccess == WriteData)
            operations.Add(Observe("same-handle-write", "before-lease", path, () => WriteByte(consumer, 0x51)));
        HeldHandle? lease = null;
        try
        {
            operations.Add(Observe("acquire-with-existing-consumer", "acquire", path, () =>
            { lease = new(sandbox, path, profile.Access, ShareRead, "leaf-write-delete-sharing-diagnostic"); return lease.Description(); }));
            if (lease is not null && consumerAccess == WriteAttributes)
                operations.Add(Observe("same-handle-time", "lease-held", path, () => SetTime(consumer, DateTime.UtcNow.AddMinutes(-3))));
            if (lease is not null && consumerAccess == WriteData)
                operations.Add(Observe("same-handle-write", "lease-held", path, () => WriteByte(consumer, 0x52)));
        }
        finally { lease?.Dispose(); consumer.Dispose(); }
        HeldHandle? retry = null;
        try
        {
            operations.Add(Observe("acquire-after-consumer-close", "after-release", path, () =>
            { retry = new(sandbox, path, profile.Access, ShareRead, "same-leaf-after-existing-consumer-close"); return retry.Description(); }));
        }
        finally { retry?.Dispose(); }
        return new(Complete(operations) && Recovered(operations),
            operations.Any(o => o.Phase == "lease-held" && o.Succeeded),
            new { path, requestedLease = LeaseRequest(path, profile, ShareRead), consumer = consumer.Description(), lease = lease?.Description(), retry = retry?.Description(),
                existingDeleteHandleAction = consumerAccess == Delete ? "Access held across acquisition attempt; not marked for deletion in this scenario." : null,
                incompatibleExistingConsumerRejected = operations.Any(o => o.Name == "acquire-with-existing-consumer" && o.NativeError == 32), operations });
    }

    private static ScenarioResult LeafCancellation(ExperimentSandbox sandbox, LeaseProfile profile)
    {
        var root = Directory.CreateDirectory(Path.Combine(sandbox.RunDirectory, profile.Id, "cancel-release", "candidate")).FullName;
        var path = Sample(sandbox, root, "sample.bin");
        var operations = new List<Operation>
        {
            Observe("new-data-writer", "before-lease", path, () => NewWriter(sandbox, path, 0x61)),
            Observe("new-delete-access", "before-lease", path, () => OpenOnly(sandbox, path, Delete | ReadAttributes, "delete-consumer-no-mark"))
        };
        HeldHandle? lease = null;
        object? cancellation = null;
        try
        {
            operations.Add(Observe("acquire-leaf-lease", "acquire", path, () =>
            { lease = new(sandbox, path, profile.Access, ShareRead, "leaf-write-delete-sharing-diagnostic"); return lease.Description(); }));
            if (lease is not null)
            {
                operations.Add(Observe("new-data-writer", "lease-held", path, () => NewWriter(sandbox, path, 0x62)));
                operations.Add(Observe("new-delete-access", "lease-held", path, () => OpenOnly(sandbox, path, Delete | ReadAttributes, "delete-consumer-no-mark")));
                operations.Add(Observe("new-attribute-handle-time", "lease-held", path, () =>
                {
                    using var attributes = new HeldHandle(sandbox, path, WriteAttributes | ReadAttributes, ShareAll, "new-leaf-attribute-consumer");
                    var mutation = SetTime(attributes, DateTime.UtcNow.AddMinutes(-2)); attributes.Dispose();
                    return new { handle = attributes.Description(), mutation };
                }));
                cancellation = CancelAndRelease(lease);
            }
        }
        finally { lease?.Dispose(); }
        operations.Add(Observe("new-data-writer", "after-release", path, () => NewWriter(sandbox, path, 0x63)));
        operations.Add(Observe("new-delete-access", "after-release", path, () => OpenOnly(sandbox, path, Delete | ReadAttributes, "delete-consumer-no-mark")));
        operations.Add(Observe("fixture-delete-after-release", "after-release", path, () => DeleteSample(sandbox, path)));
        return new(lease is not null && Complete(operations) && Recovered(operations),
            operations.Any(o => o.Phase == "lease-held" && o.Succeeded),
            new { path, requestedLease = LeaseRequest(path, profile, ShareRead), lease = lease?.Description(), cancellation, operations,
                dataConsumerRejectedDuringLease = operations.Any(o => o.Phase == "lease-held" && o.Name == "new-data-writer" && o.NativeError == 32),
                deleteConsumerRejectedDuringLease = operations.Any(o => o.Phase == "lease-held" && o.Name == "new-delete-access" && o.NativeError == 32),
                consumersRecoveredAfterRelease = Recovered(operations),
                consumerInterferenceObserved = operations.Any(o => o.Phase == "lease-held" && o.NativeError == 32) });
    }

    private sealed record LeaseProfile(string Id, uint Access, string AccessDescription);
    private sealed record ScenarioResult(bool HarnessComplete, bool CounterexampleObserved, object Details);
    private sealed record Operation(string Name, string Phase, string Path, DateTimeOffset StartedUtc,
        DateTimeOffset FinishedUtc, string Outcome, int? NativeError, string? Error, object? Detail)
    {
        public bool Succeeded => Outcome == "succeeded";
    }

    private static Operation Observe(string name, string phase, string path, Func<object?> action)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            var detail = action();
            return new(name, phase, path, started, DateTimeOffset.UtcNow, "succeeded", null, null, detail);
        }
        catch (Exception error)
        {
            var code = error is Win32Exception native ? native.NativeErrorCode :
                error is IOException && ((uint)error.HResult & 0xffff0000u) == 0x80070000u ? error.HResult & 0xffff : (int?)null;
            return new(name, phase, path, started, DateTimeOffset.UtcNow, code == 32 ? "sharing-violation" : "failed", code, error.Message, null);
        }
    }

    private static bool Complete(IEnumerable<Operation> operations) => operations.All(o => o.Succeeded || o.NativeError == 32);
    private static bool Recovered(IEnumerable<Operation> operations) =>
        operations.Any(o => o.Phase == "after-release") && operations.Where(o => o.Phase is "before-lease" or "after-release").All(o => o.Succeeded);
    private static object ProfileDescription(LeaseProfile profile) => new { profile.Id, accessHex = $"0x{profile.Access:X8}", access = profile.AccessDescription };
    private static object LeaseRequest(string path, LeaseProfile profile, uint share) => new { path, profile = ProfileDescription(profile),
        accessHex = $"0x{profile.Access:X8}", shareHex = $"0x{share:X8}",
        access = profile.AccessDescription, share = share == 0 ? "none" : "FILE_SHARE_READ", fixtureObjectOnly = true };

    private sealed class HeldHandle : IDisposable
    {
        internal SafeFileHandle Handle { get; }
        internal string Path { get; }
        private readonly uint access, share;
        private readonly string role;
        private readonly DateTimeOffset openedUtc;
        private DateTimeOffset? closedUtc;
        internal HeldHandle(ExperimentSandbox sandbox, string path, uint access, uint share, string role)
        {
            Guard(sandbox, path);
            Path = path; this.access = access; this.share = share; this.role = role;
            Handle = WindowsFileMetadata.Open(path, access, share);
            openedUtc = DateTimeOffset.UtcNow;
            try
            {
                var metadata = WindowsFileMetadata.Read(Handle, path);
                if (metadata.Identity is null || metadata.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Fixture handle identity or final path could not be verified.");
            }
            catch { Handle.Dispose(); throw; }
        }
        internal object Description() => new { role, path = Path, accessHex = $"0x{access:X8}", shareHex = $"0x{share:X8}",
            openedUtc, closedUtc, released = Handle.IsClosed,
            flags = "OPEN_EXISTING|FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT; no recursive or ancestor write exclusion" };
        public void Dispose() { if (Handle.IsClosed) return; Handle.Dispose(); closedUtc = DateTimeOffset.UtcNow; }
    }

    private static object CancelAndRelease(HeldHandle lease)
    {
        using var cancellation = new CancellationTokenSource();
        var requestedUtc = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var observed = false;
        try { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { observed = true; }
        finally { lease.Dispose(); }
        return new { requestedUtc, cancellationObserved = observed, finallyReleased = lease.Handle.IsClosed,
            releaseMilliseconds = watch.Elapsed.TotalMilliseconds, controlledSynchronousCancellationOnly = true };
    }

    private static string Sample(ExperimentSandbox sandbox, string root, string name)
    {
        var path = Path.Combine(root, name); Guard(sandbox, path);
        var source = sandbox.CreateFixture("lease-" + Guid.NewGuid().ToString("N") + ".bin", SampleBytes);
        File.Move(source, path); return path;
    }
    private static void Guard(ExperimentSandbox sandbox, string path)
    {
        var full = ExperimentSandbox.Normalize(path);
        if (!full.StartsWith(sandbox.RunDirectory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ExperimentRefusedException("Lease operations must stay strictly inside this new run.", 3);
    }
    private static object SetTime(HeldHandle consumer, DateTime value)
    {
        var requested = value.ToFileTimeUtc();
        if (!SetFileTime(consumer.Handle, IntPtr.Zero, IntPtr.Zero, ref requested)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var actual = WindowsFileMetadata.Read(consumer.Handle, consumer.Path).ModifiedTime;
        if (actual != requested) throw new IOException("SetFileTime returned success but the observed fixture time differs.");
        return new { requestedFileTime = requested.ToString(CultureInfo.InvariantCulture), actualFileTime = actual.ToString(CultureInfo.InvariantCulture) };
    }
    private static object SetAttributes(string path, FileAttributes attributes)
    {
        File.SetAttributes(path, attributes);
        var actual = File.GetAttributes(path);
        if ((actual & FileAttributes.Hidden) != (attributes & FileAttributes.Hidden)) throw new IOException("Fixture attribute verification failed.");
        return new { requested = attributes.ToString(), actual = actual.ToString(), api = "File.SetAttributes; this managed call's native access mask is not instrumented" };
    }
    private static object WriteByte(HeldHandle consumer, byte value)
    {
        RandomAccess.Write(consumer.Handle, new[] { value }, 0);
        using var reader = WindowsFileMetadata.Open(consumer.Path, 1 | ReadAttributes, ShareAll);
        var actual = new byte[1];
        if (RandomAccess.Read(reader, actual, 0) != 1 || actual[0] != value || RandomAccess.GetLength(reader) != SampleBytes)
            throw new IOException("Same-length fixture write verification failed.");
        return new { position = 0, requestedByte = value, actualByte = actual[0], length = SampleBytes, flushedToDisk = false };
    }
    private static object NewWriter(ExperimentSandbox sandbox, string path, byte value)
    {
        using var writer = new HeldHandle(sandbox, path, WriteData | ReadAttributes, ShareAll, "new-data-consumer");
        var mutation = WriteByte(writer, value); writer.Dispose();
        return new { handle = writer.Description(), mutation };
    }
    private static object OpenOnly(ExperimentSandbox sandbox, string path, uint access, string role)
    {
        using var consumer = new HeldHandle(sandbox, path, access, ShareAll, role);
        consumer.Dispose(); var description = consumer.Description();
        return new { requestedAccessHex = $"0x{access:X8}", requestedShareHex = "0x00000007", opened = true, noDeleteMark = true, description, released = consumer.Handle.IsClosed };
    }
    private static object CreateEmpty(ExperimentSandbox sandbox, string path)
    {
        Guard(sandbox, path);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
        if (!File.Exists(path) || new FileInfo(path).Length != 0) throw new IOException("Empty fixture creation verification failed.");
        return new { created = true, logicalBytes = 0, api = "FileStream CreateNew; no file content allocated" };
    }
    private static object DeleteSample(ExperimentSandbox sandbox, string path)
    {
        Guard(sandbox, path);
        if (!File.Exists(path)) throw new IOException("Expected generated deletion fixture is missing before the action.");
        File.Delete(path);
        if (File.Exists(path)) throw new IOException("Generated deletion fixture is still present.");
        return new { existedBefore = true, absentAfter = true, api = "File.Delete of this run's generated fixture only; not production authorization" };
    }
    private static object MoveTree(ExperimentSandbox sandbox, string source, string destination)
    {
        Guard(sandbox, source); Guard(sandbox, destination);
        Directory.Move(source, destination);
        if (Directory.Exists(source) || !File.Exists(Path.Combine(destination, "a", "b", "member.bin")))
            throw new IOException("Nested fixture move verification failed.");
        return new { source, destination, sourceAbsent = true, nestedMemberPresent = true, subtreeMemberNotificationsProven = false };
    }
    private static void MarkDelete(SafeFileHandle handle)
    {
        if (!SetFileInformationByHandle(handle, 4, [1], 1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    private static object DeleteHeld(HeldHandle consumer)
    {
        MarkDelete(consumer.Handle); consumer.Dispose();
        if (File.Exists(consumer.Path)) throw new IOException("Delete marked but the fixture is still present after handle close.");
        return new { deleteMarked = true, absentAfterClose = true, consumer = consumer.Description() };
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileTime(SafeFileHandle handle, IntPtr creation, IntPtr access, ref long write);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, byte[] info, uint size);
}
