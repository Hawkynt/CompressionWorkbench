namespace FileFormat.AndroidSparse;

/// <summary>Parsed sparse-image file header (28 bytes, little-endian).</summary>
/// <param name="Magic">Sparse magic (<c>0x3AFF26ED</c>).</param>
/// <param name="MajorVersion">Major format version.</param>
/// <param name="MinorVersion">Minor format version.</param>
/// <param name="FileHeaderSize">Declared size of the file header (28).</param>
/// <param name="ChunkHeaderSize">Declared size of each chunk header (12).</param>
/// <param name="BlockSize">Block size in bytes (must be a multiple of 4).</param>
/// <param name="TotalBlocks">Number of output blocks in the expanded image.</param>
/// <param name="TotalChunks">Number of chunk records that follow.</param>
/// <param name="ImageChecksum">CRC32 of the expanded image (0 when unused).</param>
internal readonly record struct AndroidSparseHeader(
  uint Magic,
  ushort MajorVersion,
  ushort MinorVersion,
  ushort FileHeaderSize,
  ushort ChunkHeaderSize,
  uint BlockSize,
  uint TotalBlocks,
  uint TotalChunks,
  uint ImageChecksum) {

  /// <summary>Expanded (raw) image length in bytes.</summary>
  public long ExpandedLength => (long)this.TotalBlocks * this.BlockSize;
}
