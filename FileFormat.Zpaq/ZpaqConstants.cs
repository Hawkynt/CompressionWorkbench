namespace FileFormat.Zpaq;

/// <summary>
/// Constants for the ZPAQ archive format (level 1 journaling).
/// </summary>
public static class ZpaqConstants {
  /// <summary>
  /// The 3-byte block locator prefix present at the start of every ZPAQ block ("zPQ").
  /// </summary>
  public static ReadOnlySpan<byte> BlockPrefix => "zPQ"u8;

  /// <summary>
  /// Compression level byte for ZPAQ level 1 archives.
  /// </summary>
  public const byte Level1 = 1;

  /// <summary>
  /// Compression level byte for ZPAQ level 2 archives.
  /// </summary>
  public const byte Level2 = 2;

  /// <summary>
  /// Block type byte: compressed data block (contains ZPAQL program + compressed payload).
  /// </summary>
  public const byte BlockTypeCompressed = 1;

  /// <summary>
  /// Block type byte: comment/filename header block in a journaling transaction.
  /// In a level-1 journaling archive this block carries the transaction date,
  /// filenames and per-file attributes for one transaction.
  /// </summary>
  public const byte BlockTypeHeader = (byte)'c';

  /// <summary>
  /// Block type byte: data block in a journaling transaction.
  /// Each data block holds the (ZPAQL-compressed) payload for one or more files.
  /// </summary>
  public const byte BlockTypeData = (byte)'d';

  /// <summary>
  /// Block type byte: hash/index block that closes a journaling transaction.
  /// Contains SHA-1 hashes of the uncompressed file data.
  /// </summary>
  public const byte BlockTypeIndex = (byte)'h';

  /// <summary>
  /// Minimum number of bytes in a valid block header
  /// (3-byte prefix + 1 level byte + 1 type byte = 5 bytes).
  /// </summary>
  public const int MinBlockHeaderSize = 5;

  /// <summary>
  /// Size of the Windows FILETIME timestamp stored in journaling header blocks (8 bytes).
  /// </summary>
  public const int FileTimeSize = 8;

  /// <summary>
  /// Windows FILETIME ticks (100-nanosecond intervals) from the Windows epoch
  /// (1601-01-01) to the Unix epoch (1970-01-01), used when converting timestamps.
  /// </summary>
  public const long WindowsToUnixEpochTicks = 116444736000000000L;
}
