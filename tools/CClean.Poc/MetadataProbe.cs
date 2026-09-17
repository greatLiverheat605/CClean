using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using CClean.Core.Experiments;
using CClean.Core.Files;
using CClean.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace CClean.Poc;

internal static class MetadataProbe
{
    public static void Run(ExperimentSandbox box, List<object> results)
    {
        if (!OperatingSystem.IsWindows()) throw new ExperimentRefusedException("Windows required.", 2);
        void Verify(string name, bool success, string scope = "ordinary")
        {
            if (!success) throw new InvalidOperationException($"Metadata check failed: {name}");
            results.Add(new { name, status = "passed", scope });
        }
        var normal = box.CreateFixture("中文.bin", 8192);
        var first = WindowsFileMetadata.Read(normal);
        Verify("stable-identity", first.IsOrdinaryFile && first.Identity == WindowsFileMetadata.Read(normal).Identity);
        var hard = Path.Combine(box.RunDirectory, "hard.bin");
        if (!CreateHardLinkW(hard, normal, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var linked = WindowsFileMetadata.Read(normal);
        Verify("hardlink-dedup", linked.LinkCount == 2 && !linked.IsOrdinaryFile &&
            WindowsFileMetadata.SumUnique([linked, WindowsFileMetadata.Read(hard)]).KnownLogicalBytes == 8192);
        var streamFile = box.CreateFixture("ads.bin", 100);
        File.WriteAllText(streamFile + ":named", "synthetic", System.Text.Encoding.UTF8);
        var streamInfo = WindowsFileMetadata.Read(streamFile);
        Verify("ads-exclusion", streamInfo.HasNamedStreams && !streamInfo.IsOrdinaryFile);
        var symbolic = Path.Combine(box.RunDirectory, "symlink.bin");
        File.CreateSymbolicLink(symbolic, normal);
        var symbolicInfo = WindowsFileMetadata.Read(symbolic);
        Verify("symlink-no-follow", symbolicInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) && symbolicInfo.Identity != first.Identity, "privileged-or-developer-mode");
        var directoryLink = Path.Combine(box.RunDirectory, "directory-link");
        Directory.CreateSymbolicLink(directoryLink, box.RunDirectory);
        Verify("linked-ancestor-rejected", WindowsFileMetadata.Read(Path.Combine(directoryLink, "中文.bin")).Identity is null, "privileged-or-developer-mode");
        var junction = Path.Combine(box.RunDirectory, "junction");
        CreateJunction(junction, box.RunDirectory);
        Verify("junction-no-follow", WindowsFileMetadata.Read(junction).Attributes.HasFlag(FileAttributes.ReparsePoint) &&
            WindowsFileMetadata.Read(Path.Combine(junction, "中文.bin")).Identity is null);
        foreach (var kind in new[] { "sparse", "compressed" })
        {
            var path = box.CreateFixture(kind + ".bin", 65536);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var input = kind == "compressed" ? new byte[] { 1, 0 } : Array.Empty<byte>();
            if (!DeviceIoControl(handle, kind == "sparse" ? 0x900c4u : 0x9c040u, input, input.Length,
                    IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            handle.Dispose();
            var info = WindowsFileMetadata.Read(path);
            Verify(kind + "-allocation", !info.IsOrdinaryFile && info.AllocatedBytes is not null &&
                info.Attributes.HasFlag(kind == "sparse" ? FileAttributes.SparseFile : FileAttributes.Compressed));
        }
        var deep = Path.Combine(box.RunDirectory, new string('a', 100), new string('b', 100), "中文长路径");
        Directory.CreateDirectory(deep);
        var deepPath = Path.Combine(deep, "long.bin");
        File.WriteAllBytes(deepPath, [1, 2, 3]);
        Verify("long-path", deepPath.Length > 260 && WindowsFileMetadata.Read(deepPath).IsOrdinaryFile);
        AclCheck(box, Verify);
        var space = VolumeSpaceReader.Read(box.Root);
        Verify("volume-space", space.UserAvailableBytes <= space.VolumeFreeBytes && space.VolumeFreeBytes <= space.TotalBytes);
        Verify("missing-is-unknown", WindowsFileMetadata.Read(Path.Combine(box.RunDirectory, "missing")).LogicalBytes is null);
        // Evidence is retained, but remove only the two links created by this run so the next run can validate its root.
        File.Delete(symbolic);
        Directory.Delete(directoryLink);
        Directory.Delete(junction);
    }

    private static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        using var handle = CreateFileW(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var substitute = System.Text.Encoding.Unicode.GetBytes("\\??\\" + target);
        var print = System.Text.Encoding.Unicode.GetBytes(target);
        var data = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xa0000003u).CopyTo(data, 0);
        BitConverter.GetBytes((ushort)(data.Length - 8)).CopyTo(data, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(data, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(data, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(data, 14);
        substitute.CopyTo(data, 16);
        print.CopyTo(data, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x900a4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AclCheck(ExperimentSandbox box, Action<string, bool, string> verify)
    {
        var path = box.CreateFixture("acl.bin", 100);
        var file = new FileInfo(path);
        var original = file.GetAccessControl();
        var restricted = file.GetAccessControl();
        restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl, AccessControlType.Deny));
        try
        {
            file.SetAccessControl(restricted);
            var denied = WindowsFileMetadata.Read(path);
            verify("acl-denied-is-unknown", denied.Identity is null && denied.LogicalBytes is null, "current-user-acl");
        }
        finally { file.SetAccessControl(original); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string target, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputSize,
        IntPtr output, int outputSize, out uint returned, IntPtr overlapped);
}
