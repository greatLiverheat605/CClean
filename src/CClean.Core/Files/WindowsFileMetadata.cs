using System.Buffers;
using System.ComponentModel;
using System.Globalization;
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
    internal static bool IsStreamlessDirectory(string path, string identity)
    {
        try
        {
            using var lease = new DirectoryLease(path);
            using var handle = Open(path);
            var current = Read(handle, path);
            if (current.Identity != identity || (current.Attributes & FileAttributes.Directory) == 0 ||
                (current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.System)) != 0) return false;
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                // NTFS directories have no unnamed data stream. Any returned stream is protected content.
                // ERROR_HANDLE_EOF is the only accepted empty result; other errors remain unknown.
                if (GetFileInformationByHandleEx(handle, 7, buffer, (uint)buffer.Length)) return false;
                return Marshal.GetLastWin32Error() == 38;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ExperimentRefusedException)
        { return false; }
    }

    internal static FileSnapshot Read(string path, DirectoryLease parent, FileSnapshot? previous = null)
    {
        try
        {
            var full = ExperimentSandbox.Normalize(path);
            if (!parent.IsHeld || !System.IO.Path.GetDirectoryName(full)!.TrimEnd(System.IO.Path.DirectorySeparatorChar)
                    .Equals(parent.DirectoryPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Stable parent does not match file.");
            using var handle = Open(full);
            return Read(handle, full, previous);
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

    internal static FileSnapshot Read(SafeFileHandle handle, string observedPath, FileSnapshot? previous = null)
    {
        var finalPath = ArrayPool<char>.Shared.Rent(Math.Max(260, observedPath.Length + 5));
        try
        {
            var pathLength = GetFinalPathNameByHandleW(handle, finalPath, (uint)finalPath.Length, 0);
            if (pathLength == 0 || pathLength >= finalPath.Length) throw new Win32Exception(Marshal.GetLastWin32Error());
            var resolved = finalPath.AsSpan(0, (int)pathLength);
            if (!resolved.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
                !resolved[4..].Equals(observedPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Final handle path differs from the approved path.");
        }
        finally { ArrayPool<char>.Shared.Return(finalPath, clearArray: true); }
        // Native metadata buffers are temporary; retain only the immutable snapshot values.
        var info = ArrayPool<byte>.Shared.Rent(40);
        Span<char> identityCharacters = stackalloc char[70];
        var identityLength = 0;
        long created, modified, changed, logical, allocated;
        FileAttributes attributes;
        uint links;
        try
        {
            Info(handle, 18, info, 24);
            Convert.TryToHexString(info.AsSpan(0, 24), identityCharacters, out identityLength);
            Info(handle, 0, info, 40);
            created = BitConverter.ToInt64(info, 0);
            modified = BitConverter.ToInt64(info, 16);
            changed = BitConverter.ToInt64(info, 24);
            attributes = (FileAttributes)BitConverter.ToUInt32(info, 32);
            Info(handle, 1, info, 24);
            logical = BitConverter.ToInt64(info, 8);
            allocated = BitConverter.ToInt64(info, 0);
            links = BitConverter.ToUInt32(info, 16);
        }
        finally { ArrayPool<byte>.Shared.Return(info, clearArray: true); }
        identityCharacters[identityLength++] = ':';
        created.TryFormat(identityCharacters[identityLength..], out var createdLength, provider: CultureInfo.InvariantCulture);
        identityLength += createdLength;
        var identity = previous?.Identity is string existingIdentity && identityCharacters[..identityLength].SequenceEqual(existingIdentity)
            ? existingIdentity : new string(identityCharacters[..identityLength]);
        var special = (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.SparseFile |
            FileAttributes.Compressed | FileAttributes.Offline | FileAttributes.Encrypted | FileAttributes.Device)) != 0;
        var complete = false;
        var named = false;
        if (!attributes.HasFlag(FileAttributes.ReparsePoint) && !attributes.HasFlag(FileAttributes.Directory))
        {
            var streams = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                // Reuse the bounded metadata buffer across large scans; no file content is read.
                if (!GetFileInformationByHandleEx(handle, 7, streams, (uint)streams.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
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
            finally { ArrayPool<byte>.Shared.Return(streams, clearArray: true); }
        }
        var exclusion = special ? "Special object" : links != 1 ? "Multiple hard links" :
            !complete ? "Stream enumeration incomplete" : named ? "Named stream" : null;
        // Reuse values only after every native identity/path/metadata/stream check above.
        if (previous is not null && previous.Path == observedPath && previous.Identity == identity &&
            previous.LogicalBytes == logical && previous.AllocatedBytes == allocated && previous.LinkCount == links &&
            previous.Attributes == attributes && previous.ModifiedTime == modified && previous.ChangeTime == changed &&
            previous.StreamsComplete == complete && previous.HasNamedStreams == named && previous.Exclusion == exclusion)
            return previous;
        return new(observedPath, identity, logical,
            allocated, links, attributes, modified, changed, complete, named, exclusion);
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

    private static void Info(SafeFileHandle handle, int kind, byte[] bytes, int size)
    {
        if (!GetFileInformationByHandleEx(handle, kind, bytes, (uint)size)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, [Out] byte[] info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint capacity, uint flags);
}
