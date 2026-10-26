namespace FileFormat.DiskDoubler;

/// <summary>
/// Constants for the DiskDoubler compressed file format (Salient Software, 1989-1993).
/// </summary>
internal static class DiskDoublerConstants {
  /// <summary>Magic at the start of a single compressed file (and of every file header inside a DiskDoubler archive).</summary>
  public const uint Magic = 0xABCD0054;

  /// <summary>Size of the single-file header in bytes, magic and header CRC included.</summary>
  public const int HeaderSize = 84;

  /// <summary>Bytes covered by the header CRC (everything before it).</summary>
  public const int HeaderCrcSpan = 82;

  /// <summary>Size of the MacBinary header that commonly wraps a DiskDoubler file.</summary>
  public const int MacBinaryHeaderSize = 128;

  /// <summary>Compression method: stored (no compression).</summary>
  public const int MethodStored = 0;

  /// <summary>Compression method: Unix <c>compress</c> LZW.</summary>
  public const int MethodCompress = 1;

  /// <summary>Compression method: "AD" / "ADS" LZSS blocks (DiskDoubler 3.x/4.x, AutoDoubler).</summary>
  public const int MethodAds = 6;

  /// <summary>Compression method: Compact Pro (LZH + RLE).</summary>
  public const int MethodCompactPro = 8;

  /// <summary>Compression method: "AD1" LZSS blocks, same block format as <see cref="MethodAds"/>.</summary>
  public const int MethodAd1 = 9;

  /// <summary>Seconds between the Macintosh epoch (1904-01-01) and the Unix epoch.</summary>
  public const long MacEpochOffsetSeconds = 2_082_844_800;
}
