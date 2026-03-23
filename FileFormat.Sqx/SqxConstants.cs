namespace FileFormat.Sqx;

/// <summary>
/// Constants for the SQX archive format (V11 and V20).
/// </summary>
internal static class SqxConstants {
  /// <summary>SQX magic signature in main header.</summary>
  public const string Magic = "-sqx-";

  // ── Block types ───────────────────────────────────────────────────

  /// <summary>Block type: main archive header.</summary>
  public const byte BlockMain = 0x52;

  /// <summary>Block type: file entry.</summary>
  public const byte BlockFile = 0x44;

  /// <summary>Block type: directory entry.</summary>
  public const byte BlockDirectory = 0x56;

  /// <summary>Block type: end of archive.</summary>
  public const byte BlockEnd = 0x53;

  /// <summary>Block type: comment.</summary>
  public const byte BlockComment = 0x43;

  /// <summary>Block type: data recovery record.</summary>
  public const byte BlockRecovery = 0x58;

  /// <summary>Block type: authenticity verification.</summary>
  public const byte BlockAV = 0x41;

  /// <summary>Block type: internal subheader.</summary>
  public const byte BlockInternal = 0x49;

  // ── Archive version ───────────────────────────────────────────────

  /// <summary>SQX format version 1.x.</summary>
  public const byte ArcVersion11 = 11;

  /// <summary>SQX format version 2.0.</summary>
  public const byte ArcVersion20 = 20;

  // ── Compression methods (SQX V11) ────────────────────────────────

  /// <summary>Compression method: store (no compression).</summary>
  public const byte MethodStore = 0x00;

  /// <summary>Compression method: LZH normal.</summary>
  public const byte MethodNormal = 0x01;

  /// <summary>Compression method: LZH good.</summary>
  public const byte MethodGood = 0x02;

  /// <summary>Compression method: LZH high.</summary>
  public const byte MethodHigh = 0x03;

  /// <summary>Compression method: LZH best.</summary>
  public const byte MethodBest = 0x04;

  /// <summary>Compression method: audio.</summary>
  public const byte MethodAudio = 0x05;

  // ── Compression methods (SQX V20 additions) ──────────────────────

  /// <summary>Compression method: LZH extended normal.</summary>
  public const byte MethodExNormal = 0x06;

  /// <summary>Compression method: LZH extended good.</summary>
  public const byte MethodExGood = 0x07;

  /// <summary>Compression method: LZH extended high.</summary>
  public const byte MethodExHigh = 0x08;

  /// <summary>Compression method: LZH extended best.</summary>
  public const byte MethodExBest = 0x09;

  /// <summary>Compression method: extended audio.</summary>
  public const byte MethodExAudio = 0x0A;

  // ── Main header flags (BLOCK_FLAGS of main header) ────────────────

  /// <summary>Headers are encrypted (Blowfish).</summary>
  public const ushort MainFlagEncryptedHeaders = 0x0002;

  /// <summary>Archive is solid.</summary>
  public const ushort MainFlagSolid = 0x0004;

  /// <summary>Multi-volume archive.</summary>
  public const ushort MainFlagMultiVolume = 0x0008;

  /// <summary>Last volume of multi-volume.</summary>
  public const ushort MainFlagLastVolume = 0x0010;

  /// <summary>Archive has main comment.</summary>
  public const ushort MainFlagComment = 0x0020;

  /// <summary>External recovery data.</summary>
  public const ushort MainFlagExtRecovery = 0x0040;

  // ── File header flags (BLOCK_FLAGS of file header) ────────────────

  /// <summary>Block size is 4 bytes.</summary>
  public const ushort FileFlagLargeBlock = 0x0001;

  /// <summary>File data is encrypted (AES-128-CBC).</summary>
  public const ushort FileFlagEncrypted = 0x0002;

  /// <summary>Solid: stats/data from previous files used.</summary>
  public const ushort FileFlagSolid = 0x0004;

  /// <summary>Continued from previous volume.</summary>
  public const ushort FileFlagPrevVolume = 0x0008;

  /// <summary>Continued on next volume.</summary>
  public const ushort FileFlagNextVolume = 0x0010;

  /// <summary>File has comment.</summary>
  public const ushort FileFlagComment = 0x0020;

  /// <summary>Filename converted to ASCII.</summary>
  public const ushort FileFlagAscii = 0x0040;

  /// <summary>File larger than 4GB (extended size fields).</summary>
  public const ushort FileFlagFile64 = 0x0080;

  /// <summary>Another block/subblock follows.</summary>
  public const ushort FileFlagNextBlock = 0x8000;

  /// <summary>Mask for dictionary size index (bits 8-10).</summary>
  public const ushort FileFlagDictMask = 0x0700;

  // ── COMP_FLAGS (per-file) ─────────────────────────────────────────

  /// <summary>Extra compressor features present (2-byte EXTRA_COMPRESSOR follows).</summary>
  public const byte CompFlagExtra = 0x01;

  // ── EXTRA_COMPRESSOR flags ────────────────────────────────────────

  /// <summary>IA-32 relative call instruction transform applied.</summary>
  public const ushort ExtraFlagBcj = 0x0002;

  /// <summary>Delta transform applied.</summary>
  public const ushort ExtraFlagDelta = 0x0004;

  // ── Encryption ────────────────────────────────────────────────────

  /// <summary>No encryption.</summary>
  public const ushort CryptNone = 0x0000;

  /// <summary>AES (Rijndael) 128-bit encryption.</summary>
  public const ushort CryptAes128 = 0x0001;

  // ── Recovery record ───────────────────────────────────────────────

  /// <summary>Recovery record signature.</summary>
  public const string RecoverySignature = "SQ4RD";

  /// <summary>Recovery sector size in bytes.</summary>
  public const int RecoverySectorSize = 512;

  // ── Dictionary size lookup ────────────────────────────────────────

  /// <summary>Dictionary sizes indexed by bits 8-10 of file flags.</summary>
  public static readonly int[] DictSizes = [
    32 * 1024,     // 0 = 32KB
    64 * 1024,     // 1 = 64KB
    128 * 1024,    // 2 = 128KB
    256 * 1024,    // 3 = 256KB
    512 * 1024,    // 4 = 512KB
    1024 * 1024,   // 5 = 1MB
    2 * 1024 * 1024, // 6 = 2MB
    4 * 1024 * 1024  // 7 = 4MB
  ];

  /// <summary>
  /// Gets the dictionary size flag bits for a given dictionary size.
  /// </summary>
  public static ushort GetDictFlag(int dictSize) {
    for (var i = DictSizes.Length - 1; i >= 0; --i)
      if (dictSize >= DictSizes[i])
        return (ushort)(i << 8);
    return 0;
  }

  /// <summary>
  /// Gets the dictionary size from file flags.
  /// </summary>
  public static int GetDictSize(ushort flags) {
    var idx = (flags >> 8) & 0x0F;
    return idx < DictSizes.Length ? DictSizes[idx] : DictSizes[0];
  }

  // ── Backward compat aliases / internal method codes ────────────────

  /// <summary>Alias for <see cref="FileFlagEncrypted"/>.</summary>
  public const ushort FlagEncrypted = FileFlagEncrypted;

  /// <summary>Alias for <see cref="MethodNormal"/>.</summary>
  public const byte MethodLzh = MethodNormal;

  /// <summary>Internal method code for multimedia (delta+arithmetic). Stored as MethodExNormal + transform.</summary>
  public const byte MethodMultimedia = 0x80;

  /// <summary>Internal method code for LZH+BCJ preprocessing. Stored as MethodNormal + extra flag.</summary>
  public const byte MethodLzhBcj = 0x81;

  /// <summary>Internal method code for LZH+Delta preprocessing. Stored as MethodNormal + extra flag.</summary>
  public const byte MethodLzhDelta = 0x82;
}
