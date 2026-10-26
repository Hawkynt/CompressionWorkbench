namespace FileSystem.Vdfs;

/// <summary>
/// One catalog entry of a VDF volume: a directory, or a file and where its payload lives.
/// </summary>
public sealed class VdfsEntry {

  /// <summary>Full path inside the volume, components joined by <c>/</c>.</summary>
  public string Name { get; init; } = "";

  /// <summary>Uncompressed size; 0 for a directory.</summary>
  public long Size { get; init; }

  /// <summary>Whether the entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Whether the payload is a Union ZippedStream rather than the raw bytes.</summary>
  public bool IsZipped { get; init; }

  /// <summary>Bytes the payload occupies in the volume: the stream for a zipped file, else <see cref="Size" />.</summary>
  public long StoredSize { get; init; }

  /// <summary>Win32 attributes recorded in the entry.</summary>
  public uint Attributes { get; init; }

  /// <summary>Absolute offset of the payload.</summary>
  internal long DataOffset { get; init; }

  /// <summary>Parsed ZippedStream, when <see cref="IsZipped" />.</summary>
  internal VdfsZippedStream.Layout? Zipped { get; init; }
}
