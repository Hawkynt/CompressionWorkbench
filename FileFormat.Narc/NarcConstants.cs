#pragma warning disable CS1591
namespace FileFormat.Narc;

public static class NarcConstants {
  // NITRO header is fixed 16 bytes: magic(4) + bom(2) + version(2) + fileSize(4) + headerSize(2) + sectionCount(2)
  public const int NitroHeaderSize = 16;

  // Section header is 8 bytes: magic(4) + sectionSize(4)
  public const int SectionHeaderSize = 8;

  // BTAF entry is 8 bytes: startOffset(4) + endOffset(4), both relative to start of GMIF data region
  public const int BtafEntrySize = 8;

  // Magic for the wrapping NITRO file. ASCII "NARC" in stream order.
  public static readonly byte[] MagicNarc = "NARC"u8.ToArray();

  // BTAF = File Allocation Table — list of (start,end) offsets into GMIF data
  public static readonly byte[] MagicBtaf = "BTAF"u8.ToArray();

  // BTNF = File Name Table — directory tree (we only emit the flat-tree variant)
  public static readonly byte[] MagicBtnf = "BTNF"u8.ToArray();

  // GMIF = File Image — concatenated file payloads
  public static readonly byte[] MagicGmif = "GMIF"u8.ToArray();

  // NITRO byte-order mark indicating little-endian. Big-endian NARCs are not known to exist on DS hardware.
  public const ushort BomLittleEndian = 0xFFFE;

  // NITRO format version. All shipping NARC files use 0x0100.
  public const ushort DefaultVersion = 0x0100;

  // NITRO containers always declare exactly 3 sections: BTAF, BTNF, GMIF.
  public const ushort SectionCount = 3;

  // BTNF length byte is 7 bits in canonical NARC; the high bit flags subdirectory entries which we never emit.
  public const int MaxNameLength = 127;
}
