namespace FileFormat.Mix;

/// <summary>
/// Represents a single entry in a Westwood TD/RA1 MIX archive.
/// </summary>
/// <remarks>
/// MIX files store only the 32-bit Westwood ID hash, not the original filename. The reader
/// surfaces a synthetic display name in the form <c>&lt;0xXXXXXXXX&gt;</c> when no name database
/// is available. The writer accepts a real name and computes the ID via <see cref="WestwoodCrc"/>.
/// </remarks>
public sealed class MixEntry {
  /// <summary>Gets the Westwood 32-bit ID hash of the original filename.</summary>
  public uint Id { get; init; }

  /// <summary>Gets the entry name. May be the original filename (writer side) or a synthetic <c>&lt;0xXXXXXXXX&gt;</c> placeholder (reader side).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the offset of the entry data relative to the start of the body section.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the entry size in bytes.</summary>
  public long Size { get; init; }
}
