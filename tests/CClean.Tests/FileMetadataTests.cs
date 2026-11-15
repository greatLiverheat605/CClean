using System.Runtime.InteropServices;
using CClean.Core.Experiments;
using CClean.Core.Files;
using CClean.Core.Storage;

namespace CClean.Tests;

[Trait("Category", "MvpP0")]
[Trait("Category", "FileMetadata")]
[Trait("Category", "M0")]
public class FileMetadataTests
{
    private static ExperimentSandbox Sandbox() => new(Path.Combine(Path.GetTempPath(), "cclean-metadata-" + Guid.NewGuid().ToString("N")));

    [Fact]
    [Trait("Category", "Foundation")]
    public void OrdinaryIdentityIsStableAndUnknownIsNotZero()
    {
        using var box = Sandbox();
        var path = box.CreateFixture("中文.bin", 12345);
        var first = WindowsFileMetadata.Read(path);
        Assert.True(first.IsOrdinaryFile, first.Exclusion);
        Assert.Equal(12345, first.LogicalBytes);
        Assert.True(first.AllocatedBytes >= first.LogicalBytes);
        var renamed = Path.Combine(box.RunDirectory, "renamed.bin");
        File.Move(path, renamed);
        Assert.Equal(first.Identity, WindowsFileMetadata.Read(renamed).Identity);
        var missing = WindowsFileMetadata.Read(path);
        Assert.Null(missing.LogicalBytes);
        Assert.Equal(1, WindowsFileMetadata.SumUnique([first, missing]).UnknownObjects);
    }

    [Fact]
    [Trait("Category", "Foundation")]
    public void HardLinksAreDeduplicatedAndExcluded()
    {
        using var box = Sandbox();
        var path = box.CreateFixture("original.bin", 8000);
        var alias = Path.Combine(box.RunDirectory, "alias.bin");
        Assert.True(CreateHardLinkW(alias, path, IntPtr.Zero));
        var a = WindowsFileMetadata.Read(path);
        var b = WindowsFileMetadata.Read(alias);
        Assert.Equal(a.Identity, b.Identity);
        Assert.Equal(2u, a.LinkCount);
        Assert.False(a.IsOrdinaryFile);
        Assert.Equal(8000, WindowsFileMetadata.SumUnique([a, b]).KnownLogicalBytes);
    }

    [Fact]
    [Trait("Category", "Foundation")]
    public void NamedStreamsAreExcluded()
    {
        using var box = Sandbox();
        var path = box.CreateFixture("streams.bin", 100);
        File.WriteAllText(path + ":secret", "synthetic", System.Text.Encoding.UTF8);
        var info = WindowsFileMetadata.Read(path);
        Assert.True(info.StreamsComplete);
        Assert.True(info.HasNamedStreams);
        Assert.False(info.IsOrdinaryFile);
    }

    [Fact]
    public void RepeatedReadsPreserveStreamSafetyWithoutLargePerFileAllocations()
    {
        using var box = Sandbox();
        var ordinary = box.CreateFixture("ordinary.bin", 10);
        var named = box.CreateFixture("named.bin", 10);
        File.WriteAllText(named + ":extra", "synthetic", System.Text.Encoding.UTF8);
        WindowsFileMetadata.Read(ordinary); WindowsFileMetadata.Read(named);
        var before = GC.GetAllocatedBytesForCurrentThread();
        const int reads = 64;
        for (var i = 0; i < reads; i++)
        {
            var metadata = WindowsFileMetadata.Read(i % 2 == 0 ? ordinary : named);
            Assert.True(metadata.StreamsComplete);
            Assert.Equal(i % 2 != 0, metadata.HasNamedStreams);
            Assert.Equal(i % 2 == 0, metadata.IsOrdinaryFile);
        }
        // The old 64 KiB-per-file stream buffer exceeds this generous allocation bound.
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < reads * 16 * 1024);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("ads")]
    [InlineData("replace")]
    [InlineData("hardlink")]
    public void ReusingSnapshotValuesStillChecksFreshHandleMetadataAndStreams(string change)
    {
        using var box = Sandbox();
        var path = box.CreateFixture("revalidated.bin", 10);
        using var parent = new DirectoryLease(box.RunDirectory);
        var first = WindowsFileMetadata.Read(path, parent);
        Assert.True(first.IsOrdinaryFile);
        Assert.Same(first, WindowsFileMetadata.Read(path, parent, first));
        switch (change)
        {
            case "write": File.AppendAllText(path, "external", System.Text.Encoding.UTF8); break;
            case "ads": File.WriteAllBytes(path + ":external", [9]); break;
            case "replace": File.Delete(path); File.WriteAllBytes(path, [9]); break;
            case "hardlink": Assert.True(CreateHardLinkW(Path.Combine(box.RunDirectory, "alias.bin"), path, IntPtr.Zero)); break;
        }
        var current = WindowsFileMetadata.Read(path, parent, first);
        Assert.NotSame(first, current);
        Assert.Equal(WindowsFileMetadata.Read(path), current);
        if (change is "ads" or "hardlink") Assert.False(current.IsOrdinaryFile);
    }

    [Fact]
    public void SymbolicLinkIsNotFollowed()
    {
        using var box = Sandbox();
        var path = box.CreateFixture("target.bin", 100);
        var link = Path.Combine(box.RunDirectory, "link.bin");
        File.CreateSymbolicLink(link, path);
        var info = WindowsFileMetadata.Read(link);
        Assert.True(info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.NotEqual(WindowsFileMetadata.Read(path).Identity, info.Identity);
        Assert.False(info.IsOrdinaryFile);
    }

    [Fact]
    [Trait("Category", "Foundation")]
    public void SpaceSamplesKeepUserAndVolumeValuesSeparate()
    {
        var space = VolumeSpaceReader.Read(Path.GetTempPath());
        Assert.True(space.UserAvailableBytes <= space.VolumeFreeBytes);
        Assert.True(space.VolumeFreeBytes <= space.TotalBytes);
    }

    [Fact]
    public void VolumeListingKeepsUnavailableVolumesSeparateFromZeroCapacity()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var volumes = VolumeSpaceReader.ReadVolumes([root, root.ToLowerInvariant(), "invalid\0root"]);
        Assert.Equal(2, volumes.Count);
        Assert.True(volumes[0].Available);
        Assert.NotNull(volumes[0].Space);
        Assert.False(volumes[1].Available);
        Assert.Null(volumes[1].Space);
        Assert.NotEmpty(volumes[1].Error!);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string target, IntPtr security);
}
