namespace FileFormat.Mpq;

/// <summary>Entry in an MPQ archive.</summary>
public sealed class MpqEntry {
  /// <summary>
  /// Gets or sets the file name.
  /// </summary>
  public string FileName { get; init; } = "";
  /// <summary>
  /// Gets or sets the original size.
  /// </summary>
  public long OriginalSize { get; init; }
  /// <summary>
  /// Gets or sets the compressed size.
  /// </summary>
  public long CompressedSize { get; init; }
  /// <summary>
  /// Gets or sets the flags.
  /// </summary>
  public uint Flags { get; init; }
  /// <summary>
  /// Gets or sets the file offset.
  /// </summary>
  public long FileOffset { get; init; }
  /// <summary>Gets the locale the hash-table entry was stored under (0 = neutral).</summary>
  public ushort Locale { get; init; }
  /// <summary>Gets whether <see cref="FileName"/> came from the listfile rather than being made up for an unnamed entry.</summary>
  public bool IsNameKnown { get; init; } = true;
  /// <summary>The name's raw bytes, used for the encryption key.</summary>
  internal byte[]? NameBytes { get; init; }
  /// <summary>Index of the entry's block-table record, or -1 when the entry was built by hand.</summary>
  internal int BlockIndex { get; init; } = -1;

  /// <summary>File exists in archive.</summary>
  public bool Exists => (Flags & 0x80000000) != 0;
  /// <summary>File is compressed.</summary>
  public bool IsCompressed => (Flags & 0x00000200) != 0 || (Flags & 0x00000100) != 0;
  /// <summary>File is PKWARE DCL imploded (no per-sector mask byte).</summary>
  public bool IsImploded => (Flags & 0x00000100) != 0 && (Flags & 0x00000200) == 0;
  /// <summary>File is encrypted.</summary>
  public bool IsEncrypted => (Flags & 0x00010000) != 0;
  /// <summary>The encryption key is adjusted by offset and size.</summary>
  public bool HasAdjustedKey => (Flags & 0x00020000) != 0;
  /// <summary>File is a single unit (not sector-based).</summary>
  public bool IsSingleUnit => (Flags & 0x01000000) != 0;
  /// <summary>File carries an Adler-32 per sector.</summary>
  public bool HasSectorChecksums => (Flags & 0x04000000) != 0;
  /// <summary>File is a patch (incremental update) rather than file content.</summary>
  public bool IsPatchFile => (Flags & 0x00100000) != 0;
  /// <summary>File is a deletion marker from a patch archive.</summary>
  public bool IsDeleteMarker => (Flags & 0x02000000) != 0;
}
