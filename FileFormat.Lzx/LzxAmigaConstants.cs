namespace FileFormat.Lzx;

/// <summary>
/// Constants for the Amiga LZX archive format (Jonathan Forbes, 1995).
/// </summary>
/// <remarks>
/// This is the standalone Amiga archiver format, not Microsoft's CAB/WIM LZX codec
/// (though both share the same core compression algorithm).
/// </remarks>
internal static class LzxAmigaConstants {
  /// <summary>3-byte magic signature: ASCII "LZX" (0x4C 0x5A 0x58).</summary>
  internal static ReadOnlySpan<byte> Magic => "LZX"u8;

  /// <summary>Length of the magic signature in bytes.</summary>
  internal const int MagicLength = 3;

  // ── Compression methods ────────────────────────────────────────────────

  /// <summary>Method 0: stored (no compression).</summary>
  internal const byte MethodStored = 0;

  /// <summary>Method 2: LZX compressed.</summary>
  internal const byte MethodLzx = 2;

  // ── Entry header layout ────────────────────────────────────────────────

  /// <summary>Minimum entry header size before variable-length filename and comment.</summary>
  internal const int MinEntryHeaderSize = 31;

  // Offsets within the fixed portion of the entry header
  internal const int OffsetAttributes = 0;       // 2 bytes (LE)
  internal const int OffsetUncompressedSize = 2;  // 4 bytes (LE)
  internal const int OffsetCompressedSize = 6;    // 4 bytes (LE)
  internal const int OffsetMachineType = 10;      // 1 byte
  internal const int OffsetMethod = 11;           // 1 byte
  internal const int OffsetFlags = 12;            // 1 byte
  internal const int OffsetCommentLength = 13;    // 1 byte
  internal const int OffsetExtractVersion = 14;   // 1 byte
  internal const int OffsetPad = 15;              // 1 byte
  internal const int OffsetDate = 16;             // 4 bytes (LE) — Amiga DateStamp
  internal const int OffsetDataCrc = 20;          // 4 bytes (LE) — CRC-32 of uncompressed data
  internal const int OffsetHeaderCrc = 24;        // 4 bytes (LE) — CRC-32 of header
  internal const int OffsetFilenameLength = 28;   // 1 byte
  internal const int FixedHeaderSize = 29;        // bytes before filename

  // ── Flags ──────────────────────────────────────────────────────────────

  /// <summary>Bit 0 of the flags byte: file is merged with the next entry (solid group).</summary>
  internal const byte FlagMerged = 0x01;

  // ── Machine types ──────────────────────────────────────────────────────

  internal const byte MachineAmiga = 0;
  internal const byte MachineUnix = 1;
  internal const byte MachinePc = 2;

  // ── Amiga file attribute bits ──────────────────────────────────────────

  /// <summary>Hold (archive) bit.</summary>
  internal const uint AttrHold = 0x0020;

  /// <summary>Script bit.</summary>
  internal const uint AttrScript = 0x0040;

  /// <summary>Pure (re-entrant) bit.</summary>
  internal const uint AttrPure = 0x0080;

  /// <summary>Archived bit.</summary>
  internal const uint AttrArchived = 0x0010;

  /// <summary>Read bit.</summary>
  internal const uint AttrRead = 0x0008;

  /// <summary>Write bit.</summary>
  internal const uint AttrWrite = 0x0004;

  /// <summary>Execute bit.</summary>
  internal const uint AttrExecute = 0x0002;

  /// <summary>Delete bit.</summary>
  internal const uint AttrDelete = 0x0001;

  // ── Limits ─────────────────────────────────────────────────────────────

  /// <summary>Maximum filename length in bytes.</summary>
  internal const int MaxFilenameLength = 256;

  // ── LZX decompressor window bits ───────────────────────────────────────

  /// <summary>
  /// The Amiga LZX archiver uses a 17-bit window (128 KB) by default.
  /// </summary>
  internal const int DefaultWindowBits = 17;
}
