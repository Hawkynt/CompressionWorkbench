namespace Compression.Registry;

/// <summary>
/// Options for archive/stream creation, passed from the orchestration layer to format descriptors.
/// </summary>
public sealed class FormatCreateOptions {
  /// <summary>Encryption password.</summary>
  public string? Password { get; init; }

  /// <summary>Compression method name (e.g. "deflate", "lzma").</summary>
  public string? MethodName { get; init; }

  /// <summary>Whether "+" optimization was requested.</summary>
  public bool Optimize { get; init; }

  /// <summary>Compression level (0-9), or null for format default.</summary>
  public int? Level { get; init; }

  /// <summary>Dictionary size in bytes, or 0 for format default.</summary>
  public long DictSize { get; init; }

  /// <summary>Word size / fast bytes, or null for format default.</summary>
  public int? WordSize { get; init; }

  /// <summary>Number of parallel threads.</summary>
  public int Threads { get; init; } = 1;

  /// <summary>Maximum solid block size in bytes.</summary>
  public long SolidSize { get; init; }

  /// <summary>Whether to compress all files regardless of entropy detection.</summary>
  public bool ForceCompress { get; init; }

  /// <summary>When true, encrypt file names/headers.</summary>
  public bool EncryptFilenames { get; init; }

  /// <summary>Encryption method override (e.g. "aes256", "zipcrypto").</summary>
  public string? EncryptionMethod { get; init; }

  /// <summary>Set of file paths detected as incompressible (null = not computed).</summary>
  public HashSet<string>? IncompressiblePaths { get; init; }
}
