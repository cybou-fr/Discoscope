using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

/// <summary>Windows storage categories measured from the scan snapshot.</summary>
public static class WindowsStorageInsights
{
    public static IReadOnlyList<StorageInsight> Analyze(ScanDirectory? root, CancellationToken token = default)
    {
        if (root is null) return [];
        var volume = Path.GetPathRoot(root.Path) ?? root.Path;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new (string Name, string Path, string Guidance, bool IsFile)[]
        {
            ("Temporaires utilisateur", Path.GetTempPath(), "Aperçu et déplacement vers la Corbeille dans Nettoyage.", false),
            ("Temporaires Windows", Path.Combine(windows, "Temp"), "Nettoyage des fichiers éligibles si les droits le permettent.", false),
            ("Téléchargements", Path.Combine(profile, "Downloads"), "Fichiers personnels. Vérifier dans l’Explorateur.", false),
            ("Profil utilisateur", profile, "Inclut les téléchargements et les données d’applications.", false),
            ("Données d’applications locales", localData, "Utiliser les options de l’application concernée.", false),
            ("Données d’applications itinérantes", roamingData, "Utiliser les options de l’application concernée.", false),
            ("Windows Update", Path.Combine(windows, "SoftwareDistribution", "Download"), "Utiliser les paramètres de stockage Windows.", false),
            ("Optimisation de la distribution", Path.Combine(windows, "SoftwareDistribution", "DeliveryOptimization"), "Utiliser les paramètres de stockage Windows.", false),
            ("Corbeille", Path.Combine(volume, "$Recycle.Bin"), "Consulter les éléments dans la Corbeille Windows.", false),
            ("Fichier de veille prolongée", Path.Combine(volume, "hiberfil.sys"), "Géré par les paramètres d’alimentation Windows.", true),
            ("Fichier d’échange", Path.Combine(volume, "pagefile.sys"), "Géré par la mémoire virtuelle de Windows.", true)
        };

        var results = new List<StorageInsight>();
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(candidate.Path)) continue;

            var path = Path.TrimEndingDirectorySeparator(candidate.Path);
            var relative = Path.GetRelativePath(root.Path, path);
            bool outside = Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            if (outside)
            {
                Add(0, 0, "Hors périmètre", false);
                continue;
            }

            var directory = TemporaryFilesInspector.FindDirectory(root,
                candidate.IsFile ? Path.GetDirectoryName(path)! : path, token);
            if (candidate.IsFile && directory is not null)
            {
                var name = Path.GetFileName(path);
                var found = false;
                foreach (var file in directory.Files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!file.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    Add(file.Bytes, 1, "Mesuré", true);
                    found = true;
                    break;
                }
                if (!found) Add(0, 0, "Non relevé", false);
            }
            else if (!candidate.IsFile && directory is { EnumerationSucceeded: true })
                Add(directory.Bytes, directory.FileCount, directory.SkippedCount > 0 ? "Partiel" : "Mesuré", true);
            else
                Add(0, 0, "Non relevé", false);

            void Add(long bytes, long count, string status, bool measured) =>
                results.Add(new StorageInsight(candidate.Name, path, bytes, count, candidate.Guidance, status)
                    { IsMeasured = measured });
        }
        return results.OrderByDescending(row => row.IsMeasured).ThenByDescending(row => row.LogicalBytes).ToArray();
    }
}
