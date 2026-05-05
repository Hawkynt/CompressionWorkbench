namespace FileFormat.Lzx;

/// <summary>
/// Represents a single entry in an Amiga LZX archive.
/// </summary>
public sealed class LzxAmigaEntry {
  /// <summary>Gets the filename of the entry.</summary>
  public required string FileName { get; init; }

  /// <summary>Gets the uncompressed size in bytes.</summary>
  public required uint OriginalSize { get; init; }

  /// <summary>
  /// Gets the compressed size in bytes. May be 0 for merged (solid) entries
  /// whose data is part of the group's final entry.
  /// </summary>
  public required uint CompressedSize { get; init; }

  /// <summary>Gets the compression method (0 = Stored, 2 = LZX).</summary>
  public required byte Method { get; init; }

  /// <summary>Gets the Amiga file attributes (RWED bits etc.).</summary>
  public required uint Attributes { get; init; }

  /// <summary>Gets the last-modified timestamp.</summary>
  public required DateTime LastModified { get; init; }

  /// <summary>Gets the entry comment, or an empty string if none.</summary>
  public required string Comment { get; init; }

  /// <summary>Gets the CRC-32 of the uncompressed data.</summary>
  public required uint Crc { get; init; }

  /// <summary>
  /// Gets whether this entry is merged with the following entry (solid group).
  /// When <see langword="true"/>, this entry's compressed data is combined with
  /// subsequent entries until a non-merged entry is reached.
  /// </summary>
  public required bool IsMerged { get; init; }

  /// <summary>Gets the machine type (0 = Amiga, 1 = Unix, 2 = PC).</summary>
  public required byte MachineType { get; init; }

  /// <summary>Gets the byte offset in the archive stream where compressed data begins (internal use).</summary>
  internal long DataOffset { get; init; }

  /// <summary>Gets the index of this entry within its merged group (internal use).</summary>
  internal int GroupIndex { get; init; }

  /// <summary>Gets the total number of entries in the merged group (internal use).</summary>
  internal int GroupSize { get; init; }

  /// <summary>Gets the index of the final entry in the group that holds the compressed data (internal use).</summary>
  internal int GroupDataEntryIndex { get; init; }
}
