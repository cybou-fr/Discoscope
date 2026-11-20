using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Discoscope.Desktop.Services;

internal sealed class CleanupFileGuard : IDisposable
{
    private const uint OpenExisting = 3;
    private const uint GenericRead = 0x80000000;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private readonly List<SafeFileHandle> _handles = [];

    public long Length { get; private set; }
    public DateTime ModifiedUtc { get; private set; }

    private CleanupFileGuard() { }

    public static CleanupFileGuard Open(string root, string path)
    {
        string? volume = Path.GetPathRoot(root);
        if (volume is not { Length: 3 } || volume[1] != ':' || root.Length <= volume.Length ||
            new DriveInfo(volume).DriveType != DriveType.Fixed ||
            !Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal) ||
            !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Hors emplacement local autorisé.");

        var segments = path[volume.Length..].Split(Path.DirectorySeparatorChar);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (string segment in segments)
            if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(invalid) >= 0)
                throw new IOException("Chemin de fichier non autorisé.");

        var guard = new CleanupFileGuard();
        try
        {
            string part = volume;
            // Pin ancestors top-down without sharing deletion, and inspect each reparse point without following it.
            for (int i = 0; i < segments.Length; i++)
            {
                part = Path.Join(part, segments[i]);
                bool directory = i < segments.Length - 1;
                var handle = OpenHandle(part, directory);
                guard._handles.Add(handle);
                var attributes = File.GetAttributes(handle);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    ((attributes & FileAttributes.Directory) != 0) != directory)
                    throw new IOException("Lien, point de réanalyse ou type de chemin non autorisé.");
                if (!directory)
                {
                    guard.Length = RandomAccess.GetLength(handle);
                    guard.ModifiedUtc = File.GetLastWriteTimeUtc(handle);
                }
            }
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenHandle(string path, bool directory)
    {
        var share = directory ? FileShare.ReadWrite : FileShare.Read | FileShare.Delete;
        var handle = CreateFile(@"\\?\" + path, GenericRead, share, IntPtr.Zero, OpenExisting,
            OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }

    public void Dispose()
    {
        for (int i = _handles.Count - 1; i >= 0; i--)
            _handles[i].Dispose();
        _handles.Clear();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
