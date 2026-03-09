namespace Compression.Core.BitIO;

/// <summary>
/// Specifies the order in which bits are read from or written to a byte.
/// </summary>
public enum BitOrder {
  /// <summary>
  /// Least significant bit first. Used by Deflate, ZIP, GZIP, PNG.
  /// </summary>
  LsbFirst,

  /// <summary>
  /// Most significant bit first. Used by JPEG, bzip2, many legacy formats.
  /// </summary>
  MsbFirst
}
