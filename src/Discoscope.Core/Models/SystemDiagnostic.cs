namespace Discoscope.Core.Models;
public sealed record SystemDiagnostic(string Name, string Status, string Details, string? RawOutput);
