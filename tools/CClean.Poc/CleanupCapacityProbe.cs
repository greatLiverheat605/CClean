using System.Diagnostics;
using System.Text;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Files;
using CClean.Core.Sessions;

namespace CClean.Poc;

// Encoding research only: synthetic paths/identities never enter an executor or journal.
internal static class CleanupCapacityProbe
{
    internal static void Run(ExperimentSandbox sandbox, List<object> checks)
    {
        var root = Path.Combine(sandbox.RunDirectory, "synthetic-only", "_cacache", "content-v2");
        var scope = new RuleScope(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!, CacheRule.NpmContent.Id, 1, "11.19.0", true, true);
        var context = new CleanupContext(1, [scope], new ActivitySnapshot(1, []), []) { Contract = CleanupContract.ObjectReviewV1 };
        var time = DateTime.UtcNow.AddDays(-3).ToFileTimeUtc();
        foreach (var count in new[] { 1000, 1001, 10000, 100000 })
        {
            var items = Enumerable.Range(0, count).Select(i => new CleanupItem(
                new FileSnapshot(Path.Combine(root, $"synthetic-{i:D6}.bin"), $"{i:X48}:{time}", 16, 4096, 1,
                    FileAttributes.Archive, time, time, true, false, null), new(true, "研究合成项", "非真实文件，不能执行", scope), "synthetic-root")).ToList();
            var plan = new CleanupPlan(DateTimeOffset.UtcNow, context.Fingerprint(), items, false, context.Contract);
            var timer = Stopwatch.StartNew();
            int? bytes = null; string? refusal = null; var decoded = 0;
            try
            {
                var encoded = CleanupManifestCodec.Encode(plan);
                bytes = Encoding.UTF8.GetByteCount(encoded);
                var restored = CleanupManifestCodec.Decode(encoded, plan.Id.ToString(), CancellationToken.None);
                decoded = restored.Count;
                if (restored.Count != count || restored[^1] != items[^1]) throw new InvalidOperationException("Manifest roundtrip mismatch.");
            }
            catch (IOException error) when (error.Message.Contains("8 MiB")) { refusal = error.Message; }
            checks.Add(new
            {
                name = "synthetic-encoding-capacity", status = "measured", count, bytes, refusal, decoded,
                encodingAndDecodeMs = timer.ElapsedMilliseconds,
                worstCaseRemainingReadsIfEveryDeleteNotifies = (long)count * (count - 1) / 2,
                current1000ItemConfirmationLowerBound = (count + 999) / 1000,
                productionPlannerWouldTruncate = count > 1000,
                syntheticMetadataOnly = true, executableApproval = false, executionAttempted = false,
                processPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                limitation = "Encoding-only synthetic metadata; not a real-object scan, throughput, resident-memory acceptance or permission to raise the production cap."
            });
        }
    }
}
