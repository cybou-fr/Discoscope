using System.ComponentModel;
using System.Diagnostics;
using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

internal static class DiagnosticProcess
{
    public static async Task<SystemDiagnostic> RunAsync(
        string name, ProcessStartInfo start, TimeSpan timeLimit, string timeoutMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeLimit);
        try
        {
            using var process = new Process { StartInfo = start };
            if (!process.Start())
                return new(name, "Indisponible", "Impossible de démarrer la commande.", null);

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).ConfigureAwait(false);
                var report = await stdout.ConfigureAwait(false);
                var error = await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0)
                    return new(name, "Échec", $"Code de sortie : {process.ExitCode}. {error.Trim()}", report);

                return new(name, "Analysé", "Rapport en lecture seule.", report);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process exited before the kill request.
                }
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(name, "Délai dépassé", timeoutMessage, null);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(name, "Indisponible", ex.Message, null);
        }
    }
}
