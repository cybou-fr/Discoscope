using System.IO;
using Microsoft.VisualBasic.FileIO;
namespace Discoscope.Desktop.Services;
public static class UserTempCleanup
{
    public sealed record Candidate(string Path, long Bytes, DateTime ModifiedUtc);
    public sealed record Result(int Recycled, int Skipped, long LogicalBytes);
    private static string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
    private static bool Allowed(string path)
    {
        var root = Root;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return false;
        path = Path.GetFullPath(path);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        var parent = Path.GetDirectoryName(path);
        while (parent is not null)
        {
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return false;
            if (parent.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
            parent = Path.GetDirectoryName(parent);
        }
        return false;
    }
    public static IReadOnlyList<Candidate> Preview()
    {
        var found = new List<Candidate>();
        if (!Directory.Exists(Root) || (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0) return found;
        var stack = new Stack<string>();
        stack.Push(Root);
        while (stack.Count > 0 && found.Count < 500)
        {
            var directory = stack.Pop();
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (found.Count >= 500) break;
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0 || !Allowed(path)) continue;
                        if ((attributes & FileAttributes.Directory) != 0) { stack.Push(path); continue; }
                        var info = new FileInfo(path);
                        if (info.LastWriteTimeUtc <= DateTime.UtcNow.AddDays(-7))
                            found.Add(new Candidate(info.FullName, info.Length, info.LastWriteTimeUtc));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }
    public static Result Recycle(IReadOnlyList<Candidate> candidates, CancellationToken token = default)
    {
        int recycled = 0, skipped = 0;
        long bytes = 0;
        foreach (var item in candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Allowed(item.Path)) { skipped++; continue; }
                var info = new FileInfo(item.Path);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    info.Length != item.Bytes || info.LastWriteTimeUtc != item.ModifiedUtc ||
                    info.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7)) { skipped++; continue; }
                FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                recycled++;
                bytes += item.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                skipped++;
            }
        }
        return new Result(recycled, skipped, bytes);
    }
}
