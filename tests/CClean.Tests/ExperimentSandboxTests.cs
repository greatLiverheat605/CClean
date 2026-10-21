using CClean.Core.Experiments;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
[Trait("Category", "Environment")]
[Trait("Category", "M0")]
[Trait("Category", "Safety")]
public class ExperimentSandboxTests
{
    [Theory]
    [Trait("Category", "Foundation")]
    [InlineData("C:/lab/../user")]
    [InlineData("C:/lab/file:stream")]
    [InlineData("C:/lab/name.")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\?\\C:\\lab")]
    public void AmbiguousPathsAreRejected(string path) =>
        Assert.Equal(3, Assert.Throws<ExperimentRefusedException>(() => ExperimentSandbox.Normalize(path)).ExitCode);

    [Fact]
    [Trait("Category", "Foundation")]
    public void BudgetCannotExhaustVolumeOrGrowOnSystemDrive()
    {
        Assert.Throws<ExperimentRefusedException>(() => ExperimentSandbox.CheckCapacity(1024, ExperimentSandbox.DefaultReserve, 4096, "NTFS", false));
        Assert.Throws<ExperimentRefusedException>(() => ExperimentSandbox.CheckCapacity(long.MaxValue, ExperimentSandbox.DefaultReserve, long.MaxValue, "NTFS", false));
        Assert.Throws<ExperimentRefusedException>(() => ExperimentSandbox.CheckCapacity(ExperimentSandbox.DefaultBudget + 1, ExperimentSandbox.DefaultReserve, long.MaxValue, "NTFS", true));
        Assert.Throws<ExperimentRefusedException>(() => ExperimentSandbox.CheckCapacity(1024, 0, long.MaxValue, "NTFS", false));
    }

    [Fact]
    [Trait("Category", "Foundation")]
    public void ExistingUnownedDirectoryIsNotAdopted()
    {
        var root = Path.Combine(Path.GetTempPath(), "cclean-unowned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(3, Assert.Throws<ExperimentRefusedException>(() => new ExperimentSandbox(root)).ExitCode);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public void DirectoryLinkIsRejectedWithoutWritingToItsTarget()
    {
        var parent = Path.Combine(Path.GetTempPath(), "cclean-link-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(parent, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(parent, "link");
        Directory.CreateSymbolicLink(link, target);
        Assert.Equal(3, Assert.Throws<ExperimentRefusedException>(() => new ExperimentSandbox(link)).ExitCode);
        Assert.Empty(Directory.GetFileSystemEntries(target));
        Directory.Delete(link);
        Directory.Delete(target);
        Directory.Delete(parent);
    }

    [Fact]
    [Trait("Category", "Foundation")]
    public void OwnedRunsAreUniqueAndFixturesCannotEscapeOrExceedBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), "cclean-owned-" + Guid.NewGuid().ToString("N"));
        string firstRun;
        using (var sandbox = new ExperimentSandbox(root, 4096))
        {
            firstRun = sandbox.RunDirectory;
            Assert.Throws<ExperimentRefusedException>(() => sandbox.CreateFixture("../escape", 1));
            Assert.Throws<ExperimentRefusedException>(() => sandbox.CreateFixture("oversized", 4097));
            var path = sandbox.CreateFixture("small.bin", 4096);
            Assert.Equal(4096, new FileInfo(path).Length);
            Assert.Throws<ExperimentRefusedException>(() => sandbox.CreateFixture("extra.bin", 1));
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
        }
        using var next = new ExperimentSandbox(root, 4096);
        Assert.NotEqual(firstRun, next.RunDirectory);
        Assert.True(File.Exists(Path.Combine(firstRun, "small.bin")));
        // Keep marked fixture evidence; no generic recursive teardown in a safety test.
    }
}
