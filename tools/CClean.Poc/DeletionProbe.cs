using System.Security.Cryptography;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Files;

namespace CClean.Poc;

internal static class DeletionProbe
{
    public static void Run(ExperimentSandbox box, List<object> checks)
    {
        const int rounds = 30;
        var protectedFiles = new Dictionary<string, byte[]>();
        var deleted = 0;
        for (var round = 0; round < rounds; round++)
        {
            var protect = box.CreateFixture($"protected-{round}.bin", 128);
            protectedFiles.Add(protect, SHA256.HashData(File.ReadAllBytes(protect)));
            var approved = box.CreateFixture($"approved-{round}.bin", 1024);
            var mutationsBlocked = 0;
            void ExpectBlocked(Action mutation)
            {
                try { mutation(); }
                catch (IOException) { mutationsBlocked++; return; }
                throw new InvalidOperationException("Mutation succeeded after validation; G2 failed.");
            }
            var result = VerifiedFileDeletion.DeleteFixture(box, WindowsFileMetadata.Read(approved), afterValidation: () =>
            {
                ExpectBlocked(() => File.Move(approved, approved + ".moved"));
                ExpectBlocked(() => Directory.Move(box.RunDirectory, box.RunDirectory + "-moved"));
                ExpectBlocked(() => File.WriteAllText(approved + ":late", "protected", System.Text.Encoding.UTF8));
                ExpectBlocked(() => File.WriteAllBytes(approved, [9]));
            });
            if (!result.DeleteMarked || File.Exists(approved) || mutationsBlocked != 4)
                throw new InvalidOperationException("Approved ordinary deletion failed: " + result.Reason);
            deleted++;
            var replacement = box.CreateFixture($"replacement-{round}.bin", 32);
            var preview = WindowsFileMetadata.Read(replacement);
            File.Move(replacement, replacement + ".old");
            File.WriteAllText(replacement, "protected replacement", System.Text.Encoding.UTF8);
            protectedFiles.Add(replacement, SHA256.HashData(File.ReadAllBytes(replacement)));
            protectedFiles.Add(replacement + ".old", SHA256.HashData(File.ReadAllBytes(replacement + ".old")));
            if (VerifiedFileDeletion.DeleteFixture(box, preview).DeleteMarked) throw new InvalidOperationException("Replacement deleted.");
            var stream = box.CreateFixture($"stream-{round}.bin", 32);
            var beforeStream = WindowsFileMetadata.Read(stream);
            File.WriteAllText(stream + ":added", "protected stream", System.Text.Encoding.UTF8);
            if (VerifiedFileDeletion.DeleteFixture(box, beforeStream).DeleteMarked || File.ReadAllText(stream + ":added") != "protected stream")
                throw new InvalidOperationException("Added stream lost.");
            protectedFiles.Add(stream, SHA256.HashData(File.ReadAllBytes(stream)));
            var locked = box.CreateFixture($"locked-{round}.bin", 32);
            var beforeLock = WindowsFileMetadata.Read(locked);
            using (var writer = File.Open(locked, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                if (VerifiedFileDeletion.DeleteFixture(box, beforeLock).DeleteMarked) throw new InvalidOperationException("Locked file deleted.");
            protectedFiles.Add(locked, SHA256.HashData(File.ReadAllBytes(locked)));
        }
        foreach (var entry in protectedFiles)
            if (!File.Exists(entry.Key) || !SHA256.HashData(File.ReadAllBytes(entry.Key)).SequenceEqual(entry.Value))
                throw new InvalidOperationException("Protected identity/content changed.");
        var remaining = Directory.GetFiles(box.RunDirectory).Length;
        if (remaining != protectedFiles.Count) throw new InvalidOperationException("Unexpected final object set.");
        checks.Add(new { name = "deletion-safety", status = "passed", rounds, deleted, protectedObjects = protectedFiles.Count,
            unapprovedDeleted = 0, mutationAttemptsBlocked = rounds * 4 });
    }
}
