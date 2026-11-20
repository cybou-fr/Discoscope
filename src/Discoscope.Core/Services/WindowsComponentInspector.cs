using System.Diagnostics;
using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

/// <summary>Runs DISM's read-only component-store analysis. Never performs cleanup.</summary>
public sealed class WindowsComponentInspector
{
    public async Task<SystemDiagnostic> InspectAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return new("Magasin de composants Windows", "Non pris en charge", "Windows uniquement.", null);

        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe"),
            Arguments = "/Online /Cleanup-Image /AnalyzeComponentStore /English",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        try
        {
            using var process = new Process { StartInfo = start };
            if (!process.Start())
                return new("Magasin de composants Windows", "Indisponible", "Impossible de démarrer DISM.", null);

            // Read both streams concurrently to avoid process pipe deadlocks.
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var output = await outputTask;
                var error = await errorTask;
                if (process.ExitCode != 0)
                    return new("Magasin de composants Windows", "Échec", $"DISM : code {process.ExitCode}. {error.Trim()}", output);
                return new("Magasin de composants Windows", "Analysé", "Rapport DISM en lecture seule.", output);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("Magasin de composants Windows", "Délai dépassé", "DISM n'a pas répondu dans les quatre minutes.", null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new("Magasin de composants Windows", "Indisponible", ex.Message, null);
        }
    }
}
