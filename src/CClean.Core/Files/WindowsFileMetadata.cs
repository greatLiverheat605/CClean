using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CClean.Core.Experiments;
using Microsoft.Win32.SafeHandles;

namespace CClean.Core.Files;

public sealed record FileSnapshot(string Path, string? Identity, long? LogicalBytes, long? AllocatedBytes,
    uint LinkCount, FileAttributes Attributes, long ModifiedTime, long ChangeTime, bool StreamsComplete,
    bool HasNamedStreams, string? Exclusion)
{
    public bool IsOrdinaryFile => Exclusion is null;
}

public static class WindowsFileMetadata
{
    internal static FileSnapshot Read(string path, DirectoryLease parent)
    {
        try
        {
            var full = ExperimentSandbox.Normalize(path);
            if (!parent.IsHeld || !System.IO.Path.GetDirectoryName(full)!.TrimEnd(System.IO.Path.DirectorySeparatorChar)
                    .Equals(parent.DirectoryPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Stable parent does not match file.");
            using var handle = Open(full);
            return Read(handle, full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ExperimentRefusedException)
        {
            return new(path, null, null, null, 0, 0, 0, 0, false, false, error.Message);
        }
    }

    public static FileSnapshot Read(string path)
    {
        try
        {
            var full = ExperimentSandbox.Normalize(path);
            // A missing parent is not created by a read-only metadata query.
            if (!Directory.Exists(System.IO.Path.GetDirectoryName(full))) throw new IOException("Parent missing.");
            using var lease = new DirectoryLease(System.IO.Path.GetDirectoryName(full)!);
            using var handle = Open(full);
            return Read(handle, full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ExperimentRefusedException)
        {
            return new(path, null, null, null, 0, 0, 0, 0, false, false, error.Message);
        }
    }

    internal static SafeFileHandle Open(string path, uint access = 0x80, uint share = 7)
    {
        var handle = CreateFileW("\\\\?\\" + path, access, share, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var code = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(code);
    }

    internal static FileSnapshot Read(SafeFileHandle handle, string observedPath)
    {
        var finalPath = new StringBuilder(Math.Max(260, observedPath.Length + 5));
        var pathLength = GetFinalPathNameByHandleW(handle, finalPath, (uint)finalPath.Capacity, 0);
        if (pathLength == 0 || pathLength >= finalPath.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var resolved = finalPath.ToString();
        if (!resolved.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            !resolved[4..].Equals(observedPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Final handle path differs from the approved path.");
        var id = Info(handle, 18, 24);
        var basic = Info(handle, 0, 40);
        var standard = Info(handle, 1, 24);
        var attributes = (FileAttributes)BitConverter.ToUInt32(basic, 32);
        var links = BitConverter.ToUInt32(standard, 16);
        var special = (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.SparseFile |
            FileAttributes.Compressed | FileAttributes.Offline | FileAttributes.Encrypted | FileAttributes.Device)) != 0;
        var complete = false;
        var named = false;
        if (!attributes.HasFlag(FileAttributes.ReparsePoint) && !attributes.HasFlag(FileAttributes.Directory))
        {
            try
            {
                var streams = Info(handle, 7, 64 * 1024);
                var offset = 0;
                do
                {
                    if (offset > streams.Length - 24) throw new IOException("Invalid stream information.");
                    var next = BitConverter.ToUInt32(streams, offset);
                    var length = BitConverter.ToUInt32(streams, offset + 4);
                    if (length % 2 != 0 || length > streams.Length - offset - 24) throw new IOException("Invalid stream name.");
                    var name = Encoding.Unicode.GetString(streams, offset + 24, (int)length);
                    if (name != "::$DATA") named = true;
                    if (next == 0) break;
                    if (next < 24 || next > streams.Length - offset) throw new IOException("Invalid stream offset.");
                    offset += (int)next;
                } while (true);
                complete = true;
            }
            catch (Win32Exception) { }
        }
        var exclusion = special ? "Special object" : links != 1 ? "Multiple hard links" :
            !complete ? "Stream enumeration incomplete" : named ? "Named stream" : null;
        return new(observedPath, Convert.ToHexString(id) + ":" + BitConverter.ToInt64(basic, 0), BitConverter.ToInt64(standard, 8),
            BitConverter.ToInt64(standard, 0), links, attributes, BitConverter.ToInt64(basic, 16),
            BitConverter.ToInt64(basic, 24), complete, named, exclusion);
    }

    public static (long KnownLogicalBytes, long KnownAllocatedBytes, int UnknownObjects) SumUnique(IEnumerable<FileSnapshot> files)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long logical = 0, allocated = 0;
        var unknown = 0;
        foreach (var file in files)
        {
            if (file.Identity is null || file.LogicalBytes is null || file.AllocatedBytes is null) { unknown++; continue; }
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) { unknown++; continue; }
            if (!file.StreamsComplete || file.HasNamedStreams) unknown++;
            if (seen.Add(file.Identity)) { logical = checked(logical + file.LogicalBytes.Value); allocated = checked(allocated + file.AllocatedBytes.Value); }
        }
        return (logical, allocated, unknown);
    }

    private static byte[] Info(SafeFileHandle handle, int kind, int size)
    {
        var bytes = new byte[size];
        if (!GetFileInformationByHandleEx(handle, kind, bytes, (uint)size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return bytes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, [Out] byte[] info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
