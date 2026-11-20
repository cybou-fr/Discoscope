using System.Diagnostics;
using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

/// <summary>Runs DISM's read-only component-store analysis. Never performs cleanup.</summary>
public sealed class WindowsComponentInspector
{
    public Task<SystemDiagnostic> InspectAsync(CancellationToken cancellationToken)
    {
        const string name = "Magasin de composants Windows";
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new SystemDiagnostic(name, "Non pris en charge", "Disponible uniquement sous Windows.", null));

        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe"),
            Arguments = "/Online /Cleanup-Image /AnalyzeComponentStore /English",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        return DiagnosticProcess.RunAsync(name, start, TimeSpan.FromMinutes(4),
            "DISM n’a pas répondu dans les quatre minutes.", cancellationToken);
    }
}
