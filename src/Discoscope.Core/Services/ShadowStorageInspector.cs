using System.ComponentModel;
using System.Diagnostics;
using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

/// <summary>
/// Read-only VSS shadow storage report. This is not a list of deletable backups.
/// </summary>
public sealed class ShadowStorageInspector
{
    public async Task<SystemDiagnostic> InspectAsync(CancellationToken cancellationToken)
    {
        const string name = "Stockage des clichés instantanés (VSS)";
        if (!OperatingSystem.IsWindows())
            return new(name, "Non pris en charge", "Disponible uniquement sous Windows.", null);

        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vssadmin.exe");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "list shadowstorage",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var process = new Process { StartInfo = start };
            if (!process.Start())
                return new(name, "Indisponible", "Impossible de démarrer vssadmin.", null);

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var report = await stdout;
                var error = await stderr;
                if (process.ExitCode != 0)
                    return new(name, "Échec", $"vssadmin : code {process.ExitCode}. {error.Trim()}", report);
                return new(name, "Analysé", "Rapport VSS en lecture seule. Aucun cliché n'a été modifié.", report);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(name, "Délai dépassé", "La commande VSS a dépassé 45 secondes.", null);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new(name, "Indisponible", ex.Message, null);
        }
    }
}
