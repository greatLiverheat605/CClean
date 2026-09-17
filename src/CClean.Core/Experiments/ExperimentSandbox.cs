using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CClean.Core.Experiments;

public sealed class ExperimentRefusedException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

// Holds every existing ancestor against rename/delete for the experiment lifetime.
// This is an experiment boundary, not the product's deletion authorization.
public sealed class DirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    internal string DirectoryPath { get; }
    internal bool IsHeld => handles.Count > 0;

    public DirectoryLease(string path, bool createMissing = false)
    {
        try
        {
            var full = Path.GetFullPath(path);
            DirectoryPath = full.TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetPathRoot(full)!;
            var current = root;
            Open(current);
            foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current))
                {
                    if (!createMissing) throw new IOException("Directory does not exist.");
                    Directory.CreateDirectory(current);
                }
                Open(current);
            }
        }
        catch { Dispose(); throw; }
    }

    private void Open(string path)
    {
        var handle = CreateFileW("\\\\?\\" + path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new ExperimentRefusedException($"Cannot stabilize directory: {new Win32Exception().Message}", 3);
        }
        handles.Add(handle);
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag info, 8) ||
            (info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
            throw new ExperimentRefusedException("Reparse point or unverifiable directory refused.", 3);
    }

    public void Dispose()
    {
        for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
        handles.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind,
        out AttributeTag info, uint size);
}

public sealed class ExperimentSandbox : IDisposable
{
    public const string PlanId = "CCLEAN-20260917-DEV";
    public const long DefaultBudget = 256L * 1024 * 1024;
    public const long DefaultReserve = 10L * 1024 * 1024 * 1024;
    private readonly DirectoryLease lease;
    private readonly FileStream marker;
    private long allocated;
    public string Root { get; }
    public string RunId { get; } = $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
    public string RunDirectory { get; }
    public long Budget { get; }
    public long Reserve { get; }

    public ExperimentSandbox(string root, long budget = DefaultBudget, long reserve = DefaultReserve)
    {
        if (!OperatingSystem.IsWindows()) throw new ExperimentRefusedException("Windows required.", 2);
        Root = Normalize(root);
        Budget = budget;
        Reserve = reserve;
        var drive = new DriveInfo(Path.GetPathRoot(Root)!);
        CheckCapacity(budget, reserve, drive.AvailableFreeSpace, drive.DriveFormat,
            string.Equals(drive.Name, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase));
        if (Root == Path.GetPathRoot(Root)?.TrimEnd('\\'))
            throw new ExperimentRefusedException("Volume root refused.", 3);
        // Existing roots must already be ours; an empty existing directory is not permission.
        var existed = Directory.Exists(Root);
        lease = new DirectoryLease(Root, createMissing: true);
        try
        {
            var markerPath = Path.Combine(Root, ".cclean-sandbox");
            if (existed)
            {
                if (!File.Exists(markerPath) || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
                    throw new ExperimentRefusedException("Existing directory is not an experiment root.", 3);
                marker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(marker, leaveOpen: true);
                if (reader.ReadToEnd() != PlanId) throw new ExperimentRefusedException("Wrong sandbox owner.", 3);
                var pending = new Stack<string>();
                pending.Push(Root);
                while (pending.TryPop(out var directory))
                {
                    using var childLease = new DirectoryLease(directory);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            throw new ExperimentRefusedException("Existing sandbox contains a link.", 3);
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                        else if (entry != markerPath)
                        {
                            var length = new FileInfo(entry).Length;
                            if (length > Budget - allocated)
                                throw new ExperimentRefusedException("Retained fixtures exceed the total sandbox budget.", 2);
                            allocated += length;
                        }
                    }
                }
            }
            else
            {
                marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                marker.Write(System.Text.Encoding.UTF8.GetBytes(PlanId));
                marker.Flush(true);
            }
            RunDirectory = Path.Combine(Root, RunId);
            Directory.CreateDirectory(RunDirectory);
        }
        catch { marker?.Dispose(); lease.Dispose(); throw; }
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            (path.Length > 2 && path.IndexOf(':', 2) >= 0) || path.Split('\\', '/').Any(s => s is ".." || s.EndsWith(' ') || s.EndsWith('.')))
            throw new ExperimentRefusedException("Ambiguous, device, network or traversal path refused.", 3);
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    }

    public static void CheckCapacity(long budget, long reserve, long free, string format, bool systemVolume)
    {
        if (budget <= 0 || reserve < DefaultReserve)
            throw new ExperimentRefusedException("Positive budget and at least 10 GiB reserve required.", 2);
        if (format != "NTFS" || (systemVolume && budget > DefaultBudget))
            throw new ExperimentRefusedException("Large experiments require a non-system NTFS volume.", 2);
        if (free < reserve || budget > free - reserve)
            throw new ExperimentRefusedException("Insufficient experiment capacity.", 2);
    }

    public string CreateFixture(string name, long bytes)
    {
        if (name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || bytes < 0)
            throw new ExperimentRefusedException("Invalid fixture.", 3);
        if (bytes > Budget - allocated) throw new ExperimentRefusedException("Fixture budget exceeded.", 2);
        var drive = new DriveInfo(Path.GetPathRoot(Root)!);
        CheckCapacity(Budget, Reserve, drive.AvailableFreeSpace, drive.DriveFormat,
            drive.Name.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase));
        using var runLease = new DirectoryLease(RunDirectory);
        var path = Path.Combine(RunDirectory, name);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[64 * 1024];
        for (long left = bytes; left > 0; left -= Math.Min(left, buffer.Length))
        {
            if (drive.AvailableFreeSpace < Reserve + buffer.Length)
                throw new ExperimentRefusedException("Free space reserve reached while writing.", 2);
            file.Write(buffer, 0, (int)Math.Min(left, buffer.Length));
        }
        file.Flush(true);
        allocated += bytes;
        return path;
    }

    public void Dispose() { marker.Dispose(); lease.Dispose(); }
}
