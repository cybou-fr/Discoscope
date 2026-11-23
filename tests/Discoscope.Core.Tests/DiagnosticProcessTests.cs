using System.Diagnostics;
using Discoscope.Core.Services;

namespace Discoscope.Core.Tests;

public sealed class DiagnosticProcessTests
{
    [Fact]
    public async Task Run_CapturesOutputErrorsAndExitCodeOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var result = await DiagnosticProcess.RunAsync("Test", Command("/d /c \"echo report & echo details 1>&2 & exit /b 2\""),
            TimeSpan.FromSeconds(10), "Délai dépassé.", CancellationToken.None);

        Assert.Equal("Échec", result.Status);
        Assert.Contains("2", result.Details);
        Assert.Contains("details", result.Details);
        Assert.Contains("report", result.RawOutput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_DistinguishesCancellationFromTimeoutOnWindows(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) return;

        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var cancellation = new CancellationTokenSource();
        var shortLimit = TimeSpan.FromMilliseconds(300);
        if (cancel) cancellation.CancelAfter(shortLimit);
        var result = DiagnosticProcess.RunAsync("Test", start, cancel ? TimeSpan.FromSeconds(10) : shortLimit,
            "Délai de test.", cancellation.Token);

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        else
            Assert.Equal("Délai dépassé", (await result).Status);
    }

    [Fact]
    public async Task Run_WaitsForDiagnosticProcessToExitAfterCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;

        var directory = Directory.CreateTempSubdirectory("DiscoscopeDiagnostic-");
        try
        {
            string marker = Path.Combine(directory.FullName, "pid.txt");
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"[IO.File]::WriteAllText('{marker.Replace("'", "''")}', [string]$PID); Start-Sleep -Seconds 30");
            using var cancellation = new CancellationTokenSource();
            var running = DiagnosticProcess.RunAsync("Test", start, TimeSpan.FromSeconds(30), "", cancellation.Token);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                int pid = 0;
                while (!File.Exists(marker) || !int.TryParse(await File.ReadAllTextAsync(marker, deadline.Token), out pid))
                    await Task.Delay(25, deadline.Token);
                using var process = Process.GetProcessById(pid);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
                Assert.True(process.HasExited);
            }
            finally
            {
                cancellation.Cancel();
                try { await running; }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ProcessStartInfo Command(string arguments) => new()
    {
        FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        Arguments = arguments,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
}
