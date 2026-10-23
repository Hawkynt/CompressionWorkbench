namespace FileSystem.Zfs;

/// <summary>
/// One name in the pool's namespace: a file, directory or symbolic link of a ZPL dataset.
/// Child datasets appear under their dataset name, snapshots under
/// <c>&lt;dataset&gt;/.zfs/snapshot/&lt;name&gt;</c>.
/// </summary>
public sealed class ZfsEntry {
  /// <summary>Path from the pool root, '/'-separated.</summary>
  public string Name { get; init; } = "";
  /// <summary>Logical size in bytes; for a symbolic link the length of its target.</summary>
  public long Size { get; init; }
  /// <summary>Whether the entry is a directory.</summary>
  public bool IsDirectory { get; init; }
  /// <summary>Whether the entry is a symbolic link.</summary>
  public bool IsSymlink => this.LinkTarget != null;
  /// <summary>The target of a symbolic link, otherwise null.</summary>
  public string? LinkTarget { get; init; }
  /// <summary>Modification time (UTC), when the object records one.</summary>
  public DateTime? LastModified { get; init; }
  /// <summary>POSIX mode bits, type included.</summary>
  public uint Mode { get; init; }
  /// <summary>Full name of the dataset (or snapshot) the entry belongs to.</summary>
  public string Dataset { get; init; } = "";

  internal ulong ObjectId { get; init; }
  internal ZfsObjectSet? ObjectSet { get; init; }
  internal ZfsDnode? Dnode { get; init; }
}
