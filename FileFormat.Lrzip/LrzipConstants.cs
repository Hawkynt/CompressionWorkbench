#pragma warning disable CS1591
namespace FileFormat.Lrzip;

internal static class LrzipConstants {
  // ASCII "LRZI" — first four header bytes
  internal static readonly byte[] Magic = [0x4C, 0x52, 0x5A, 0x49];
  internal const string MagicString = "LRZI";

  // We target v0.6 of the lrzip container format
  internal const byte DefaultMajorVersion = 0;
  internal const byte DefaultMinorVersion = 6;

  // Magic(4) + major(1) + minor(1) + expandedSize(8) + method(1) + flags(1) + hashType(1)
  // + reserved(5) + md5(16) = 38 bytes total. We treat 32 bytes as the framed container
  // header (everything except the trailing 16-byte MD5 hash) for sub-section access.
  internal const int HeaderSize = 38;

  // Offset of the 5-byte LZMA properties block (1 byte props + 4 bytes dictSize LE) inside the body
  internal const int LzmaPreambleSize = 5;

  // 0x5D = lc=3, lp=0, pb=2 — the canonical LZMA setting
  internal const byte DefaultLzmaPropertiesByte = 0x5D;

  // 1 MiB default dictionary; small enough to keep tests cheap, big enough to be useful
  internal const int DefaultLzmaDictionarySize = 0x100000;

  // Method byte codes per the lrzip 0.6 format
  internal const byte MethodNone  = 0;
  internal const byte MethodLzma  = 1;
  internal const byte MethodLzo   = 2;
  internal const byte MethodBzip2 = 3;
  internal const byte MethodGzip  = 4;
  internal const byte MethodZpaq  = 5;
}
