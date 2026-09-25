namespace Compression.Registry;

/// <summary>
/// Describes a compression method available within a format.
/// </summary>
/// <param name="Name">Internal method name (e.g. "deflate", "lzma").</param>
/// <param name="DisplayName">Human-readable name (e.g. "Deflate", "LZMA").</param>
/// <param name="SupportsOptimize">Legacy UI hint that this compression method has an
/// optimal/stronger encoder path. It is not a format capability; format-level dispatch
/// is defined by <see cref="ICompressionOptimizable"/>.</param>
public sealed record FormatMethodInfo(string Name, string DisplayName, bool SupportsOptimize = false);
