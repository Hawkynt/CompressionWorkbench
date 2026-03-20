namespace FileFormat.Tar;

/// <summary>
/// Represents a single entry in a TAR archive.
/// </summary>
public sealed class TarEntry {
  /// <summary>Gets or sets the file name (including path within the archive).</summary>
  public string Name { get; set; } = string.Empty;

  /// <summary>Gets or sets the uncompressed size in bytes.</summary>
  public long Size { get; set; }

  /// <summary>Gets or sets the type flag indicating the entry type.</summary>
  public byte TypeFlag { get; set; } = TarConstants.TypeRegular;

  /// <summary>Gets or sets the file mode (Unix permissions).</summary>
  public int Mode { get; set; } = 420; // 0644 octal

  /// <summary>Gets or sets the user ID of the owner.</summary>
  public int Uid { get; set; }

  /// <summary>Gets or sets the group ID of the owner.</summary>
  public int Gid { get; set; }

  /// <summary>Gets or sets the last modification time.</summary>
  public DateTimeOffset ModifiedTime { get; set; } = DateTimeOffset.UnixEpoch;

  /// <summary>Gets or sets the link target name for hard or symbolic links.</summary>
  public string LinkName { get; set; } = string.Empty;

  /// <summary>Gets or sets the user name of the owner.</summary>
  public string UserName { get; set; } = string.Empty;

  /// <summary>Gets or sets the group name of the owner.</summary>
  public string GroupName { get; set; } = string.Empty;

  /// <summary>Gets whether this entry represents a directory.</summary>
  public bool IsDirectory => TypeFlag == TarConstants.TypeDirectory;

  /// <summary>Gets whether this entry represents a regular file.</summary>
  public bool IsFile => TypeFlag == TarConstants.TypeRegular || TypeFlag == TarConstants.TypeRegularAlt;

  /// <summary>Gets or sets the real size of the file for multi-volume continuation entries.</summary>
  /// <remarks>
  /// For GNU multi-volume continuation entries (type 'M'), this holds the total size
  /// of the original file. <see cref="Size"/> holds the size of the data in this volume.
  /// The <see cref="Offset"/> field indicates where this chunk starts within the original file.
  /// </remarks>
  public long RealSize { get; set; }

  /// <summary>Gets or sets the byte offset within the original file for multi-volume entries.</summary>
  public long Offset { get; set; }
}
