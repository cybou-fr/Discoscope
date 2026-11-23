using System.Runtime.InteropServices;
using Discoscope.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace Discoscope.Core.Services;

public sealed record AllocatedFile(string Path, long AllocatedBytes, long LogicalBytes);
public sealed record AllocatedFolder(ScanDirectory Directory, long AllocatedBytes, long LogicalBytes, bool IsPartial);
public sealed record AllocatedSpaceProgress(long ProcessedPaths, long TotalPaths, long AllocatedBytes);

public sealed record AllocatedSpaceResult(long Bytes, long MeasuredPaths, long UnavailablePaths, long DuplicateLinks)
{
    public long MeasuredLogicalBytes { get; init; }
    public IReadOnlyList<AllocatedFile> LargestFiles { get; init; } = [];
    public IReadOnlyList<AllocatedFolder> LargestFolders { get; init; } = [];
}

public sealed class AllocatedSpaceInspector
{
    public Task<AllocatedSpaceResult> InspectAsync(ScanDirectory root, CancellationToken token = default) =>
        InspectAsync(root, progress: null, token);

    public Task<AllocatedSpaceResult> InspectAsync(ScanDirectory root, IProgress<AllocatedSpaceProgress>? progress,
        CancellationToken token) => Task.Run(() => Inspect(root, progress, token), token);

    private static AllocatedSpaceResult Inspect(ScanDirectory root, IProgress<AllocatedSpaceProgress>? progress,
        CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var volume = new DriveInfo(Path.GetPathRoot(root.Path)!);
        if (volume.DriveType != DriveType.Fixed || !volume.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new PlatformNotSupportedException();

        var linkedFiles = new HashSet<FileIdentity>();
        var folderLinks = new HashSet<FileIdentity>();
        const int maxLargestFiles = 200;
        var largestFiles = new PriorityQueue<AllocatedFile, (long Bytes, long Order)>();
        var largestFolders = new PriorityQueue<AllocatedFolder, (long Bytes, long Order)>();
        long bytes = 0, logicalBytes = 0, measured = 0, unavailable = 0, duplicates = 0;
        long lastReport = 0;
        long folderOrder = 0;
        long fileTimeEpochTicks = DateTime.FromFileTimeUtc(0).Ticks;
        MeasureDirectories([root], null);
        foreach (var child in root.Directories)
        {
            token.ThrowIfCancellationRequested();
            folderLinks.Clear();
            MeasureDirectories(child.EnumerateSubtree(), child);
        }
        token.ThrowIfCancellationRequested();
        progress?.Report(new(measured + unavailable + duplicates, root.FileCount, bytes));
        token.ThrowIfCancellationRequested();
        return new(bytes, measured, unavailable, duplicates)
        {
            MeasuredLogicalBytes = logicalBytes,
            LargestFiles = largestFiles.UnorderedItems.OrderByDescending(item => item.Priority)
                .Select(item => item.Element).ToArray(),
            LargestFolders = largestFolders.UnorderedItems.OrderByDescending(item => item.Priority)
                .Select(item => item.Element).ToArray()
        };

        void MeasureDirectories(IEnumerable<ScanDirectory> directories, ScanDirectory? folder)
        {
            long folderBytes = 0, folderLogicalBytes = 0;
            long unavailableBefore = unavailable;
            foreach (var directory in directories)
            {
                token.ThrowIfCancellationRequested();
                foreach (var file in directory.Files)
                {
                    token.ThrowIfCancellationRequested();
                    long processed = measured + unavailable + duplicates;
                    if (progress is not null && processed > 0 &&
                        (lastReport == 0 || Environment.TickCount64 - lastReport >= 250))
                    {
                        lastReport = Environment.TickCount64;
                        progress.Report(new(processed, root.FileCount, bytes));
                        token.ThrowIfCancellationRequested();
                    }
                    string filePath = Path.Join(directory.Path, file.Name);
                    string path = filePath;
                    if (path.Length >= 260 && path.Length > 2 && path[1] == ':')
                        path = @"\\?\" + path;
                    using var handle = CreateFile(path, 0, 7, IntPtr.Zero,
                        3, 0x00200000, IntPtr.Zero);
                    if (handle.IsInvalid ||
                        !GetBasicInfo(handle, 0, out var basic, Marshal.SizeOf<BasicInfo>()) ||
                        (basic.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                        (file.ModifiedUtc != default && file.ModifiedUtc.Ticks - fileTimeEpochTicks != basic.LastWriteTime) ||
                        !GetStandardInfo(handle, 1, out var standard, Marshal.SizeOf<StandardInfo>()) ||
                        standard.DeletePending != 0 || standard.EndOfFile != file.Bytes || standard.AllocationSize < 0)
                    {
                        unavailable++;
                        continue;
                    }

                    long allocation = standard.AllocationSize;
                    if ((basic.Attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
                    {
                        if (!GetCompressionInfo(handle, 8, out var compression, Marshal.SizeOf<CompressionInfo>()) ||
                            compression.CompressedFileSize < 0)
                        {
                            unavailable++;
                            continue;
                        }
                        allocation = compression.CompressedFileSize;
                    }

                    bool firstInMeasurement = true;
                    bool firstInFolder = true;
                    if (standard.NumberOfLinks > 1)
                    {
                        if (!GetIdentity(handle, 18, out var identity, Marshal.SizeOf<FileIdentity>()) ||
                            (identity.Low == 0 && identity.High == 0))
                        {
                            unavailable++;
                            continue;
                        }
                        firstInMeasurement = linkedFiles.Add(identity);
                        firstInFolder = folder is not null && folderLinks.Add(identity);
                    }
                    if (folder is not null && firstInFolder)
                    {
                        folderBytes = checked(folderBytes + allocation);
                        folderLogicalBytes = checked(folderLogicalBytes + standard.EndOfFile);
                    }
                    if (!firstInMeasurement)
                    {
                        duplicates++;
                        continue;
                    }
                    bytes = checked(bytes + allocation);
                    logicalBytes = checked(logicalBytes + standard.EndOfFile);
                    measured++;
                    var priority = (allocation, -measured);
                    if (largestFiles.Count < maxLargestFiles)
                        largestFiles.Enqueue(new AllocatedFile(filePath, allocation, standard.EndOfFile), priority);
                    else if (largestFiles.TryPeek(out _, out var smallest) && priority.CompareTo(smallest) > 0)
                        largestFiles.DequeueEnqueue(new AllocatedFile(filePath, allocation, standard.EndOfFile), priority);
                }
            }
            if (folder is null) return;

            var folderPriority = (folderBytes, --folderOrder);
            bool partial = unavailable != unavailableBefore || folder.SkippedCount > 0;
            var row = new AllocatedFolder(folder, folderBytes, folderLogicalBytes, partial);
            if (largestFolders.Count < maxLargestFiles)
                largestFolders.Enqueue(row, folderPriority);
            else if (largestFolders.TryPeek(out _, out var smallestFolder) && folderPriority.CompareTo(smallestFolder) > 0)
                largestFolders.DequeueEnqueue(row, folderPriority);
        }
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
    private struct BasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public FileAttributes Attributes;
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
    private static extern bool GetBasicInfo(SafeFileHandle handle, int infoClass, out BasicInfo value, int bytes);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCompressionInfo(SafeFileHandle handle, int infoClass, out CompressionInfo value, int bytes);
}
