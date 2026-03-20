namespace FileFormat.StuffIt;

internal static class StuffItConstants {
  // Archive magic values.
  public const uint MagicSit  = 0x53495421; // "SIT!" — classic StuffIt 1-4
  public const uint MagicStuf = 0x53747566; // "Stuf" — StuffIt 5+

  // Archive header layout (classic "SIT!" format).
  public const int ArchiveHeaderSize      = 22;
  public const uint ArchiveSignatureRLau  = 0x724C6175; // "rLau"

  // Entry header layout — 112 bytes total.
  public const int EntryHeaderSize        = 112;
  public const int FileNameOffset         = 3;   // offset of name bytes within header
  public const int FileNameMaxLength      = 63;

  // Compression method codes.
  public const byte MethodStore           = 0;
  public const byte MethodRle             = 1;
  public const byte MethodLzc            = 2;   // 14-bit LZW — not supported
  public const byte MethodHuffman        = 3;   // not supported
  public const byte MethodLzah           = 5;   // LZ + adaptive Huffman — not supported
  public const byte MethodFixedHuffman   = 6;   // MW — not supported
  public const byte MethodMwImproved     = 8;   // not supported
  public const byte MethodLzc12          = 13;  // 12-bit LZW — not supported
  public const byte MethodLzc12Rle       = 14;  // 12-bit LZW + RLE — not supported
  public const byte MethodEncrypted      = 15;  // DES — not supported

  // RLE escape byte used by method 1.
  public const byte RleMarker            = 0x90;

  // Mac epoch: January 1, 1904 00:00:00 UTC.
  public static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  // CRC-16/CCITT polynomial (normal, non-reflected) — used for StuffIt entry checksums.
  // The reflected form of 0x1021 is 0x8408, used by ARC.
  // StuffIt uses the forward (non-reflected) variant with init=0.
  public const ushort Crc16Polynomial    = 0x1021;
}
