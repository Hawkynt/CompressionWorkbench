namespace FileFormat.Ba2;

internal static class Ba2Constants {
  /// <summary>BTDX header magic — Bethesda Texture/Data archive.</summary>
  internal const uint Magic = 0x58_44_54_42; // 'B','T','D','X' little-endian

  /// <summary>Version 1 — Fallout 4, Skyrim SE.</summary>
  internal const uint Version1 = 1;

  /// <summary>4-byte ASCII tag for general (non-texture) archives.</summary>
  internal const uint TypeGnrl = 0x4C_52_4E_47; // 'G','N','R','L' little-endian

  /// <summary>4-byte ASCII tag for DX10 texture archives — out of scope for the GNRL writer/reader.</summary>
  internal const uint TypeDx10 = 0x30_31_58_44; // 'D','X','1','0' little-endian

  /// <summary>Per-record sentinel constant. Bethesda writes this verbatim; we mirror it for compatibility with their tools.</summary>
  internal const uint RecordSentinel = 0xBAADF00D;

  /// <summary>BTDX(4) + version(4) + type(4) + fileCount(4) + nameTableOffset(8).</summary>
  internal const int HeaderSize = 24;

  /// <summary>nameHash(4) + ext(4) + dirHash(4) + flags(4) + offset(8) + packedSize(4) + size(4) + sentinel(4).</summary>
  internal const int RecordSize = 36;
}
