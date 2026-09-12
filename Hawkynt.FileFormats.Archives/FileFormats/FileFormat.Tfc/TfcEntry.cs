namespace FileFormat.Tfc;

/// <summary>
/// Represents a single texture mip-level bundle inside a Mass Effect TFC cache.
/// </summary>
/// <remarks>
/// The reader surfaces each bundle as one entry whose payload (<see cref="Size"/> bytes) is the
/// raw concatenation of the per-block size table and the compressed/stored block data — i.e. all
/// bytes after the 16-byte chunk header. <see cref="IsCompressed"/> indicates whether the bundle's
/// blocks are LZX-compressed; LZX decompression is intentionally out of scope here, so callers
/// receive the opaque compressed bytes.
/// </remarks>
public sealed class TfcEntry {
  /// <summary>Synthetic bundle name (zero-padded, e.g. <c>bundle_00000.bin</c>).</summary>
  public string Name { get; init; } = "";

  /// <summary>Absolute offset of the bundle's chunk header within the TFC stream.</summary>
  public long Offset { get; init; }

  /// <summary>Total size of the entry's payload in bytes (block-size table + block data).</summary>
  public long Size { get; init; }

  /// <summary>Sum of per-block compressed sizes, as declared in the bundle's chunk header.</summary>
  public long CompressedSize { get; init; }

  /// <summary>Sum of per-block uncompressed sizes, as declared in the bundle's chunk header.</summary>
  public long UncompressedSize { get; init; }

  /// <summary>Nominal block size declared by the bundle (typically 128 KiB).</summary>
  public uint BlockSize { get; init; }

  /// <summary>True when <see cref="CompressedSize"/> differs from <see cref="UncompressedSize"/>; blocks are LZX-compressed.</summary>
  public bool IsCompressed { get; init; }
}
