namespace FileFormat.Rar;

/// <summary>
/// Represents an entry in a RAR archive.
/// </summary>
public sealed class RarEntry {
  /// <summary>Gets or sets the entry name (including path within the archive).</summary>
  public string Name { get; set; } = "";

  /// <summary>Gets or sets the uncompressed size in bytes (-1 when the archive leaves it unknown).</summary>
  public long Size { get; set; }

  /// <summary>Gets or sets the compressed size in bytes, summed over every volume the entry spans.</summary>
  public long CompressedSize { get; set; }

  /// <summary>Gets or sets a value indicating whether this entry is a directory.</summary>
  public bool IsDirectory { get; set; }

  /// <summary>Gets or sets the last modification time, or <see langword="null"/> if not stored.</summary>
  public DateTimeOffset? ModifiedTime { get; set; }

  /// <summary>Gets or sets the creation time, or <see langword="null"/> if not stored.</summary>
  public DateTimeOffset? CreatedTime { get; set; }

  /// <summary>Gets or sets the last access time, or <see langword="null"/> if not stored.</summary>
  public DateTimeOffset? AccessedTime { get; set; }

  /// <summary>Gets or sets the CRC-32 of the uncompressed data, or <see langword="null"/> if not stored.</summary>
  public uint? Crc { get; set; }

  /// <summary>Gets or sets the BLAKE2sp hash RAR 5 stores instead of (or beside) the CRC-32.</summary>
  public byte[]? Blake2sp { get; set; }

  /// <summary>Gets or sets the compression method used (0 = Store, 1-5 = compressed).</summary>
  public int CompressionMethod { get; set; }

  /// <summary>Gets or sets a value indicating whether this entry uses solid compression (dictionary carries over from previous file).</summary>
  public bool IsSolid { get; set; }

  /// <summary>Gets or sets a value indicating whether this entry is encrypted.</summary>
  public bool IsEncrypted { get; set; }

  /// <summary>Gets or sets the host file attributes (Windows attributes, or the Unix mode for Unix archives).</summary>
  public uint Attributes { get; set; }

  /// <summary>Gets or sets the host the entry was archived on (0 Windows/DOS, 1 Unix for RAR 5; the RAR 4 host byte otherwise).</summary>
  public int HostOs { get; set; }

  /// <summary>Gets or sets what the entry is a link to, if anything.</summary>
  public RarLinkKind LinkKind { get; set; }

  /// <summary>Gets or sets the stored link target of a link entry.</summary>
  public string? LinkTarget { get; set; }

  /// <summary>Gets a value indicating whether the entry is a symbolic link or junction (listed, not extracted as a file).</summary>
  public bool IsSymlink => this.LinkKind is RarLinkKind.UnixSymlink or RarLinkKind.WindowsSymlink or RarLinkKind.WindowsJunction;

  /// <summary>Gets or sets the <c>rar -ver</c> file version, when the archive keeps several.</summary>
  public long? FileVersion { get; set; }

  /// <summary>Gets or sets the name of the NTFS alternate stream this entry is (<c>file:stream</c>), if it is one.</summary>
  public string? StreamName { get; set; }

  /// <summary>Gets or sets a value indicating whether part of the entry lies in a volume that is not present.</summary>
  public bool IsIncomplete { get; set; }

  /// <summary>Gets or sets how many volumes the entry's data spans.</summary>
  public int VolumeCount { get; set; } = 1;
}
