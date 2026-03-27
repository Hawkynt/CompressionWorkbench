namespace FileFormat.PackIt;

/// <summary>
/// Constants for the PackIt (.pit) classic Macintosh archive format (Harry Chesley, 1984).
/// </summary>
internal static class PackItConstants {
  /// <summary>Entry magic for a stored (uncompressed) entry: "PMag".</summary>
  public static ReadOnlySpan<byte> MagicStored => "PMag"u8;

  /// <summary>Entry magic for a Huffman-compressed entry: "PMa4".</summary>
  public static ReadOnlySpan<byte> MagicCompressed => "PMa4"u8;

  /// <summary>Size of the per-entry header in bytes (magic + filename field + metadata).</summary>
  public const int EntryHeaderSize = 87;

  /// <summary>Maximum filename length in a PackIt entry (Pascal string capacity minus length byte).</summary>
  public const int FileNameMaxLength = 62;
}
