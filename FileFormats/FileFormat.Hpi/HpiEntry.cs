namespace FileFormat.Hpi;

/// <summary>
/// Represents a single entry (file or directory) in a Total Annihilation HPI archive.
/// Paths use forward-slash separators and never start with a slash.
/// </summary>
public sealed class HpiEntry {
  /// <summary>Gets the full forward-slash path of the entry (e.g. <c>"units/armcom.fbi"</c>).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute byte offset in the archive of this entry's data block (files) or sub-directory header (directories).</summary>
  public long DataOffset { get; init; }

  /// <summary>Gets the original (uncompressed) size of the file in bytes; 0 for directories.</summary>
  public long Size { get; init; }

  /// <summary>Gets a value indicating whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }
}
