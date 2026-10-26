namespace FileFormat.DiskDoubler;

/// <summary>Represents one fork of a DiskDoubler compressed file.</summary>
public sealed class DiskDoublerEntry {
  /// <summary>Gets the entry name: the file's name for the data fork, with ".rsrc" appended for the resource fork.</summary>
  public string Name { get; init; } = string.Empty;

  /// <summary>
  /// Gets the compression method: 0 stored, 1 Unix compress, 2 and 5 DiskDoubler Huffman variants,
  /// 3 RLE, 4 Huffman, 6 and 9 "AD" LZSS, 7 Stac LZS, 8 Compact Pro, 10 "DD". The high bit is a flag
  /// and is masked off.
  /// </summary>
  public int Method { get; init; }

  /// <summary>Gets the original (uncompressed) size in bytes.</summary>
  public long OriginalSize { get; init; }

  /// <summary>Gets the compressed size in bytes.</summary>
  public long CompressedSize { get; init; }

  /// <summary>Gets the absolute byte offset of the compressed data within the source stream.</summary>
  public long DataOffset { get; init; }

  /// <summary>Gets whether this entry represents the data fork (as opposed to the resource fork).</summary>
  public bool IsDataFork { get; init; }

  /// <summary>Gets the fork's checksum field (its meaning depends on the method).</summary>
  public ushort Checksum { get; init; }

  /// <summary>Gets the fork's delta pre-processing type (0 = none).</summary>
  public int DeltaType { get; init; }

  /// <summary>Gets the Mac file type code.</summary>
  public string FileType { get; init; } = string.Empty;

  /// <summary>Gets the Mac creator code.</summary>
  public string Creator { get; init; } = string.Empty;

  /// <summary>Gets the modification time, or <see langword="null"/> when the header holds zero.</summary>
  public DateTime? Modified { get; init; }

  /// <summary>Gets whether this reader can decode the entry's method.</summary>
  public bool IsSupported => this.DeltaType == 0 && this.Method is
    DiskDoublerConstants.MethodStored or DiskDoublerConstants.MethodCompress or
    DiskDoublerConstants.MethodAds or DiskDoublerConstants.MethodAd1;

  /// <summary>The XOR key the encoder applied to compress output (0 or 0x5A), from the header's version bytes.</summary>
  internal byte XorKey { get; init; }
}
