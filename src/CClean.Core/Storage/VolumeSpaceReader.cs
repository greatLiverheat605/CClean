using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CClean.Core.Storage;

public sealed record VolumeSpace(ulong UserAvailableBytes, ulong TotalBytes, ulong VolumeFreeBytes, DateTimeOffset SampledAt);

public static class VolumeSpaceReader
{
    public static VolumeSpace Read(string path)
    {
        if (!GetDiskFreeSpaceExW(Path.GetPathRoot(Path.GetFullPath(path))!, out var available, out var total, out var free))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(available, total, free, DateTimeOffset.UtcNow);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
}
