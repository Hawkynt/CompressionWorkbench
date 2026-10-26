namespace FileFormat.PackIt;

/// <summary>
/// Constants for the PackIt (.pit) classic Macintosh archive format (Harry Chesley, 1984).
/// </summary>
internal static class PackItConstants {
  /// <summary>Entry magic for a stored (uncompressed) entry: "PMag".</summary>
  public static ReadOnlySpan<byte> MagicStored => "PMag"u8;

  /// <summary>Entry magic for a Huffman-compressed entry: "PMa4".</summary>
  public static ReadOnlySpan<byte> MagicCompressed => "PMa4"u8;

  /// <summary>Entry magic for a Huffman-compressed entry XOR-encrypted with the password: "PMa5".</summary>
  public static ReadOnlySpan<byte> MagicXorEncrypted => "PMa5"u8;

  /// <summary>Entry magic for a Huffman-compressed entry DES-encrypted with the password: "PMa6".</summary>
  public static ReadOnlySpan<byte> MagicDesEncrypted => "PMa6"u8;

  /// <summary>End-of-archive marker that follows the last entry: "PEnd".</summary>
  public static ReadOnlySpan<byte> MagicEnd => "PEnd"u8;

  /// <summary>Size of the entry header that follows the magic, in bytes.</summary>
  public const int HeaderSize = 94;

  /// <summary>Bytes of the header covered by the header CRC (everything before the CRC itself).</summary>
  public const int HeaderCrcSpan = 92;

  /// <summary>Capacity of the filename field (after its length byte).</summary>
  public const int FileNameMaxLength = 63;

  /// <summary>Seconds between the Macintosh epoch (1904-01-01) and the Unix epoch.</summary>
  public const long MacEpochOffsetSeconds = 2_082_844_800;
}
