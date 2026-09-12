namespace FileFormat.Vpp;

/// <summary>
/// Represents a single file entry in a VPP_PC v1 archive.
/// </summary>
public sealed class VppEntry {
  /// <summary>Gets the entry name (up to 59 ASCII characters; null-terminator excluded).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute byte offset of the entry data within the archive stream.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the size of the entry data in bytes (unaligned, i.e. payload length).</summary>
  public long Size { get; init; }
}
