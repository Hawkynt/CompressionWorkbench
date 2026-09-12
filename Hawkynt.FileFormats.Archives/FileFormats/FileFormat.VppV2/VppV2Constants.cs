#pragma warning disable CS1591
namespace FileFormat.VppV2;

internal static class VppV2Constants {
  /// <summary>Magic UInt32 LE: 0x51890ACE — same Volition signature as v1; on disk: CE 0A 89 51.</summary>
  internal const uint Magic = 0x51890ACEu;

  /// <summary>Saint's Row 2 (Volition Q3 PS3/Xbox 360 era) version field.</summary>
  internal const uint SupportedVersion = 2u;

  /// <summary>
  /// Total populated header size in bytes: Magic(4) + Version(4) + ShortName(256) + Path(96) +
  /// HeaderSize(4) + FileCount(4) + ArchiveSize(4) + TocSize(4) + NameTableSize(4) + DataSize(4) +
  /// CompressedSize(4) + Flags(4) = 392. The remainder is padding up to <see cref="RequiredHeaderSizeField"/>
  /// (0x800), which is where the TOC actually starts.
  /// </summary>
  internal const int HeaderSize = 392;

  /// <summary>Offset of the HeaderSize field within the header (immediately after Path).</summary>
  internal const int HeaderSizeFieldOffset = 8 + ShortNameFieldSize + PathFieldSize; // 360

  /// <summary>Offset of the FileCount field within the header.</summary>
  internal const int FileCountFieldOffset = HeaderSizeFieldOffset + 4; // 364

  /// <summary>Offset of the ArchiveSize field within the header.</summary>
  internal const int ArchiveSizeFieldOffset = HeaderSizeFieldOffset + 8; // 368

  /// <summary>Offset of the TocSize field within the header.</summary>
  internal const int TocSizeFieldOffset = HeaderSizeFieldOffset + 12; // 372

  /// <summary>Offset of the NameTableSize field within the header.</summary>
  internal const int NameTableSizeFieldOffset = HeaderSizeFieldOffset + 16; // 376

  /// <summary>Offset of the uncompressed-total DataSize field within the header.</summary>
  internal const int DataSizeFieldOffset = HeaderSizeFieldOffset + 20; // 380

  /// <summary>Offset of the on-disk-total CompressedSize field within the header.</summary>
  internal const int CompressedSizeFieldOffset = HeaderSizeFieldOffset + 24; // 384

  /// <summary>Offset of the archive-level Flags field within the header.</summary>
  internal const int FlagsFieldOffset = HeaderSizeFieldOffset + 28; // 388

  /// <summary>Required HeaderSize field value: TOC and every section start at this 0x800 boundary.</summary>
  internal const uint RequiredHeaderSizeField = 0x00000800u;

  /// <summary>Section alignment for header → TOC → name table → data region transitions.</summary>
  internal const int SectionAlignment = 0x800;

  /// <summary>Per-entry payload alignment within the data region.</summary>
  internal const int DataAlignment = 0x10;

  /// <summary>Bytes per TOC entry (NameOff, ExtOff, DataOff, DataSize, CompSize, Flags, Padding — 7×4).</summary>
  internal const int TocEntrySize = 28;

  /// <summary>Cosmetic ShortName field width inside the header (ASCII, null-padded).</summary>
  internal const int ShortNameFieldSize = 256;

  /// <summary>Cosmetic Path field width inside the header (ASCII, null-padded).</summary>
  internal const int PathFieldSize = 96;

  /// <summary>Archive-level flag: bit 0 = per-entry zlib compression.</summary>
  internal const uint FlagArchiveCompressed = 0x1u;

  /// <summary>Archive-level flag: bit 1 = whole-archive zlib stream (rejected).</summary>
  internal const uint FlagArchiveCondensed = 0x2u;

  /// <summary>Per-entry flag: bit 0 = entry payload is zlib-compressed.</summary>
  internal const uint FlagEntryCompressed = 0x1u;
}
