using Discoscope.Core.Models;
namespace Discoscope.Core.Services;
public sealed class VolumeInspector
{
    public VolumeSnapshot Inspect(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new ArgumentException("Chemin de lecteur invalide.", nameof(path));
        var drive = new DriveInfo(root);
        if (!drive.IsReady) throw new IOException("Le lecteur n'est pas prêt.");
        return new VolumeSnapshot(root, drive.TotalSize, drive.TotalFreeSpace, drive.AvailableFreeSpace);
    }
}
