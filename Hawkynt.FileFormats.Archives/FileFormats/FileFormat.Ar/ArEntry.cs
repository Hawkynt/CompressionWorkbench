namespace FileFormat.Ar;

/// <summary>
/// Represents a single file entry in a Unix ar archive.
/// </summary>
public sealed class ArEntry {
  /// <summary>Gets or sets the filename of the entry.</summary>
  public string Name { get; set; } = string.Empty;

  /// <summary>Gets or sets the last-modification time of the entry.</summary>
  public DateTimeOffset ModifiedTime { get; set; } = DateTimeOffset.UnixEpoch;

  /// <summary>Gets or sets the numeric owner (user) ID.</summary>
  public int OwnerId { get; set; }

  /// <summary>Gets or sets the numeric group ID.</summary>
  public int GroupId { get; set; }

  /// <summary>Gets or sets the file permission mode (octal, e.g. 0o100644).</summary>
  public int FileMode { get; set; } = 0x81A4; // octal 0100644

  /// <summary>Gets or sets the raw file data.</summary>
  public byte[] Data { get; set; } = [];

  /// <summary>Byte offset of this entry's data within the archive.</summary>
  public long DataOffset { get; set; }

  /// <summary>Length of this entry's data, which may exceed what <see cref="Data" /> can hold.</summary>
  public long DataSize { get; set; }

  /// <summary>
  /// True when <see cref="Data" /> holds the entry's bytes. False for an entry too
  /// large to materialise, whose bytes must be read via
  /// <c>ArReader.CopyEntryTo</c> using <see cref="DataOffset" /> and <see cref="DataSize" />.
  /// </summary>
  public bool IsDataLoaded => this.Data.LongLength == this.DataSize;
}
