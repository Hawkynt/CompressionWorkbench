namespace FileFormat.DiskDoubler;

/// <summary>Represents a single compressed file stored in a DiskDoubler file.</summary>
public sealed class DiskDoublerEntry {
  /// <summary>Gets the original filename.</summary>
  public string Name { get; init; } = string.Empty;

  /// <summary>Gets the compression method ID (0 = stored, 1 = RLE, 3 = LZC variant, others = proprietary).</summary>
  public int Method { get; init; }

  /// <summary>Gets the original (uncompressed) size in bytes.</summary>
  public long OriginalSize { get; init; }

  /// <summary>Gets the compressed size in bytes.</summary>
  public long CompressedSize { get; init; }

  /// <summary>Gets the absolute byte offset of the compressed data within the source stream.</summary>
  public long DataOffset { get; init; }

  /// <summary>Gets whether this entry represents the data fork (as opposed to the resource fork).</summary>
  public bool IsDataFork { get; init; }
}
