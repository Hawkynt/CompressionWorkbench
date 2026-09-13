namespace FileFormat.Pfs0;

/// <summary>
/// Represents a single entry in a Nintendo Switch PartitionFS (PFS0) archive.
/// </summary>
public sealed class Pfs0Entry {
  /// <summary>Gets the entry name (UTF-8, decoded from the string table).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute stream offset where the entry data begins (translated from the on-disk relative offset).</summary>
  public long Offset { get; init; }

  /// <summary>Gets the entry data size in bytes.</summary>
  public long Size { get; init; }
}
