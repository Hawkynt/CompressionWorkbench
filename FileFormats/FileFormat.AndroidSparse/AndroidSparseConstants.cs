namespace FileFormat.AndroidSparse;

/// <summary>
/// On-disk constants for the Android sparse image format
/// (AOSP <c>system/core/libsparse/sparse_format.h</c>).
/// </summary>
internal static class AndroidSparseConstants {
  /// <summary>
  /// Sparse image magic. On disk the first four bytes are <c>3A FF 26 ED</c>
  /// (the sequence written <c>0x3AFF26ED</c>), which read as a little-endian
  /// u32 yields <c>0xED26FF3A</c> — matching libsparse's <c>SPARSE_HEADER_MAGIC</c>.
  /// </summary>
  public const uint Magic = 0xED26FF3A;

  /// <summary>Size in bytes of the sparse file header.</summary>
  public const int FileHeaderSize = 28;

  /// <summary>Size in bytes of each chunk header.</summary>
  public const int ChunkHeaderSize = 12;

  /// <summary>Raw chunk: <c>chunk_sz * blk_sz</c> literal bytes follow.</summary>
  public const ushort ChunkTypeRaw = 0xCAC1;

  /// <summary>Fill chunk: a single 4-byte pattern replicated over the region.</summary>
  public const ushort ChunkTypeFill = 0xCAC2;

  /// <summary>Don't-care chunk: skipped region, materialised as zero bytes.</summary>
  public const ushort ChunkTypeDontCare = 0xCAC3;

  /// <summary>CRC32 chunk: a 4-byte running CRC, carries no output blocks.</summary>
  public const ushort ChunkTypeCrc32 = 0xCAC4;

  /// <summary>Major version emitted by <c>Create</c>.</summary>
  public const ushort MajorVersion = 1;

  /// <summary>Minor version emitted by <c>Create</c>.</summary>
  public const ushort MinorVersion = 0;

  /// <summary>Default output block size used by <c>img2simg</c> and by <c>Create</c>.</summary>
  public const uint DefaultBlockSize = 4096;
}
