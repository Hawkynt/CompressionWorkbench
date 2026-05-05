#pragma warning disable CS1591
namespace FileFormat.Mhk;

internal static class MhkConstants {
  // Outer IFF-style frame tag: "MHWK" — MHK files always nest a single RSRC chunk inside this.
  internal const string OuterMagicString = "MHWK";

  // Inner chunk that holds the actual resource directory + file payloads.
  internal const string RsrcMagicString = "RSRC";

  /// <summary>8-byte outer header: magic(4) + bodySize(4 BE).</summary>
  internal const int OuterHeaderSize = 8;

  /// <summary>18-byte RSRC payload header that follows magic(4) + size(4): version(2) + compaction(2) + totalSize(4) + dirOffset(4) + typeTableOffset(2) + reserved(2).</summary>
  internal const int RsrcHeaderFixedSize = 18;

  /// <summary>Full inner header on disk including its own magic+size: 8 + 18.</summary>
  internal const int RsrcHeaderTotalSize = 8 + RsrcHeaderFixedSize;

  /// <summary>FourCC type tags are exactly 4 ASCII bytes.</summary>
  internal const int TypeTagSize = 4;

  /// <summary>Default Mohawk version word written by the engine: 0x0100.</summary>
  internal const ushort DefaultVersion = 0x0100;

  /// <summary>Compaction word — written as 1; ignored by the engine.</summary>
  internal const ushort DefaultCompaction = 0x0001;

  /// <summary>File-table entry on disk: offset(4) + sizeLow24(3) + sizeHigh8(1) + flags(1) + unknown(2) = 11 bytes.</summary>
  internal const int FileTableEntrySize = 11;

  /// <summary>Type-table entry on disk: tag(4) + resourceTableOffset(2) + nameTableOffset(2) = 8 bytes.</summary>
  internal const int TypeTableEntrySize = 8;

  /// <summary>Resource-table entry on disk: id(2) + fileIndex(2) = 4 bytes.</summary>
  internal const int ResourceTableEntrySize = 4;

  /// <summary>Name-table entry on disk: nameOffset(2) + id(2) = 4 bytes.</summary>
  internal const int NameTableEntrySize = 4;
}
