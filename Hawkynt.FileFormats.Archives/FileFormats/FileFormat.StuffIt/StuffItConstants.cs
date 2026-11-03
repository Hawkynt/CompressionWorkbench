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
  public const int FileNameMaxLength      = 31;  // bytes 34-35 follow the name field

  // Compression method codes written by StuffItWriter (the reader names all of them, see StuffItReader.MethodName).
  public const byte MethodStore           = 0;
  public const byte MethodRle             = 1;

  // Mac epoch: January 1, 1904 00:00:00 UTC.
  public static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
