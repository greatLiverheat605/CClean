using System.ComponentModel;
using System.Runtime.InteropServices;
using CClean.Core.Experiments;
using CClean.Core.Files;
using Microsoft.Win32.SafeHandles;

namespace CClean.Core.Cleanup;

public sealed record DeletionResult(bool DeleteMarked, string Reason);

// PoC-only boundary. No product entry point may call this as user authorization.
public static class VerifiedFileDeletion
{
    public static DeletionResult DeleteFixture(ExperimentSandbox sandbox, FileSnapshot approved,
        Action? beforeOpen = null, Action? afterValidation = null)
    {
        try
        {
            if (!approved.IsOrdinaryFile) return new(false, "Preview object is not ordinary.");
            var path = ExperimentSandbox.Normalize(approved.Path);
            if (!path.StartsWith(sandbox.RunDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return new(false, "Outside this experiment run.");
            beforeOpen?.Invoke();
            using var ancestors = new DirectoryLease(Path.GetDirectoryName(path)!);
            using var handle = WindowsFileMetadata.Open(path, access: 0x10080, share: 1);
            var current = WindowsFileMetadata.Read(handle, path);
            if (current != approved || !current.IsOrdinaryFile) return new(false, "Object changed since preview.");
            afterValidation?.Invoke();
            if (!SetFileInformationByHandle(handle, 4, [1], 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(true, "Delete disposition set; physical release is measured separately.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ExperimentRefusedException)
        {
            return new(false, error.Message);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, byte[] info, uint size);
}
