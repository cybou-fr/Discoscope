using System.Diagnostics;
using System.Globalization;
using System.Text;
using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

/// <summary>
/// Read-only VSS shadow storage report. This is not a list of deletable backups.
/// </summary>
public sealed class ShadowStorageInspector
{
    public Task<SystemDiagnostic> InspectAsync(CancellationToken cancellationToken)
    {
        const string name = "Stockage des clichés instantanés (VSS)";
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new SystemDiagnostic(name, "Non pris en charge", "Disponible uniquement sous Windows.", null));

        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vssadmin.exe");
        var outputEncoding = CodePagesEncodingProvider.Instance.GetEncoding(CultureInfo.InstalledUICulture.TextInfo.OEMCodePage);
        var start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "list shadowstorage",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding
        };

        return DiagnosticProcess.RunAsync(name, start, TimeSpan.FromSeconds(45),
            "La commande VSS a dépassé 45 secondes.", cancellationToken);
    }
}
