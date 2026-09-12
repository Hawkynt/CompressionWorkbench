namespace FileFormat.Nsis;

internal static class NsisConstants {
  internal static readonly byte[] Signature = [
    0xEF, 0xBE, 0xAD, 0xDE,
    (byte)'N', (byte)'u', (byte)'l', (byte)'l',
    (byte)'s', (byte)'o', (byte)'f', (byte)'t',
    (byte)'I', (byte)'n', (byte)'s', (byte)'t'
  ];

  internal const int SignatureOffset = 4;
  internal const int SignatureLength = 16;
  internal const int FirstHeaderSize = 28; // flags(4) + sig(16) + header_size(4) + archive_size(4)

  internal const int CompressionMask = 0x0F;
  internal const int SolidFlag = 0x10;

  internal const int CompNone  = 0;
  internal const int CompZlib  = 1;
  internal const int CompBzip2 = 2;
  internal const int CompLzma  = 3;

  // LZMA sub-header inside the compressed stream: 5-byte properties + 8-byte uncompressed size
  internal const int LzmaPropSize = 5;
  internal const int LzmaHeaderSize = 13;

  // Zlib: 2-byte header (CMF + FLG) before raw Deflate data
  internal const int ZlibHeaderSize = 2;

  // Bit 31 of a block-length word means the block is stored uncompressed
  internal const uint UncompressedFlag = 0x80000000u;
}
