using System.Runtime.InteropServices;
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
        return new VolumeSnapshot(root, drive.TotalSize, drive.TotalFreeSpace, drive.AvailableFreeSpace)
        {
            SubstitutedPath = FindSubstitution(root)
        };
    }

    private static string? FindSubstitution(string root)
    {
        if (!OperatingSystem.IsWindows() || root.Length != 3 || root[1] != ':') return null;
        var buffer = new char[32768];
        if (QueryDosDevice(root[..2], buffer, (uint)buffer.Length) == 0) return null;
        int end = Array.IndexOf(buffer, '\0');
        return end > 0 ? ParseSubstitution(buffer.AsSpan(0, end)) : null;
    }

    internal static string? ParseSubstitution(ReadOnlySpan<char> target)
    {
        const string dosPrefix = @"\??\";
        if (!target.StartsWith(dosPrefix, StringComparison.Ordinal)) return null;
        var path = target[dosPrefix.Length..];
        return path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
            ? path.ToString() : null;
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint QueryDosDevice(string device, [Out] char[] target, uint capacity);
}
