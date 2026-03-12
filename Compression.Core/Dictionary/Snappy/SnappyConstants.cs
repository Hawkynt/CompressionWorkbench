namespace Compression.Core.Dictionary.Snappy;

/// <summary>
/// Constants for Snappy block compression.
/// </summary>
public static class SnappyConstants {
  /// <summary>Literal tag (bits 1:0 = 00).</summary>
  public const int TagLiteral = 0x00;

  /// <summary>Copy with 1-byte offset tag (bits 1:0 = 01).</summary>
  public const int TagCopy1 = 0x01;

  /// <summary>Copy with 2-byte offset tag (bits 1:0 = 10).</summary>
  public const int TagCopy2 = 0x02;

  /// <summary>Copy with 4-byte offset tag (bits 1:0 = 11).</summary>
  public const int TagCopy4 = 0x03;

  /// <summary>Maximum offset for copy-1 (11 bits).</summary>
  public const int MaxCopy1Offset = 2047;

  /// <summary>Maximum offset for copy-2 (16 bits).</summary>
  public const int MaxCopy2Offset = 65535;

  /// <summary>Minimum match length.</summary>
  public const int MinMatch = 4;

  /// <summary>Maximum match length for copy-1/copy-2.</summary>
  public const int MaxMatchLength = 64;

  /// <summary>Hash table bits.</summary>
  public const int HashTableBits = 14;

  /// <summary>Hash table size.</summary>
  public const int HashTableSize = 1 << SnappyConstants.HashTableBits;

  /// <summary>Snappy framing format magic chunk identifier.</summary>
  public static readonly byte[] StreamIdentifier = "sNaPpY"u8.ToArray();
}
