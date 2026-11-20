using System.IO;
using System.IO.Enumeration;
using Microsoft.VisualBasic.FileIO;

namespace Discoscope.Desktop.Services;

public static class UserTempCleanup
{
    public sealed record Candidate(string Path, long Bytes, DateTime ModifiedUtc)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        internal CleanupFileGuard.FileIdentity? Identity { get; init; }
    }
    public sealed record Result(int Recycled, int Skipped, long LogicalBytes);
    public sealed record RecycleProgress(int Processed, int Total, int Recycled, int Skipped, long LogicalBytes);
    public sealed record RecycleReport(Result Result, bool Cancelled, IReadOnlyList<string> Errors)
    {
        public string? Failure { get; init; }
    }
    public sealed record Progress(long Visited, long Eligible, long LogicalBytes)
    {
        public bool Accessible { get; init; } = true;
    }
    public enum Location { User, Windows }
    public const int PreviewLimit = 1000;

    public static string GetRoot(Location location) => NormalizeRoot(location switch
    {
        Location.User => Path.GetTempPath(),
        Location.Windows => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
        _ => throw new ArgumentOutOfRangeException(nameof(location))
    });

    private static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public sealed class Inventory : IDisposable
    {
        private readonly FileStream _stream;
        private readonly List<long> _offsets;
        private bool _disposed;

        internal Inventory(string root, FileStream stream, List<long> offsets, long count, long bytes)
        {
            Root = root;
            _stream = stream;
            _offsets = offsets;
            Count = count;
            LogicalBytes = bytes;
        }

        internal string Root { get; }
        public long Count { get; }
        public long LogicalBytes { get; }
        public int PageCount => _offsets.Count;

        public IReadOnlyList<Candidate> ReadPage(int index) => ReadPage(index, CancellationToken.None);

        private IReadOnlyList<Candidate> ReadPage(int index, CancellationToken token)
        {
            lock (_stream)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (index < 0 || index >= PageCount) return [];
                using var reader = new BinaryReader(_stream, System.Text.Encoding.UTF8, leaveOpen: true);
                _stream.Position = _offsets[index];
                int count = (int)Math.Min(PreviewLimit, Count - (long)index * PreviewLimit);
                var items = new Candidate[count];
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    items[i] = new Candidate(reader.ReadString(), reader.ReadInt64(), DateTime.FromBinary(reader.ReadInt64()))
                    {
                        Identity = new CleanupFileGuard.FileIdentity(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64())
                    };
                }
                return items;
            }
        }

        public IEnumerable<IReadOnlyList<Candidate>> Pages(CancellationToken token = default)
        {
            for (int page = 0; page < PageCount; page++)
            {
                token.ThrowIfCancellationRequested();
                yield return ReadPage(page, token);
            }
        }

        public void Dispose()
        {
            lock (_stream)
            {
                if (_disposed) return;
                _disposed = true;
                _stream.Dispose();
            }
        }
    }

    public static Inventory CreateInventory(Location location = Location.User, CancellationToken token = default,
        IProgress<Progress>? progress = null) => CreateInventory(GetRoot(location), Path.GetTempPath(), token, progress);

    internal static Inventory CreateInventory(string root, string inventoryDirectory, CancellationToken token = default,
        IProgress<Progress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        root = NormalizeRoot(root);
        var path = Path.Combine(inventoryDirectory, $"discoscope-inventory-{Guid.NewGuid():N}.bin");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 4096, FileOptions.DeleteOnClose);
        try
        {
            var offsets = new List<long>();
            long count = 0;
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var summary = Scan(root, token, progress, item =>
            {
                if (count % PreviewLimit == 0) offsets.Add(stream.Position);
                writer.Write(item.Path);
                writer.Write(item.Bytes);
                writer.Write(item.ModifiedUtc.ToBinary());
                var identity = item.Identity!.Value;
                writer.Write(identity.Volume);
                writer.Write(identity.Low);
                writer.Write(identity.High);
                count++;
            }, path);
            if (!summary.Accessible) throw new IOException("L’emplacement temporaire est absent ou inaccessible.");
            writer.Flush();
            token.ThrowIfCancellationRequested();
            return new Inventory(root, stream, offsets, count, summary.LogicalBytes);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static IReadOnlyList<Candidate> Preview(Location location = Location.User, CancellationToken token = default,
        IProgress<Progress>? progress = null)
    {
        var found = new List<Candidate>();
        Scan(GetRoot(location), token, progress, item => { if (found.Count < PreviewLimit) found.Add(item); });
        return found;
    }

    public static Progress Summarize(Location location = Location.User, CancellationToken token = default,
        IProgress<Progress>? progress = null) => Scan(GetRoot(location), token, progress, null);

    private static Progress Scan(string root, CancellationToken token, IProgress<Progress>? progress, Action<Candidate>? collect,
        string? inventoryPath = null)
    {
        token.ThrowIfCancellationRequested();
        var cutoff = DateTime.UtcNow.AddDays(-7);
        long visited = 0, eligible = 0, bytes = 0;
        bool accessible = false;
        var remaining = new Stack<string>();
        remaining.Push(root);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        while (remaining.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            using var entries = OpenDirectory(directory, options);
            if (entries is null) continue;
            if (directory == root) accessible = true;
            while (MoveNext(entries, token))
            {
                var entry = entries.Current;
                if (string.Equals(entry.Path, inventoryPath, StringComparison.OrdinalIgnoreCase)) continue;
                visited++;
                if ((entry.Attributes & System.IO.FileAttributes.ReparsePoint) == 0)
                {
                    if ((entry.Attributes & System.IO.FileAttributes.Directory) != 0)
                        remaining.Push(entry.Path);
                    else if (entry.ModifiedUtc <= cutoff &&
                        CleanupFileGuard.TryReadCandidate(entry.Path, out var candidate) &&
                        candidate.ModifiedUtc <= cutoff)
                    {
                        collect?.Invoke(candidate);
                        eligible++;
                        bytes = AddBytes(bytes, candidate.Bytes);
                    }
                }
                if (visited % 2048 == 0) progress?.Report(new Progress(visited, eligible, bytes));
            }
        }
        var result = new Progress(visited, eligible, bytes) { Accessible = accessible };
        progress?.Report(result);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private readonly record struct Entry(string Path, System.IO.FileAttributes Attributes, DateTime ModifiedUtc);

    private static IEnumerator<Entry>? OpenDirectory(string directory, EnumerationOptions options)
    {
        try
        {
            // Check all ancestors, including those above the configured temporary root.
            for (string? part = directory; part is not null; part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & System.IO.FileAttributes.ReparsePoint) != 0) return null;
            return new FileSystemEnumerable<Entry>(directory, (ref FileSystemEntry entry) =>
                new Entry(entry.ToFullPath(), entry.Attributes, entry.LastWriteTimeUtc.UtcDateTime), options).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool MoveNext(IEnumerator<Entry> entries, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return entries.MoveNext(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static RecycleReport RecycleWithReport(IReadOnlyList<Candidate> candidates, Location location = Location.User,
        CancellationToken token = default, IProgress<RecycleProgress>? progress = null) =>
        RecyclePages([candidates], candidates.Count, GetRoot(location), RecycleFile, token, progress);

    public static RecycleReport RecycleInventory(Inventory inventory, Location location, CancellationToken token = default,
        IProgress<RecycleProgress>? progress = null) =>
        RecycleInventory(inventory, GetRoot(location), RecycleFile, token, progress);

    internal static RecycleReport RecycleInventory(Inventory inventory, string root, Action<string> recycle,
        CancellationToken token = default, IProgress<RecycleProgress>? progress = null)
    {
        root = NormalizeRoot(root);
        if (!string.Equals(root, inventory.Root, StringComparison.Ordinal))
            throw new InvalidOperationException("L’inventaire appartient à un autre emplacement.");
        return RecyclePages(inventory.Pages(token), inventory.Count, root, recycle, token, progress);
    }

    public static Result Recycle(IReadOnlyList<Candidate> candidates, Location location = Location.User,
        CancellationToken token = default, IProgress<RecycleProgress>? progress = null)
    {
        var report = RecycleWithReport(candidates, location, token, progress);
        if (report.Cancelled) throw new OperationCanceledException(token);
        if (report.Failure is not null) throw new IOException(report.Failure);
        return report.Result;
    }

    private static RecycleReport RecyclePages(IEnumerable<IReadOnlyList<Candidate>> pages, long total, string root,
        Action<string> recycle, CancellationToken token, IProgress<RecycleProgress>? progress)
    {
        long recycled = 0, skipped = 0, processed = 0, bytes = 0, lastReported = -1;
        var errors = new List<string>();
        string? failure = null;
        bool cancelled = false;
        try
        {
            foreach (var page in pages)
            {
                foreach (var candidate in page)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using var file = CleanupFileGuard.Open(root, candidate.Path);
                        if (file.Identity != candidate.Identity)
                            throw new IOException("Le fichier indexé a été remplacé ou son identifiant est indisponible.");
                        if (file.Length != candidate.Bytes || file.ModifiedUtc != candidate.ModifiedUtc ||
                            file.ModifiedUtc > DateTime.UtcNow.AddDays(-7))
                            throw new IOException("Fichier modifié ou devenu non éligible.");
                        token.ThrowIfCancellationRequested();
                        file.VerifyPathStillMatches(candidate.Path);
                        recycle(candidate.Path);
                        recycled++;
                        bytes = AddBytes(bytes, candidate.Bytes);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                        or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException ||
                        ex is OperationCanceledException && !token.IsCancellationRequested)
                    {
                        skipped++;
                        if (errors.Count < 100) errors.Add($"{candidate.Path} : {ex.Message}");
                    }
                    processed++;
                    if (processed % 10 == 0) Report();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; }
        catch (IOException ex) { failure = ex.Message; }
        if (lastReported != processed) Report();
        return new RecycleReport(new Result(ToInt(recycled), ToInt(skipped), bytes),
            cancelled || token.IsCancellationRequested && processed < total, errors) { Failure = failure };

        void Report()
        {
            lastReported = processed;
            progress?.Report(new RecycleProgress(ToInt(processed), ToInt(total), ToInt(recycled), ToInt(skipped), bytes));
        }
    }

    internal static void RecycleFile(string path) =>
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

    private static int ToInt(long value) => (int)Math.Min(int.MaxValue, value);
    private static long AddBytes(long total, long bytes) => total > long.MaxValue - bytes ? long.MaxValue : total + bytes;
}
