namespace FileFormat.Ba2;

/// <summary>
/// One file record inside a BA2 GNRL archive.
/// </summary>
public sealed class Ba2Entry {
  /// <summary>Full relative path with backslash separators, e.g. <c>textures\effects\smoke01.dds</c>.</summary>
  public string Name { get; init; } = "";

  /// <summary>Lookup3 hash of the lowercase basename without extension.</summary>
  public uint NameHash { get; init; }

  /// <summary>Lookup3 hash of the lowercase directory portion (no leading/trailing slash).</summary>
  public uint DirHash { get; init; }

  /// <summary>Up to 4 ASCII characters of the lowercase extension, no leading dot, trimmed of trailing NULs.</summary>
  public string Ext { get; init; } = "";

  /// <summary>Absolute byte offset where this file's payload starts in the archive.</summary>
  public long Offset { get; init; }

  /// <summary>Compressed byte length, or 0 when the file is stored uncompressed.</summary>
  public long PackedSize { get; init; }

  /// <summary>Original (uncompressed) byte length.</summary>
  public long Size { get; init; }

  /// <summary>Per-record flags. Typically 0 for GNRL archives.</summary>
  public uint Flags { get; init; }
}
