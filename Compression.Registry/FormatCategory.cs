namespace Compression.Registry;

/// <summary>
/// Classifies a format by its primary behavior.
/// </summary>
public enum FormatCategory {
  /// <summary>Multi-file container (ZIP, TAR, 7z, etc.).</summary>
  Archive,
  /// <summary>Single-stream compressor (Gzip, Bzip2, Xz, etc.).</summary>
  Stream,
  /// <summary>Encoding wrapper (MacBinary, BinHex).</summary>
  Wrapper,
  /// <summary>Auto-generated tar + stream combination (tar.gz, tar.bz2, etc.).</summary>
  CompoundTar,
  /// <summary>Recognized by signature only, no operations (ISO, UDF).</summary>
  DetectionOnly,
}
