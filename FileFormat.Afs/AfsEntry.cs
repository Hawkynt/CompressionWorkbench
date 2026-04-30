namespace FileFormat.Afs;

/// <summary>
/// Represents a single file entry in a Sega AFS archive.
/// </summary>
public sealed class AfsEntry {
  /// <summary>Gets the entry name (synthesized as "file_NNNN.bin" if metadata is absent).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute byte offset of the entry's data within the archive.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the entry's data length in bytes.</summary>
  public long Size { get; init; }

  /// <summary>Gets the entry's last-modified timestamp from the metadata block, or null if absent.</summary>
  public DateTime? LastModified { get; init; }
}
