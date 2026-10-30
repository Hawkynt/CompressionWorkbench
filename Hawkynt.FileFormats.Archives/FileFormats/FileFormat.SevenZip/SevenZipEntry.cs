namespace FileFormat.SevenZip;

/// <summary>
/// Represents a single entry (file or directory) in a 7z archive.
/// </summary>
public sealed class SevenZipEntry {
  /// <summary>Gets or sets the file name (including path within the archive).</summary>
  public string Name { get; set; } = "";

  /// <summary>Gets or sets the uncompressed size in bytes.</summary>
  public long Size { get; set; }

  /// <summary>Gets or sets whether this entry is a directory.</summary>
  public bool IsDirectory { get; set; }

  /// <summary>Gets or sets the last write time in UTC.</summary>
  public DateTime? LastWriteTime { get; set; }

  /// <summary>Gets or sets the creation time in UTC.</summary>
  public DateTime? CreationTime { get; set; }

  /// <summary>Gets or sets the last access time in UTC (<c>kATime</c>, written by 7-Zip's <c>-mta=on</c>).</summary>
  public DateTime? LastAccessTime { get; set; }

  /// <summary>Gets or sets the compressed size in bytes (-1 if unknown).</summary>
  public long CompressedSize { get; set; } = -1;

  /// <summary>Gets or sets the compression method name.</summary>
  public string Method { get; set; } = "";

  /// <summary>Gets or sets the CRC-32 of the uncompressed data.</summary>
  public uint? Crc { get; set; }

  /// <summary>Gets or sets the Windows file attributes.</summary>
  public uint? Attributes { get; set; }

  /// <summary>
  /// Gets or sets whether this is an anti-item (<c>kAnti</c>): a deletion marker a differential
  /// update archive carries for a file or directory that no longer exists. It has no data and is
  /// never extracted.
  /// </summary>
  public bool IsAnti { get; set; }

  /// <summary>Gets or sets whether the entry's data is AES-encrypted.</summary>
  public bool IsEncrypted { get; set; }

  /// <summary>
  /// The Unix mode (file type and permission bits) a Unix 7-Zip records in the high 16 bits of
  /// the attributes, flagged by <see cref="UnixExtensionFlag"/>; <see langword="null"/> when the
  /// archive was written without one.
  /// </summary>
  public int? UnixMode => this.Attributes is { } a && (a & UnixExtensionFlag) != 0 ? (int)(a >> 16) : null;

  /// <summary>
  /// Whether this is a Unix symbolic link (<c>7z a -snl</c> on Unix): its data is the link target,
  /// UTF-8 encoded.
  /// </summary>
  public bool IsSymlink => this.UnixMode is { } mode && (mode & 0xF000) == 0xA000;

  /// <summary>FILE_ATTRIBUTE_UNIX_EXTENSION: the attributes' high 16 bits hold a Unix mode.</summary>
  public const uint UnixExtensionFlag = 0x8000;

  /// <summary>The 7z attribute value for a Unix mode (type and permission bits), as Unix 7-Zip writes it.</summary>
  public static uint AttributesForUnixMode(int mode) {
    var attributes = UnixExtensionFlag | ((uint)(mode & 0xFFFF) << 16);
    if ((mode & 0xF000) == 0x4000)
      attributes |= 0x10; // FILE_ATTRIBUTE_DIRECTORY
    if ((mode & 0x80) == 0)
      attributes |= 0x01; // FILE_ATTRIBUTE_READONLY when the owner cannot write
    return attributes;
  }
}
