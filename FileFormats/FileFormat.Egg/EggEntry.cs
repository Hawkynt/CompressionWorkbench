namespace FileFormat.Egg;

/// <summary>
/// A single physical data block inside an EGG file entry. A file may be split
/// across several blocks (e.g. a file larger than 4&#160;GB, whose per-block sizes
/// are 32-bit); the decompressed file is the ordered concatenation of its blocks.
/// </summary>
/// <param name="DataOffset">Absolute stream offset of the first compressed byte of the block.</param>
/// <param name="CompressedSize">Number of compressed bytes stored for the block.</param>
/// <param name="UncompressedSize">Number of bytes the block expands to.</param>
/// <param name="Algorithm">Block algorithm number (0=Store, 1=Deflate, 2=Bzip2, 3=AZO, 4=LZMA).</param>
/// <param name="Crc32">Stored CRC-32 of the uncompressed block.</param>
internal readonly record struct EggBlock(
  long DataOffset,
  long CompressedSize,
  long UncompressedSize,
  int Algorithm,
  uint Crc32
);

/// <summary>
/// A file entry parsed from an EGG (ALZip) archive.
/// </summary>
public sealed class EggEntry {

  /// <summary>Relative path/name of the entry (forward-slash separated).</summary>
  public string Name { get; internal set; } = "";

  /// <summary>Total uncompressed size of the file, as recorded in the File Header.</summary>
  public long UncompressedSize { get; internal set; }

  /// <summary>Sum of the compressed sizes of the entry's data blocks.</summary>
  public long CompressedSize { get; internal set; }

  /// <summary>True when the entry is a directory (per the Windows/Posix file-information header).</summary>
  public bool IsDirectory { get; internal set; }

  /// <summary>True when the entry (name and/or data) is encrypted; extraction is not supported.</summary>
  public bool IsEncrypted { get; internal set; }

  /// <summary>Last-modified timestamp (UTC) when a file-information header supplied one.</summary>
  public DateTime? LastModified { get; internal set; }

  /// <summary>Algorithm number of the first data block (0=Store, 1=Deflate, 2=Bzip2, 3=AZO, 4=LZMA).</summary>
  public int PrimaryAlgorithm { get; internal set; }

  /// <summary>Human-readable name of <see cref="PrimaryAlgorithm"/>.</summary>
  public string MethodName => PrimaryAlgorithm switch {
    0 => "Store",
    1 => "Deflate",
    2 => "Bzip2",
    3 => "AZO",
    4 => "LZMA",
    _ => $"Method{PrimaryAlgorithm}",
  };

  /// <summary>The physical data blocks that make up the entry, in order.</summary>
  internal List<EggBlock> Blocks { get; } = [];
}
