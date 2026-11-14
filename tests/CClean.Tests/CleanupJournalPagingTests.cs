using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Files;
using Microsoft.Data.Sqlite;

namespace CClean.Tests;

[Trait("Category", "CleanupRecovery")]
[Trait("Category", "MvpP0")]
public class CleanupJournalPagingTests
{
    [Fact]
    public void ObjectContractManifestIsReadableAndUnknownContractsAreRejected()
    {
        using var fixture = new Fixture();
        var plan = new CleanupPlan(DateTimeOffset.UtcNow, "synthetic-context", [fixture.Item(0)], false, CleanupContract.ObjectReviewV1);
        fixture.Journal.Begin(plan);
        var compact = fixture.ReadManifest(plan.Id.ToString());
        var json = JsonNode.Parse(compact)!;
        Assert.Equal(2, json["Version"]!.GetValue<int>());
        Assert.Equal("ObjectReviewV1", json["Contract"]!.GetValue<string>());
        Assert.Equal(plan.Items, fixture.Journal.Manifest(plan.Id.ToString()));
        // The first isolated experiment used v1 with an explicit contract; retain its audit readability.
        var experimental = ChangedCompact(fixture, compact, j => j["Version"] = 1);
        Assert.Equal(plan.Items, fixture.Journal.Manifest(experimental));
        foreach (var invalid in new[]
        {
            ChangedCompact(fixture, compact, j => j.Remove("Contract")),
            ChangedCompact(fixture, compact, j => j["Contract"] = "future-contract"),
            ChangedCompact(fixture, compact, j => j["Version"] = 3)
        }) Assert.Throws<IOException>(() => fixture.Journal.Manifest(invalid));
    }

    [Fact]
    public void InProcessManifestCacheRejectsChangedPersistentText()
    {
        using var fixture = new Fixture();
        var plan = new CleanupPlan(DateTimeOffset.UtcNow, "synthetic-context", [fixture.Item(0)], false);
        fixture.Journal.Begin(plan);
        var run = plan.Id.ToString();
        Assert.Equal(plan.Items, fixture.Journal.Manifest(run));
        var changed = JsonNode.Parse(fixture.ReadManifest(run))!.AsObject();
        changed["Format"] = "unknown-manifest";
        fixture.UpdateManifest(run, changed.ToJsonString());
        Assert.Throws<IOException>(() => fixture.Journal.Manifest(run));
        Assert.Throws<IOException>(() => CleanupRecovery.InspectPage(fixture.Journal, run));
    }

    [Fact]
    public void CachedManifestPagesDoNotReallocateTheWholeManifest()
    {
        using var fixture = new Fixture();
        var plan = new CleanupPlan(DateTimeOffset.UtcNow, "synthetic-context",
            Enumerable.Range(0, 10000).Select(i => fixture.Item(i)).ToList(), false);
        fixture.Journal.Begin(plan);
        var run = plan.Id.ToString();
        var after = -1; var count = 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        while (true)
        {
            var page = fixture.Journal.ManifestPage(run, after, 1000);
            count += page.Items.Count;
            if (!page.HasMore) break;
            after = page.NextOrdinal;
        }
        Assert.Equal(10000, count);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 20L * 1024 * 1024);
    }

    [Fact]
    public void LegacyAndCompactManifestsPreserveApprovedObjectsAndDecisions()
    {
        using var fixture = new Fixture();
        var items = new[] { fixture.Item(0), fixture.Item(1, false) };
        var legacy = fixture.InsertRun(JsonSerializer.Serialize(new { Items = items }));
        Assert.Equal(items, fixture.Journal.Manifest(legacy).ToArray());
        var legacyPlan = new CleanupPlan(DateTimeOffset.UtcNow, "synthetic-context", items.ToList(), false);
        var fullLegacy = fixture.InsertRun(JsonSerializer.Serialize(legacyPlan), legacyPlan.Id.ToString());
        Assert.Equal(items, fixture.Journal.Manifest(fullLegacy).ToArray());

        var plan = new CleanupPlan(DateTimeOffset.UtcNow, "synthetic-context", items.ToList(), false);
        fixture.Journal.Begin(plan);
        var compact = fixture.ReadManifest(plan.Id.ToString());
        using var document = JsonDocument.Parse(compact);
        Assert.Equal("cclean-cleanup-manifest", document.RootElement.GetProperty("Format").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(items, fixture.Journal.Manifest(plan.Id.ToString()).ToArray());

        foreach (var run in new[] { legacy, fullLegacy, plan.Id.ToString() })
        {
            var first = fixture.Journal.ManifestPage(run, -1, 1);
            Assert.True(first.RunFound); Assert.True(first.HasMore); Assert.Equal(2, first.TotalItems);
            Assert.Equal(items[0], Assert.Single(first.Items));
            var last = fixture.Journal.ManifestPage(run, first.NextOrdinal, 1);
            Assert.True(last.RunFound); Assert.False(last.HasMore);
            Assert.Equal(items[1], Assert.Single(last.Items));
        }
    }

    [Fact]
    public void RecoveryFindsTailCandidateAndCommittedResultBeyondOldReadLimits()
    {
        using var fixture = new Fixture();
        var items = Enumerable.Range(0, 1002).Select(i => fixture.Item(i, i == 1001)).ToArray();
        var tail = items[^1];
        var run = fixture.InsertRun(JsonSerializer.Serialize(new { Items = items }));
        fixture.InsertEvents(Enumerable.Range(0, 3001).Select(_ => (run, tail.Path, "Started"))
            .Append((run, tail.Path, "Deleted")));

        Assert.Equal(1002, fixture.Journal.Manifest(run).Count);
        Assert.Equal(3002, fixture.Journal.Events(run).Count);
        var complete = Assert.Single(CleanupRecovery.Inspect(fixture.Journal, run));
        Assert.Equal(tail.Path, complete.Path); Assert.Equal("Deleted", complete.State);

        var observed = new List<CleanupEvent>();
        var after = -1; var checkedItems = 0; long? through = null;
        while (true)
        {
            var page = CleanupRecovery.InspectPage(fixture.Journal, run, after, 200, through);
            Assert.True(page.RunFound); Assert.Equal(1002, page.TotalItems);
            Assert.Equal(!page.HasMore, page.IsComplete);
            if (through is not null) Assert.Equal(through.Value, page.ThroughSequence);
            else fixture.InsertEvents([(run, tail.Path, "Started")]);
            through = page.ThroughSequence; checkedItems += page.CheckedItems; observed.AddRange(page.Items);
            if (!page.HasMore) break;
            Assert.True(page.NextOrdinal > after); after = page.NextOrdinal;
        }
        Assert.Equal(1002, checkedItems);
        Assert.Equal(complete, Assert.Single(observed));
    }

    [Fact]
    public void EventKeysetStaysWithinRunAndCapturedHighWaterUntilRefreshed()
    {
        using var fixture = new Fixture();
        var run = fixture.InsertRun("{\"Items\":[]}");
        var other = fixture.InsertRun("{\"Items\":[]}");
        var sequences = fixture.InsertEvents(Enumerable.Range(0, 5).SelectMany(i => new[]
        {
            (run, fixture.Item(i).Path, "Started"),
            (other, fixture.Item(i + 10).Path, "Skipped")
        }));
        var expected = sequences.Where((_, i) => i % 2 == 0).ToArray();
        var first = fixture.Journal.EventsPage(run, 0, 2);
        Assert.True(first.RunFound); Assert.True(first.HasMore); Assert.False(first.IsComplete);
        Assert.Equal(expected[^1], first.ThroughSequence);

        var latePath = fixture.Item(99).Path;
        var late = fixture.InsertEvents([(run, latePath, "Deleted"), (other, fixture.Item(100).Path, "Deleted")])[0];
        var observed = new List<CleanupEvent>();
        var page = first; long after = 0;
        while (true)
        {
            Assert.Equal(first.ThroughSequence, page.ThroughSequence);
            Assert.Equal(!page.HasMore, page.IsComplete);
            observed.AddRange(page.Items);
            if (!page.HasMore) break;
            Assert.True(page.NextSequence > after); after = page.NextSequence;
            page = fixture.Journal.EventsPage(run, after, 2, first.ThroughSequence);
        }
        Assert.Equal(expected, observed.Select(e => e.Sequence).ToArray());
        Assert.DoesNotContain(observed, e => e.Path == latePath);
        var end = fixture.Journal.EventsPage(run, first.ThroughSequence, 2, first.ThroughSequence);
        Assert.Empty(end.Items); Assert.False(end.HasMore); Assert.True(end.IsComplete);
        var refreshed = fixture.Journal.EventsPage(run);
        Assert.Equal(late, refreshed.ThroughSequence);
        Assert.Contains(refreshed.Items, e => e.Path == latePath && e.Sequence == late);

        var missing = Guid.NewGuid().ToString();
        var absentEvents = fixture.Journal.EventsPage(missing);
        Assert.False(absentEvents.RunFound); Assert.Empty(absentEvents.Items);
        var absentManifest = fixture.Journal.ManifestPage(missing);
        Assert.False(absentManifest.RunFound); Assert.Empty(absentManifest.Items);
        var absentRecovery = CleanupRecovery.InspectPage(fixture.Journal, missing);
        Assert.False(absentRecovery.RunFound); Assert.Empty(absentRecovery.Items);
    }

    [Fact]
    public void RunKeysetReadsEveryRunWithTiedCreationTimesExactlyOnce()
    {
        using var fixture = new Fixture();
        var ids = Enumerable.Range(0, 205).Select(i => $"00000000-0000-0000-0000-{i:D12}").ToArray();
        foreach (var id in ids) fixture.InsertRun("{\"Items\":[]}", id);
        var first = fixture.Journal.RunsPage();
        Assert.Equal(200, first.Items.Count); Assert.True(first.HasMore); Assert.NotNull(first.NextCursor);
        var last = fixture.Journal.RunsPage(first.NextCursor);
        Assert.Equal(5, last.Items.Count); Assert.False(last.HasMore);
        var actual = first.Items.Concat(last.Items).Select(r => r.Id).ToArray();
        Assert.Equal(ids.OrderDescending(StringComparer.Ordinal).ToArray(), actual);
        Assert.Equal(ids.Length, actual.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void InvalidCursorsAndDamagedOrUnknownManifestsFailClosed()
    {
        using var fixture = new Fixture();
        var plan = new CleanupPlan(DateTimeOffset.UtcNow, "synthetic-context", [fixture.Item(0), fixture.Item(1, false)], false);
        fixture.Journal.Begin(plan);
        var run = plan.Id.ToString(); var compact = fixture.ReadManifest(run);
        var other = fixture.InsertRun("{\"Items\":[]}");
        var sequences = fixture.InsertEvents([(run, fixture.Item(0).Path, "Started"),
            (other, fixture.Item(2).Path, "Started"), (run, fixture.Item(0).Path, "Deleted")]);
        Assert.ThrowsAny<ArgumentException>(() => fixture.Journal.RunsPage(new CleanupRunCursor("not-a-time", run)));
        Assert.ThrowsAny<ArgumentException>(() => fixture.Journal.ManifestPage(run, -2));
        Assert.ThrowsAny<ArgumentException>(() => fixture.Journal.ManifestPage(run, 2));
        Assert.ThrowsAny<ArgumentException>(() => fixture.Journal.EventsPage(run, -1));
        Assert.ThrowsAny<ArgumentException>(() => fixture.Journal.EventsPage(run, sequences[0]));
        Assert.ThrowsAny<ArgumentException>(() => fixture.Journal.EventsPage(run, 0, 200, sequences[^1] + 1));
        Assert.ThrowsAny<ArgumentException>(() => fixture.Journal.EventsPage(run, 0, 200, sequences[1]));
        Assert.ThrowsAny<ArgumentException>(() => fixture.Journal.EventsPage(run, sequences[1], 200, sequences[^1]));
        Assert.ThrowsAny<ArgumentException>(() => fixture.Journal.EventsPage(run, sequences[^1], 200, sequences[0]));
        Assert.ThrowsAny<ArgumentException>(() => CleanupRecovery.InspectPage(fixture.Journal, run, 0));

        var invalid = new[]
        {
            fixture.InsertRun("{\"Items\":["),
            fixture.InsertRun("{\"Unexpected\":true}"),
            fixture.InsertRun(JsonSerializer.Serialize(new { Items = new[] { new { File = plan.Items[0].File, RootIdentity = plan.Items[0].RootIdentity } } })),
            ChangedCompact(fixture, compact, json => json["Format"] = "unknown-manifest"),
            ChangedCompact(fixture, compact, json => json["Version"] = 2),
            ChangedCompact(fixture, compact, json => json["Items"]![0]![11] = 999)
        };
        foreach (var invalidRun in invalid)
        {
            Assert.Throws<IOException>(() => fixture.Journal.ManifestPage(invalidRun));
            Assert.Throws<IOException>(() => fixture.Journal.Manifest(invalidRun));
            Assert.Throws<IOException>(() => CleanupRecovery.InspectPage(fixture.Journal, invalidRun));
        }
    }

    [Fact]
    public void ManifestReadLimitUsesUtf8BytesAndEveryPagedReaderHonorsCancellation()
    {
        using var fixture = new Fixture();
        var json = "{\"Items\":[],\"ContextFingerprint\":\"" + new string('中', 3_000_000) + "\"}";
        Assert.True(json.Length < 8 * 1024 * 1024);
        Assert.True(Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024);
        var oversized = fixture.InsertRun(json);
        Assert.Throws<IOException>(() => fixture.Journal.ManifestPage(oversized));
        Assert.Throws<IOException>(() => fixture.Journal.Manifest(oversized));
        Assert.Throws<IOException>(() => CleanupRecovery.InspectPage(fixture.Journal, oversized));

        var item = fixture.Item(0);
        var oversizedPlan = new CleanupPlan(DateTimeOffset.UtcNow, "synthetic-context",
            [item with { Decision = item.Decision with { Reason = new string('x', 8 * 1024 * 1024) } }], false);
        Assert.Throws<IOException>(() => fixture.Journal.Begin(oversizedPlan));
        Assert.DoesNotContain(fixture.Journal.Runs(), saved => saved.Id == oversizedPlan.Id.ToString());

        var run = fixture.InsertRun("{\"Items\":[]}");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Journal.RunsPage(null, 200, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Journal.EventsPage(run, 0, 200, null, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Journal.ManifestPage(run, -1, 200, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => CleanupRecovery.InspectPage(fixture.Journal, run, -1, 200, null, cancellation.Token));
    }

    private static string ChangedCompact(Fixture fixture, string manifest, Action<JsonObject> change)
    {
        var json = JsonNode.Parse(manifest)!.AsObject(); var id = Guid.NewGuid().ToString();
        json["Id"] = id; change(json);
        return fixture.InsertRun(json.ToJsonString(), id);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ExperimentSandbox sandbox = new(Path.Combine(Path.GetTempPath(), "cclean-journal-paging-" + Guid.NewGuid().ToString("N")));
        private readonly SqliteConnection connection;
        public CleanupJournal Journal { get; }
        private const string Created = "2026-10-24T00:00:00.0000000+00:00";

        public Fixture()
        {
            var data = Path.Combine(sandbox.RunDirectory, "audit");
            Journal = new(data);
            connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "cleanup.db"), Pooling = false }.ToString());
            connection.Open();
        }

        public CleanupItem Item(int ordinal, bool eligible = true)
        {
            var root = sandbox.RunDirectory;
            var path = Path.Combine(root, $"隔离样本-{ordinal:D4}.bin");
            var time = DateTime.UtcNow.AddHours(-1).ToFileTimeUtc();
            var file = new FileSnapshot(path, $"synthetic-identity-{ordinal}", 32, 4096, 1, FileAttributes.Normal, time, time, true, false, null);
            var scope = new RuleScope(root, CacheRule.NpmContent.Id, 1, "11.19.0", true, true);
            return new(file, new(eligible, eligible ? "候选" : "保护", "synthetic fixture; no deletion authorization",
                eligible ? scope : null), eligible ? "synthetic-root-identity" : null);
        }

        public string InsertRun(string manifest, string? id = null)
        {
            id ??= Guid.NewGuid().ToString();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO runs(id,created,state,manifest,summary) VALUES($id,$created,'Failed',$manifest,'synthetic paging fixture')";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$created", Created);
            command.Parameters.AddWithValue("$manifest", manifest); command.ExecuteNonQuery();
            return id;
        }

        public string ReadManifest(string run)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT manifest FROM runs WHERE id=$run";
            command.Parameters.AddWithValue("$run", run);
            return (string)command.ExecuteScalar()!;
        }

        public void UpdateManifest(string run, string manifest)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET manifest=$manifest WHERE id=$run";
            command.Parameters.AddWithValue("$run", run);
            command.Parameters.AddWithValue("$manifest", manifest);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public long[] InsertEvents(IEnumerable<(string Run, string Path, string State)> events)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO events(run,path,state,reason,time) VALUES($run,$path,$state,'synthetic event',$time); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$run", ""); command.Parameters.AddWithValue("$path", "");
            command.Parameters.AddWithValue("$state", ""); command.Parameters.AddWithValue("$time", Created);
            var sequences = new List<long>();
            foreach (var item in events)
            {
                command.Parameters["$run"].Value = item.Run; command.Parameters["$path"].Value = item.Path;
                command.Parameters["$state"].Value = item.State;
                sequences.Add((long)command.ExecuteScalar()!);
            }
            transaction.Commit(); return sequences.ToArray();
        }

        public void Dispose() { connection.Dispose(); Journal.Dispose(); sandbox.Dispose(); }
    }
}
