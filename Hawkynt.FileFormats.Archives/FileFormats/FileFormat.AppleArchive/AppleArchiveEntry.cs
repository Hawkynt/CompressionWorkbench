namespace FileFormat.AppleArchive;

/// <summary>One item of an Apple Archive (an AA01/YAA1 header and its blobs).</summary>
public sealed class AppleArchiveEntry {
  /// <summary>Gets the path (the PAT field), '/'-separated; empty for the archive root directory.</summary>
  public string Path { get; init; } = string.Empty;

  /// <summary>Gets the entry type (TYP): 'F' file, 'D' directory, 'L' symlink, 'B'/'C' devices, 'P' FIFO, 'S' socket, 'M' metadata.</summary>
  public char Type { get; init; } = 'F';

  /// <summary>Gets the symlink target (LNK), when present.</summary>
  public string? LinkTarget { get; init; }

  /// <summary>Gets the length of the file data blob (DAT); 0 when the item has none.</summary>
  public long Size { get; init; }

  /// <summary>Gets the POSIX permission bits (MOD), when present.</summary>
  public int? Mode { get; init; }

  /// <summary>Gets the owner id (UID), when present.</summary>
  public long? Uid { get; init; }

  /// <summary>Gets the group id (GID), when present.</summary>
  public long? Gid { get; init; }

  /// <summary>Gets the modification time (MTM), in UTC, when present.</summary>
  public DateTime? LastModified { get; init; }

  /// <summary>Gets whether this item is a directory.</summary>
  public bool IsDirectory => this.Type == 'D';

  /// <summary>Gets the three-letter keys of every field, in header order.</summary>
  public IReadOnlyList<string> FieldKeys { get; init; } = [];

  /// <summary>Offset of the DAT blob in the decoded (plain) archive stream, or -1.</summary>
  internal long DataOffset { get; init; } = -1;
}
