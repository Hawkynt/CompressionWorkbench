namespace FileFormat.Nrg;

/// <summary>Represents a file or directory entry in a Nero NRG disc image.</summary>
public sealed class NrgEntry {
  /// <summary>Gets the filename or directory name of this entry.</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the full path within the disc image, using forward slashes.</summary>
  public string FullPath { get; init; } = "";

  /// <summary>Gets whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Gets the file size in bytes (0 for directories).</summary>
  public long Size { get; init; }

  /// <summary>Gets the starting LBA (Logical Block Address) of this entry's data.</summary>
  public int StartLba { get; init; }
}
