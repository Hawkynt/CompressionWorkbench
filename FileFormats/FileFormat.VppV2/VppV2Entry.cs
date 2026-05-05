namespace FileFormat.VppV2;

/// <summary>
/// Represents a single file entry in a VPP v2 (Saint's Row 2) archive.
/// </summary>
public sealed class VppV2Entry {
  /// <summary>Gets the entry's full name (path) as stored in the name table.</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute byte offset of the entry payload within the archive stream.</summary>
  public long DataOffset { get; init; }

  /// <summary>Gets the uncompressed payload size in bytes.</summary>
  public long DataSize { get; init; }

  /// <summary>Gets the on-disk payload size in bytes (equals <see cref="DataSize"/> when stored uncompressed).</summary>
  public long CompressedSize { get; init; }

  /// <summary>Gets a value indicating whether this entry's payload is zlib-compressed (raw deflate).</summary>
  public bool IsCompressed { get; init; }
}
