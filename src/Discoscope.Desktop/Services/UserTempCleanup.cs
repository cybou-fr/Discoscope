using System.IO;
using Microsoft.VisualBasic.FileIO;
namespace Discoscope.Desktop.Services;
public static class UserTempCleanup
{
    public sealed record Candidate(string Path, long Bytes, DateTime ModifiedUtc);
    public sealed record Result(int Recycled, int Skipped, long LogicalBytes);
    public sealed record RecycleProgress(int Processed, int Total, int Recycled, int Skipped, long LogicalBytes);
    public sealed record RecycleReport(Result Result, bool Cancelled, IReadOnlyList<string> Errors);
    public sealed record Progress(long Visited, int Eligible, long LogicalBytes);
    public enum Location { User, Windows }
    public const int PreviewLimit = 1000;
    public static string GetRoot(Location location) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(location == Location.User
            ? Path.GetTempPath()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")));
    private static bool Allowed(string path, string root)
    {
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
    // Disk-backed inventory: only one page of file metadata is retained in UI memory.
    public sealed class Inventory : IDisposable
    {
        private readonly string _path;
        private readonly List<long> _offsets;
        internal Inventory(string path, List<long> offsets, long count, long bytes)
        { _path = path; _offsets = offsets; Count = count; LogicalBytes = bytes; }
        public long Count { get; }
        public long LogicalBytes { get; }
        public int PageCount => _offsets.Count;
        public IReadOnlyList<Candidate> ReadPage(int index)
        {
            if (index < 0 || index >= _offsets.Count) return Array.Empty<Candidate>();
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            stream.Position = _offsets[index];
            var items = new List<Candidate>(PreviewLimit);
            for (int i = 0; i < PreviewLimit && stream.Position < stream.Length; i++)
                items.Add(new Candidate(reader.ReadString(), reader.ReadInt64(), DateTime.FromBinary(reader.ReadInt64())));
            return items;
        }
        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static Inventory CreateInventory(Location location = Location.User, CancellationToken token = default, IProgress<Progress>? progress = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "discoscope-inventory-" + Guid.NewGuid().ToString("N") + ".bin");
        var offsets = new List<long>();
        long count = 0;
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                var summary = Scan(location, token, progress, item =>
                {
                    if (count % PreviewLimit == 0) offsets.Add(stream.Position);
                    writer.Write(item.Path);
                    writer.Write(item.Bytes);
                    writer.Write(item.ModifiedUtc.ToBinary());
                    count++;
                });
                return new Inventory(path, offsets, count, summary.LogicalBytes);
            }
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static IReadOnlyList<Candidate> Preview(Location location = Location.User, CancellationToken token = default, IProgress<Progress>? progress = null)
    {
        var found = new List<Candidate>();
        Scan(location, token, progress, item => { if (found.Count < PreviewLimit) found.Add(item); });
        return found;
    }

    // A full inventory without retaining paths: suitable for very large temporary folders.
    public static Progress Summarize(Location location = Location.User, CancellationToken token = default, IProgress<Progress>? progress = null) =>
        Scan(location, token, progress, null);

    private static Progress Scan(Location location, CancellationToken token, IProgress<Progress>? progress, Action<Candidate>? collect)
    {
        var root = GetRoot(location);
        var cutoff = DateTime.UtcNow.AddDays(-7);
        long visited = 0;
        long eligible = 0;
        long logicalBytes = 0;
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            return new Progress(0, 0, 0);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = stack.Pop();
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    visited++;
                    if (visited % 2048 == 0)
                        progress?.Report(new Progress(visited, eligible, logicalBytes));
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0 || !Allowed(path, root)) continue;
                        if ((attributes & FileAttributes.Directory) != 0) { stack.Push(path); continue; }
                        var info = new FileInfo(path);
                        if (info.LastWriteTimeUtc <= cutoff)
                        {
                            eligible++;
                            logicalBytes = logicalBytes > long.MaxValue - info.Length ? long.MaxValue : logicalBytes + info.Length;
                            collect?.Invoke(new Candidate(info.FullName, info.Length, info.LastWriteTimeUtc));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var result = new Progress(visited, eligible, logicalBytes);
        progress?.Report(result);
        return result;
    }

    // Return partial results on cancellation instead of losing the count of files already moved.
    public static RecycleReport RecycleWithReport(IReadOnlyList<Candidate> candidates,
        Location location = Location.User, CancellationToken token = default,
        IProgress<RecycleProgress>? progress = null)
    {
        var root = GetRoot(location);
        int recycled = 0, skipped = 0, processed = 0;
        long bytes = 0;
        var errors = new List<string>();
        bool cancelled = false;
        foreach (var item in candidates)
        {
            if (token.IsCancellationRequested) { cancelled = true; break; }
            try
            {
                if (!Allowed(item.Path, root))
                {
                    skipped++;
                    if (errors.Count < 100) errors.Add($"Hors emplacement autorisé : {item.Path}");
                    continue;
                }
                var info = new FileInfo(item.Path);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    info.Length != item.Bytes || info.LastWriteTimeUtc != item.ModifiedUtc ||
                    info.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7))
                {
                    skipped++;
                    if (errors.Count < 100) errors.Add($"Fichier modifié, absent ou non éligible : {item.Path}");
                    continue;
                }
                FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                recycled++;
                bytes = bytes > long.MaxValue - item.Bytes ? long.MaxValue : bytes + item.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                skipped++;
                if (errors.Count < 100) errors.Add($"{item.Path} : {ex.Message}");
            }
            finally
            {
                processed++;
                if (processed % 10 == 0 || processed == candidates.Count)
                    progress?.Report(new RecycleProgress(processed, candidates.Count, recycled, skipped, bytes));
            }
        }
        progress?.Report(new RecycleProgress(processed, candidates.Count, recycled, skipped, bytes));
        return new RecycleReport(new Result(recycled, skipped, bytes), cancelled, errors);
    }

    public static Result Recycle(IReadOnlyList<Candidate> candidates, Location location = Location.User,
        CancellationToken token = default, IProgress<RecycleProgress>? progress = null)
    {
        var report = RecycleWithReport(candidates, location, token, progress);
        if (report.Cancelled) throw new OperationCanceledException(token);
        return report.Result;
    }
}
