namespace FileFormat.Nrg;

/// <summary>Identifies how an <see cref="NrgEntry"/> is backed by the disc image.</summary>
public enum NrgEntryKind {
  /// <summary>An ISO 9660 file.</summary>
  IsoFile,
  /// <summary>An ISO 9660 directory.</summary>
  IsoDirectory,
  /// <summary>A raw stored track exposed because no ISO file tree represents its content.</summary>
  RawTrack,
}

/// <summary>Represents a filesystem or raw-track entry in a Nero NRG disc image.</summary>
public sealed class NrgEntry {
  /// <summary>Gets the filename or directory name of this entry.</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the full path within the archive view, using forward slashes.</summary>
  public string FullPath { get; init; } = "";

  /// <summary>Gets whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Gets the backing entry kind.</summary>
  public NrgEntryKind Kind { get; init; }

  /// <summary>Gets the file size in bytes (0 for directories).</summary>
  public long Size { get; init; }

  /// <summary>Gets the starting LBA of an ISO extent, or index-1 LBA for a raw track when known.</summary>
  public int StartLba { get; init; }

  /// <summary>Gets the one-based containing NRG session number when known.</summary>
  public int? SessionNumber { get; init; }

  /// <summary>Gets the globally numbered NRG track number when known.</summary>
  public int? TrackNumber { get; init; }
}
