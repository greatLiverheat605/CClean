using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CClean.Core.Storage;

public sealed record VolumeSpace(ulong UserAvailableBytes, ulong TotalBytes, ulong VolumeFreeBytes, DateTimeOffset SampledAt);
public sealed record VolumeSpaceSample(string Root, VolumeSpace? Space, string? Error)
{
    public bool Available => Space is not null;
}

public static class VolumeSpaceReader
{
    public static IReadOnlyList<VolumeSpaceSample> ReadLocalVolumes() => ReadVolumes(DriveInfo.GetDrives()
        .Where(d => d.DriveType != DriveType.Network).Select(d => d.RootDirectory.FullName));

    public static IReadOnlyList<VolumeSpaceSample> ReadVolumes(IEnumerable<string> roots)
    {
        var results = new List<VolumeSpaceSample>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { results.Add(new(root, Read(root), null)); }
            catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { results.Add(new(root, null, error.Message)); }
        }
        return results;
    }

    public static VolumeSpace Read(string path)
    {
        if (!GetDiskFreeSpaceExW(Path.GetPathRoot(Path.GetFullPath(path))!, out var available, out var total, out var free))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(available, total, free, DateTimeOffset.UtcNow);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
}
