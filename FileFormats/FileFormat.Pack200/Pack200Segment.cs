namespace FileFormat.Pack200;

/// <summary>
/// Decode status of a Pack200 segment. Listing must never throw, so an
/// incompletely-decoded segment reports <see cref="Partial"/> with a reason.
/// </summary>
public enum Pack200DecodeStatus {
  /// <summary>Header and class names were decoded successfully.</summary>
  Full,

  /// <summary>Header decoded, but class names could not be fully resolved.</summary>
  Partial,
}

/// <summary>
/// The parsed contents of a single Pack200 (JSR-200) segment: the archive header
/// fields plus the internal names of the classes it defines.
/// </summary>
public sealed class Pack200Segment {
  /// <summary>Archive minor version.</summary>
  public int MinVersion { get; init; }

  /// <summary>Archive major version.</summary>
  public int MajVersion { get; init; }

  /// <summary>Archive option bit flags (AO_* in the specification).</summary>
  public int Options { get; init; }

  /// <summary>Default class-file minor version for classes in this segment.</summary>
  public int DefaultClassMinVersion { get; init; }

  /// <summary>Default class-file major version for classes in this segment.</summary>
  public int DefaultClassMajVersion { get; init; }

  /// <summary>Number of constant-pool UTF-8 entries.</summary>
  public int Utf8Count { get; init; }

  /// <summary>Number of constant-pool Class entries.</summary>
  public int ClassPoolCount { get; init; }

  /// <summary>Number of classes defined in this segment.</summary>
  public int ClassCount { get; init; }

  /// <summary>Number of non-class resource files carried in this segment.</summary>
  public int ResourceFileCount { get; init; }

  /// <summary>Modification time (seconds since the Unix epoch) recorded in the archive header, if present.</summary>
  public long ModTime { get; init; }

  /// <summary>Internal names (e.g. <c>java/lang/Object</c>) of the classes this segment defines.</summary>
  public IReadOnlyList<string> ClassNames { get; init; } = [];

  /// <summary>Whether the segment was fully decoded or only partially.</summary>
  public Pack200DecodeStatus Status { get; init; }

  /// <summary>Human-readable note describing why decoding was partial, if applicable.</summary>
  public string? StatusNote { get; init; }
}
