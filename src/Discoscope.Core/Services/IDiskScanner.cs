using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

public interface IDiskScanner
{
    Task<ScanResult> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}
