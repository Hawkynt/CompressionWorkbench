namespace Compression.Registry;

/// <summary>
/// Describes a compression method available within a format.
/// </summary>
/// <param name="Name">Internal method name (e.g. "deflate", "lzma").</param>
/// <param name="DisplayName">Human-readable name (e.g. "Deflate", "LZMA").</param>
/// <param name="SupportsOptimize">Whether compression-specific "method+" parameter optimization is available. This is not a generic maintenance capability.</param>
public sealed record FormatMethodInfo(string Name, string DisplayName, bool SupportsOptimize = false);
