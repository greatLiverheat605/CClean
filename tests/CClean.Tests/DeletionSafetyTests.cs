using System.Security.Cryptography;
using CClean.Core.Cleanup;
using CClean.Core.Experiments;
using CClean.Core.Files;

namespace CClean.Tests;

[Trait("Category", "DeletionSafety")]
[Trait("Category", "M0")]
[Trait("Category", "Safety")]
public class DeletionSafetyTests
{
    private static ExperimentSandbox Box() => new(Path.Combine(Path.GetTempPath(), "cclean-deletion-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void ApprovedOrdinaryObjectIsDeleted()
    {
        using var box = Box();
        var file = box.CreateFixture("approved.bin", 1024);
        var result = VerifiedFileDeletion.DeleteFixture(box, WindowsFileMetadata.Read(file));
        Assert.True(result.DeleteMarked, result.Reason);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void ReplacementAndNewFilesArePreserved()
    {
        using var box = Box();
        var file = box.CreateFixture("approved.bin", 1024);
        var approved = WindowsFileMetadata.Read(file);
        var result = VerifiedFileDeletion.DeleteFixture(box, approved, beforeOpen: () =>
        {
            File.Move(file, file + ".original");
            File.WriteAllText(file, "protected replacement", System.Text.Encoding.UTF8);
            box.CreateFixture("new.bin", 100);
        });
        Assert.False(result.DeleteMarked);
        Assert.Equal("protected replacement", File.ReadAllText(file));
        Assert.True(File.Exists(Path.Combine(box.RunDirectory, "new.bin")));
        Assert.True(File.Exists(file + ".original"));
    }

    [Fact]
    public void NamedStreamAddedBeforeOpenAndLockedFileAreExcluded()
    {
        using var box = Box();
        var file = box.CreateFixture("approved.bin", 1024);
        var approved = WindowsFileMetadata.Read(file);
        File.WriteAllText(file + ":new", "protected", System.Text.Encoding.UTF8);
        Assert.False(VerifiedFileDeletion.DeleteFixture(box, approved).DeleteMarked);
        Assert.Equal("protected", File.ReadAllText(file + ":new"));
        var occupied = box.CreateFixture("occupied.bin", 100);
        var preview = WindowsFileMetadata.Read(occupied);
        using var writer = File.Open(occupied, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.False(VerifiedFileDeletion.DeleteFixture(box, preview).DeleteMarked);
    }

    [Fact]
    public void HeldHandlesPreventLeafAncestorAndStreamMutation()
    {
        using var box = Box();
        var file = box.CreateFixture("approved.bin", 1024);
        var protectedFile = box.CreateFixture("protected.bin", 900);
        var hash = SHA256.HashData(File.ReadAllBytes(protectedFile));
        var result = VerifiedFileDeletion.DeleteFixture(box, WindowsFileMetadata.Read(file), afterValidation: () =>
        {
            Assert.Throws<IOException>(() => File.Move(file, file + ".moved"));
            Assert.Throws<IOException>(() => Directory.Move(box.RunDirectory, box.RunDirectory + "-moved"));
            Assert.Throws<IOException>(() => File.WriteAllText(file + ":late", "new protected stream", System.Text.Encoding.UTF8));
        });
        Assert.True(result.DeleteMarked, result.Reason);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(protectedFile)));
    }

    [Fact]
    public void SimilarPathPrefixDoesNotGrantScope()
    {
        using var box = Box();
        var other = Path.Combine(box.Root, Path.GetFileName(box.RunDirectory) + "-other");
        Directory.CreateDirectory(other);
        var file = Path.Combine(other, "protected.bin");
        File.WriteAllText(file, "protected", System.Text.Encoding.UTF8);
        Assert.False(VerifiedFileDeletion.DeleteFixture(box, WindowsFileMetadata.Read(file)).DeleteMarked);
        Assert.Equal("protected", File.ReadAllText(file));
    }
}
