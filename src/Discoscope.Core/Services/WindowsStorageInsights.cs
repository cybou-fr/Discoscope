using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

/// <summary>
/// Read-only, non-overlapping Windows storage inventory based on the existing scan.
/// Sizes are logical, not estimates of reclaimable physical disk space.
/// </summary>
public static class WindowsStorageInsights
{
    public static IReadOnlyList<StorageInsight> Analyze(ScanDirectory? root, CancellationToken token = default)
    {
        if (root is null) return [];
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new (string Category, string Path, string Guidance, string Risk)[]
        {
            ("Fichiers temporaires utilisateur", Path.GetTempPath(),
                "Examiner les fichiers anciens et fermer les applications avant toute suppression.", "À vérifier"),
            ("Fichiers temporaires Windows", Path.Combine(windows, "Temp"),
                "Certains fichiers peuvent être utilisés par Windows ou par des installations.", "À vérifier"),
            ("Téléchargements", Path.Combine(profile, "Downloads"),
                "Fichiers personnels : ne jamais supprimer automatiquement.", "Personnel"),
            ("Windows Update", Path.Combine(windows, "SoftwareDistribution", "Download"),
                "Utiliser les outils de nettoyage Windows plutôt qu'une suppression directe.", "Système"),
            ("Optimisation de la distribution", Path.Combine(windows, "SoftwareDistribution", "DeliveryOptimization"),
                "Utiliser les paramètres de stockage Windows pour nettoyer le cache.", "Système"),
            ("Corbeille", Path.Combine(Path.GetPathRoot(root.Path) ?? root.Path, "$Recycle.Bin"),
                "Vider la corbeille uniquement après vérification des éléments.", "À vérifier")
        };
        var paths = new Dictionary<string, (string Category, string Guidance, string Risk)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(item.Path))
                paths.TryAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path)),
                    (item.Category, item.Guidance, item.Risk));
        }
        var found = new List<StorageInsight>();
        var stack = new Stack<ScanDirectory>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = stack.Pop();
            if (paths.TryGetValue(Path.TrimEndingDirectorySeparator(directory.Path), out var category))
            {
                found.Add(new StorageInsight(category.Category, directory.Path, directory.Bytes,
                    directory.FileCount, category.Guidance, category.Risk));
                continue; // Do not double-count a nested category.
            }
            foreach (var child in directory.Directories)
                stack.Push(child);
        }
        return found.OrderByDescending(x => x.LogicalBytes).ToArray();
    }
}
