namespace FileFormat.Slf;

/// <summary>
/// Represents one active entry inside a Sir-Tech SLF library (Jagged Alliance 2).
/// </summary>
public sealed class SlfEntry {
  /// <summary>Gets the entry path relative to the archive's <c>LibPath</c> (uses backslashes, e.g. <c>"sti\\bigicon.sti"</c>).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute byte offset of the entry's payload from the start of the SLF stream.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the entry payload size in bytes. SLF stores payloads uncompressed, so this is the on-disk size.</summary>
  public long Size { get; init; }

  /// <summary>Gets the entry's last-modified timestamp as decoded from the on-disk Windows FILETIME.</summary>
  public DateTime LastModified { get; init; }
}
