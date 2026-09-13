namespace FileFormat.Zstd;

/// <summary>
/// Compression level for Zstandard.
/// </summary>
public enum ZstdCompressionLevel {
  /// <summary>Fastest compression: chain depth 4.</summary>
  Fastest = 1,

  /// <summary>Fast compression: chain depth 16.</summary>
  Fast = 3,

  /// <summary>Default compression: chain depth 64.</summary>
  Default = 6,

  /// <summary>Best compression: chain depth 128.</summary>
  Best = 9
}
