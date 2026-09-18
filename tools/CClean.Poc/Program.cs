using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CClean.Core.Experiments;

var started = DateTimeOffset.UtcNow;
var runId = $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
var checks = new List<object>();
var exitCode = 0;
string? reportDirectory = null;
string? caseName = null;
try
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    string[] known = ["--case", "--sandbox", "--output", "--max-fixture-bytes", "--reserve-free-bytes", "--file-count", "--logical-bytes", "--quota-root", "--sample-root", "--checkpoint"];
    if (args.Length == 0 || args.Length % 2 != 0) throw new ExperimentRefusedException("Expected --name value pairs.", 2);
    for (var i = 0; i < args.Length; i += 2)
        if (!known.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1]))
            throw new ExperimentRefusedException("Unknown or duplicate option.", 2);
    string Required(string key) => options.TryGetValue(key, out var value) ? value :
        throw new ExperimentRefusedException($"Missing {key}.", 2);
    long Number(string key, long fallback) => !options.TryGetValue(key, out var value) ? fallback :
        long.TryParse(value, out var number) && number >= 0 ? number :
            throw new ExperimentRefusedException($"Invalid {key}.", 2);
    caseName = Required("--case");
    var output = ExperimentSandbox.Normalize(Required("--output"));
    var artifactRoot = Path.GetFullPath("artifacts");
    if (!output.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new ExperimentRefusedException("Reports must stay under workspace artifacts.", 3);
    using (var reportLease = new DirectoryLease(Path.GetDirectoryName(output)!, createMissing: true))
    {
        reportDirectory = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "-" + runId);
        Directory.CreateDirectory(reportDirectory);
    }
    if (caseName is not ("environment" or "metadata" or "watcher" or "watcher-overflow" or "deletion-safety" or "cache-rule" or "cache-product" or "cleanup-crashes" or "cleanup-crash-child" or "product-quota" or "product-audit-full" or "storage-budget" or "index-scale" or "index-rescan" or "benchmark" or "timeline-fixture")) throw new ExperimentRefusedException($"Case not implemented: {caseName}.", 2);
    var budget = Number("--max-fixture-bytes", ExperimentSandbox.DefaultBudget);
    var reserve = Number("--reserve-free-bytes", ExperimentSandbox.DefaultReserve);
    var count = Number("--file-count", 1);
    var logical = Number("--logical-bytes", 4096);
    if (count <= 0 || count > 100_000 || logical < count || logical > budget)
        throw new ExperimentRefusedException("Invalid or over-budget sample size.", 2);
    var sandboxPath = ExperimentSandbox.Normalize(Required("--sandbox"));
    var insideArtifacts = sandboxPath.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    if (!insideArtifacts)
    {
        var ancestor = Directory.GetParent(sandboxPath);
        while (ancestor is not null && !File.Exists(Path.Combine(ancestor.FullName, ".cclean-plan"))) ancestor = ancestor.Parent;
        if (ancestor is null || File.ReadAllText(Path.Combine(ancestor.FullName, ".cclean-plan")).Trim() != ExperimentSandbox.PlanId)
            throw new ExperimentRefusedException("Sandbox requires artifacts or an explicitly owned lab ancestor.", 3);
    }
    using var sandbox = new ExperimentSandbox(sandboxPath, budget, reserve);
    if (caseName == "cleanup-crash-child") CClean.Poc.RecoveryProbe.Child(sandbox, Required("--sample-root"), Required("--checkpoint"));
    else if (caseName == "cleanup-crashes") CClean.Poc.RecoveryProbe.Run(sandbox, checks);
    else if (caseName is "product-quota" or "product-audit-full") CClean.Poc.ProductQuotaProbe.Run(sandbox, Required("--quota-root"), checks, caseName == "product-audit-full");
    else if (caseName == "timeline-fixture")
    {
        CClean.Poc.TimelineProbe.Run(sandbox, checks);
    }
    else if (caseName is "index-scale" or "index-rescan" or "benchmark")
    {
        if (caseName == "benchmark" && (count != 100000 || logical != 21474836480 || budget < 25769803776 ||
            Path.GetPathRoot(sandboxPath)!.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase)))
            throw new ExperimentRefusedException("Benchmark requires 100000 files / 20 GiB / explicit 24 GiB budget on a non-system NTFS lab.", 2);
        CClean.Poc.IndexProbe.Run(sandbox, (int)count, logical, checks, caseName == "index-rescan" ? Required("--sample-root") : null);
    }
    else if (caseName == "metadata")
    {
        CClean.Poc.MetadataProbe.Run(sandbox, checks);
    }
    else if (caseName is "watcher" or "watcher-overflow")
    {
        CClean.Poc.MonitoringProbe.Run(sandbox, caseName == "watcher-overflow", checks);
    }
    else if (caseName == "deletion-safety")
    {
        CClean.Poc.DeletionProbe.Run(sandbox, checks);
    }
    else if (caseName is "cache-rule" or "cache-product")
    {
        CClean.Poc.CacheProbe.Run(sandbox, checks, caseName == "cache-product");
    }
    else if (caseName == "storage-budget")
    {
        CClean.Poc.StorageProbe.Run(sandbox, options.GetValueOrDefault("--quota-root"), checks);
    }
    else
    {
    for (long i = 0; i < count; i++)
        sandbox.CreateFixture($"fixture-{i:D6}.bin", logical / count + (i < logical % count ? 1 : 0));
    var files = Directory.GetFiles(sandbox.RunDirectory);
    var actual = files.Sum(p => new FileInfo(p).Length);
    if (files.LongLength != count || actual != logical) throw new InvalidOperationException("Fixture verification mismatch.");
    checks.Add(new { name = "environment", status = "passed", fileCount = count, logicalBytes = actual,
        fixtureBudget = budget, reserveFreeBytes = reserve, volumeFormat = new DriveInfo(Path.GetPathRoot(sandboxPath)!).DriveFormat });
    }
}
catch (Exception error)
{
    exitCode = error is ExperimentRefusedException refused ? refused.ExitCode : 1;
    checks.Add(new { name = caseName ?? "arguments", status = "failed", error = error.Message });
    Console.Error.WriteLine(error.Message);
}
var process = Process.GetCurrentProcess();
string Command(string executable, string argument)
{
    try
    {
        using var child = Process.Start(new ProcessStartInfo(executable, argument) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
        if (child is null) return "unavailable";
        var value = child.StandardOutput.ReadToEnd().Trim();
        child.WaitForExit();
        return child.ExitCode == 0 ? value : "unavailable";
    }
    catch { return "unavailable"; }
}
var report = new { runId, caseName, started, ended = DateTimeOffset.UtcNow,
    sdk = Command("dotnet", "--version"), os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(), commit = Command("git", "rev-parse HEAD"),
    workingTreeDirty = Command("git", "status --porcelain").Length > 0,
    passed = checks.Count(x => JsonSerializer.SerializeToElement(x).GetProperty("status").GetString() == "passed"),
    failed = exitCode == 0 ? 0 : 1, skipped = 0,
    peakWorkingSetBytes = process.PeakWorkingSet64, cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
    checks, exitCode };
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
try
{
    if (reportDirectory is not null)
    {
        using var outputLease = new DirectoryLease(reportDirectory);
        using var stream = new FileStream(Path.Combine(reportDirectory, "result.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(json);
        Console.WriteLine(Path.Combine(reportDirectory, "result.json"));
    }
    else Console.WriteLine(json);
}
catch (Exception error) { Console.Error.WriteLine($"Report write failed: {error.Message}"); exitCode = 1; }
return exitCode;
