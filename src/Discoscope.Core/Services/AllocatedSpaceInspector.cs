using System.Runtime.InteropServices;
using Discoscope.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace Discoscope.Core.Services;

public sealed record AllocatedSpaceResult(long Bytes, long MeasuredPaths, long UnavailablePaths, long DuplicateLinks)
{
    public long MeasuredLogicalBytes { get; init; }
}

public sealed class AllocatedSpaceInspector
{
    public Task<AllocatedSpaceResult> InspectAsync(ScanDirectory root, CancellationToken token = default) =>
        Task.Run(() => Inspect(root, token), token);

    private static AllocatedSpaceResult Inspect(ScanDirectory root, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var volume = new DriveInfo(Path.GetPathRoot(root.Path)!);
        if (volume.DriveType != DriveType.Fixed || !volume.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new PlatformNotSupportedException();

        var linkedFiles = new HashSet<FileIdentity>();
        long bytes = 0, logicalBytes = 0, measured = 0, unavailable = 0, duplicates = 0;
        foreach (var directory in root.EnumerateSubtree())
        {
            token.ThrowIfCancellationRequested();
            foreach (var file in directory.Files)
            {
                token.ThrowIfCancellationRequested();
                string path = Path.Join(directory.Path, file.Name);
                if (path.Length >= 260 && path.Length > 2 && path[1] == ':')
                    path = @"\\?\" + path;
                using var handle = CreateFile(path, 0, 7, IntPtr.Zero,
                    3, 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid ||
                    !GetAttributeInfo(handle, 9, out var attributes, Marshal.SizeOf<AttributeInfo>()) ||
                    (attributes.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                    !GetStandardInfo(handle, 1, out var standard, Marshal.SizeOf<StandardInfo>()) ||
                    standard.DeletePending != 0 || standard.EndOfFile != file.Bytes || standard.AllocationSize < 0)
                {
                    unavailable++;
                    continue;
                }

                long allocation = standard.AllocationSize;
                if ((attributes.Attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
                {
                    if (!GetCompressionInfo(handle, 8, out var compression, Marshal.SizeOf<CompressionInfo>()) ||
                        compression.CompressedFileSize < 0)
                    {
                        unavailable++;
                        continue;
                    }
                    allocation = compression.CompressedFileSize;
                }

                if (standard.NumberOfLinks > 1)
                {
                    if (!GetIdentity(handle, 18, out var identity, Marshal.SizeOf<FileIdentity>()) ||
                        (identity.Low == 0 && identity.High == 0))
                    {
                        unavailable++;
                        continue;
                    }
                    if (!linkedFiles.Add(identity))
                    {
                        duplicates++;
                        continue;
                    }
                }
                bytes = checked(bytes + allocation);
                logicalBytes = checked(logicalBytes + standard.EndOfFile);
                measured++;
            }
        }
        return new(bytes, measured, unavailable, duplicates) { MeasuredLogicalBytes = logicalBytes };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeInfo
    {
        public FileAttributes Attributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct CompressionInfo
    {
        public long CompressedFileSize;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandardInfo(SafeFileHandle handle, int infoClass, out StandardInfo value, int bytes);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle handle, int infoClass, out FileIdentity value, int bytes);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributeInfo(SafeFileHandle handle, int infoClass, out AttributeInfo value, int bytes);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCompressionInfo(SafeFileHandle handle, int infoClass, out CompressionInfo value, int bytes);
}
