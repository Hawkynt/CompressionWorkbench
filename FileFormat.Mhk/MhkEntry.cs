namespace FileFormat.Mhk;

/// <summary>
/// Represents a single resource entry in a Cyan Mohawk (MHK) archive.
/// Resources are uniquely identified by the (Type, Id) pair; names are optional metadata.
/// </summary>
public sealed class MhkEntry {
  /// <summary>Gets the 4-character ASCII FourCC resource type tag (e.g. "tBMP", "tWAV", "NAME").</summary>
  public string Type { get; init; } = "";

  /// <summary>Gets the game-specific 16-bit resource identifier.</summary>
  public ushort Id { get; init; }

  /// <summary>Gets the optional resource name from the per-type name table (may be null).</summary>
  public string? Name { get; init; }

  /// <summary>Gets the synthetic display name used by tools: <c>TYPE_id</c> or <c>TYPE_id_name</c> if a name is present.</summary>
  public string DisplayName { get; init; } = "";

  /// <summary>Gets the absolute byte offset of the file's data inside the MHK stream.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the file size in bytes (encoded as 24+8 bits in the file table).</summary>
  public long Size { get; init; }
}
