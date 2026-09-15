namespace FileFormat.Mtree;

/// <summary>Filesystem object types described by an mtree manifest.</summary>
public enum MtreeEntryType {
  Unknown,
  File,
  Directory,
  Link,
  Block,
  Character,
  Fifo,
  Socket,
}

/// <summary>One filesystem object described by an mtree manifest.</summary>
public sealed class MtreeEntry {
  /// <summary>Gets or sets the slash-separated path relative to the manifest root.</summary>
  public string Path { get; set; } = "";

  /// <summary>Gets or sets the declared object type.</summary>
  public MtreeEntryType Type { get; set; }

  /// <summary>Gets or sets the declared byte size, when present.</summary>
  public long? Size { get; set; }

  /// <summary>Gets or sets the numeric POSIX mode, when present and numeric.</summary>
  public uint? Mode { get; set; }

  /// <summary>Gets or sets the numeric owner id, when present.</summary>
  public uint? Uid { get; set; }

  /// <summary>Gets or sets the numeric group id, when present.</summary>
  public uint? Gid { get; set; }

  /// <summary>Gets or sets the declared modification time, when present.</summary>
  public DateTimeOffset? ModificationTime { get; set; }

  /// <summary>Gets or sets the symbolic-link target, when <see cref="Type"/> is <see cref="MtreeEntryType.Link"/>.</summary>
  public string? LinkTarget { get; set; }

  /// <summary>Gets or sets the external contents path declared by the non-archival <c>contents=</c> keyword.</summary>
  public string? ContentsPath { get; set; }

  /// <summary>
  /// Gets the effective keyword set after applying <c>/set</c> defaults.
  /// Flag-style keywords have a null value.
  /// </summary>
  public IReadOnlyDictionary<string, string?> Keywords { get; internal set; }
    = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

  /// <summary>Gets whether the entry represents a directory.</summary>
  public bool IsDirectory => this.Type == MtreeEntryType.Directory;

  /// <summary>Gets whether the entry represents a symbolic link.</summary>
  public bool IsSymlink => this.Type == MtreeEntryType.Link;
}
