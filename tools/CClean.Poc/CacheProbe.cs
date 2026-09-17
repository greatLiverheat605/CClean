using System.Diagnostics;
using System.Security.Cryptography;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Files;

namespace CClean.Poc;

internal static class CacheProbe
{
    public static void Run(ExperimentSandbox box, List<object> checks)
    {
        var node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        var npm = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(node) || !File.Exists(npm)) throw new ExperimentRefusedException("Verified node/npm installation missing.", 2);
        var cache = Path.Combine(box.RunDirectory, "exclusive-npm-cache");
        var userConfig = box.CreateFixture("empty-user.npmrc", 0);
        var globalConfig = box.CreateFixture("empty-global.npmrc", 0);
        string Npm(params string[] arguments)
        {
            var info = new ProcessStartInfo(node) { WorkingDirectory = box.RunDirectory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("npm_config_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
            info.ArgumentList.Add(npm);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            foreach (var argument in new[] { "--cache", cache, "--userconfig", userConfig, "--globalconfig", globalConfig,
                         "--registry", "https://registry.npmjs.org", "--ignore-scripts", "--no-audit", "--no-fund", "--loglevel", "error" }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90_000)) { process.Kill(entireProcessTree: true); throw new IOException("Owned npm probe timed out."); }
            if (process.ExitCode != 0) throw new IOException($"npm failed ({process.ExitCode}): {stderr.GetAwaiter().GetResult()}");
            var actual = Directory.GetFiles(box.RunDirectory, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
            if (actual > box.Budget) throw new ExperimentRefusedException("External tool exceeded fixture budget.", 2);
            return stdout.GetAwaiter().GetResult().Trim();
        }
        var version = Npm("--version");
        if (version != CacheRule.NpmContent.SupportedVersion) throw new ExperimentRefusedException("npm version is not validated.", 2);
        Npm("cache", "add", "is-number@7.0.0");
        var content = Path.Combine(cache, CacheRule.NpmContent.RelativeRoot);
        var approved = Directory.GetFiles(content, "*", SearchOption.AllDirectories).Select(WindowsFileMetadata.Read).ToArray();
        if (approved.Length == 0) throw new InvalidOperationException("Real npm did not generate content.");
        // This unique cache is owned by the completed probe process; it has never been configured for another consumer.
        var context = new CacheContext(cache, version, true, true, true, false, []);
        var hashes = approved.Where(f => f.Path.Contains(Path.DirectorySeparatorChar + "content-v2" + Path.DirectorySeparatorChar))
            .ToDictionary(f => f.Path, f => SHA256.HashData(File.ReadAllBytes(f.Path)));
        var newFile = Path.Combine(content, "new-unapproved.bin");
        File.WriteAllText(newFile, "must survive preview", System.Text.Encoding.UTF8);
        foreach (var file in approved)
        {
            var excluded = CacheRule.NpmContent.Exclude(context, file);
            if (excluded is not null) throw new InvalidOperationException(excluded);
            var deleted = VerifiedFileDeletion.DeleteFixture(box, file);
            if (!deleted.DeleteMarked || File.Exists(file.Path)) throw new InvalidOperationException(deleted.Reason);
        }
        if (File.ReadAllText(newFile) != "must survive preview") throw new InvalidOperationException("Unapproved object lost.");
        Npm("cache", "add", "is-number@7.0.0");
        foreach (var entry in hashes)
            if (!File.Exists(entry.Key) || !SHA256.HashData(File.ReadAllBytes(entry.Key)).SequenceEqual(entry.Value))
                throw new InvalidOperationException("Real cache did not rebuild identically.");
        Npm("install", "--prefix", Path.Combine(box.RunDirectory, "consumer"), "--no-save", "--package-lock=false", "is-number@7.0.0");
        if (!File.Exists(Path.Combine(box.RunDirectory, "consumer", "node_modules", "is-number", "index.js")))
            throw new InvalidOperationException("Real npm consumer install failed.");
        checks.Add(new { name = "external-exclusive-cache", status = "passed", tool = "npm", version,
            generatedDeletedAndRebuilt = approved.Length, newObjectsPreserved = 1, realConsumerInstall = true,
            prerequisite = CacheRule.NpmContent.Prerequisite });
    }
}
