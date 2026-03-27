namespace FileFormat.DiskDoubler;

/// <summary>
/// Constants for the DiskDoubler compressed file format (Salient Software, 1989-1993).
/// </summary>
internal static class DiskDoublerConstants {
  /// <summary>Size of the fixed-length DiskDoubler file header in bytes.</summary>
  public const int HeaderSize = 82;

  /// <summary>Compression method: stored (no compression).</summary>
  public const byte MethodStored = 0;

  /// <summary>Compression method: simple RLE.</summary>
  public const byte MethodRle = 1;

  /// <summary>Compression method: LZC variant (proprietary).</summary>
  public const byte MethodLzc = 3;
}
