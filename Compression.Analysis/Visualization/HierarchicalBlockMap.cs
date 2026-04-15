#pragma warning disable CS1591

namespace Compression.Analysis.Visualization;

/// <summary>
/// Fast hierarchical block map for visualizing large binary files.
/// Divides a stream into a 16x16 grid (256 blocks) at each zoom level.
/// Only reads sample bytes from each block — never loads the full file.
/// </summary>
public sealed class HierarchicalBlockMap {

  /// <summary>Classification of a block's content type.</summary>
  public enum BlockType : byte {
    Empty,           // All zeros
    LowEntropy,      // Headers, text, padding (entropy < 3.0)
    Structured,      // Tables, records (entropy 3.0-5.5)
    Compressed,      // Compressed data (entropy 5.5-7.5)
    Random,          // Encrypted or incompressible (entropy > 7.5)
    HasSignature     // Contains a known format signature
  }

  /// <summary>Stats for a single block in the grid.</summary>
  public sealed class BlockInfo {
    /// <summary>Grid index 0-255 (row * 16 + col).</summary>
    public required int Index { get; init; }
    /// <summary>Absolute byte offset in the stream.</summary>
    public required long Offset { get; init; }
    /// <summary>Block size in bytes.</summary>
    public required long Size { get; init; }
    /// <summary>Shannon entropy (0-8).</summary>
    public required double Entropy { get; init; }
    /// <summary>Content classification.</summary>
    public required BlockType Type { get; init; }
    /// <summary>Whether all sampled bytes are zero.</summary>
    public required bool IsZero { get; init; }
    /// <summary>Detected format signature name, if any.</summary>
    public string? SignatureName { get; init; }
    /// <summary>Unique byte count in sample.</summary>
    public required int UniqueBytes { get; init; }
    /// <summary>Most frequent byte value.</summary>
    public required byte DominantByte { get; init; }
    /// <summary>Can this block be further expanded (size > SampleSize)?</summary>
    public required bool CanExpand { get; init; }
  }

  /// <summary>Result of analyzing one zoom level.</summary>
  public sealed class LevelResult {
    /// <summary>The 256 blocks (16x16 grid).</summary>
    public required BlockInfo[] Blocks { get; init; }
    /// <summary>Absolute offset of this level's region in the stream.</summary>
    public required long RegionOffset { get; init; }
    /// <summary>Total size of this level's region.</summary>
    public required long RegionSize { get; init; }
    /// <summary>Zoom depth (0 = full file).</summary>
    public required int Depth { get; init; }
    /// <summary>Human-readable path (e.g. "Full file > Block 42 > Block 7").</summary>
    public required string Path { get; init; }
  }

  private const int GridSize = 16;
  private const int BlockCount = GridSize * GridSize; // 256
  private const int SampleSize = 4096; // bytes to read per block for entropy analysis
  private const int SignatureScanSize = 64; // bytes to check for magic signatures

  private static readonly (string Name, byte[] Magic)[] Signatures = [
    ("ZIP", [0x50, 0x4B, 0x03, 0x04]),
    ("7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]),
    ("RAR", [0x52, 0x61, 0x72, 0x21]),
    ("GZIP", [0x1F, 0x8B]),
    ("BZ2", [0x42, 0x5A, 0x68]),
    ("XZ", [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]),
    ("Zstd", [0x28, 0xB5, 0x2F, 0xFD]),
    ("LZ4", [0x04, 0x22, 0x4D, 0x18]),
    ("PDF", [0x25, 0x50, 0x44, 0x46]),
    ("PNG", [0x89, 0x50, 0x4E, 0x47]),
    ("JPEG", [0xFF, 0xD8, 0xFF]),
    ("PE/EXE", [0x4D, 0x5A]),
    ("ELF", [0x7F, 0x45, 0x4C, 0x46]),
    ("SQLite", [0x53, 0x51, 0x4C, 0x69, 0x74, 0x65]),
    ("NTFS", [0xEB, 0x52, 0x90, 0x4E, 0x54, 0x46, 0x53]),
    ("FAT", [0xEB, 0x3C, 0x90]),
    ("ext", [0x53, 0xEF]), // at offset 0x438 — handled separately
    ("MBR boot", [0x55, 0xAA]), // at offset 510
    ("GPT", [0x45, 0x46, 0x49, 0x20, 0x50, 0x41, 0x52, 0x54]),
    ("FLAC", [0x66, 0x4C, 0x61, 0x43]),
    ("OGG", [0x4F, 0x67, 0x67, 0x53]),
    ("RIFF", [0x52, 0x49, 0x46, 0x46]),
    ("TAR", [0x75, 0x73, 0x74, 0x61, 0x72]), // at offset 257
    ("zlib", [0x78, 0x9C]),
    ("LZMA", [0x5D, 0x00, 0x00]),
    ("Brotli", [0xCE, 0xB2, 0xCF, 0x81]),
    ("OLE2/CFB", [0xD0, 0xCF, 0x11, 0xE0]),
    ("VMDK", [0x4B, 0x44, 0x4D, 0x56]),
    ("VHD", [0x63, 0x6F, 0x6E, 0x65, 0x63, 0x74, 0x69, 0x78]),
    ("QCOW", [0x51, 0x46, 0x49, 0xFB]),
    ("VDI", [0x3C, 0x3C, 0x3C, 0x20]),
  ];

  /// <summary>
  /// Analyzes a region of the stream as a 16x16 grid. Fast — only samples each block.
  /// </summary>
  /// <param name="stream">Seekable stream to analyze.</param>
  /// <param name="regionOffset">Start offset of the region.</param>
  /// <param name="regionSize">Size of the region (0 = entire stream from offset).</param>
  /// <param name="depth">Zoom depth for display.</param>
  /// <param name="path">Human-readable navigation path.</param>
  public static LevelResult Analyze(Stream stream, long regionOffset = 0, long regionSize = 0, int depth = 0, string path = "Full file") {
    if (regionSize <= 0)
      regionSize = stream.Length - regionOffset;

    var blockSize = regionSize / BlockCount;
    if (blockSize == 0) blockSize = 1;

    var blocks = new BlockInfo[BlockCount];
    var buffer = new byte[Math.Min(SampleSize, blockSize)];
    Span<int> freq = stackalloc int[256];

    for (var i = 0; i < BlockCount; i++) {
      var blockOffset = regionOffset + i * blockSize;
      var actualBlockSize = Math.Min(blockSize, stream.Length - blockOffset);
      if (actualBlockSize <= 0) {
        blocks[i] = MakeEmptyBlock(i, blockOffset, 0);
        continue;
      }

      // Read sample from the start of the block.
      var sampleLen = (int)Math.Min(buffer.Length, actualBlockSize);
      stream.Position = blockOffset;
      var bytesRead = stream.Read(buffer, 0, sampleLen);
      if (bytesRead == 0) {
        blocks[i] = MakeEmptyBlock(i, blockOffset, actualBlockSize);
        continue;
      }

      var sample = buffer.AsSpan(0, bytesRead);

      // Compute entropy.
      freq.Clear();
      var isZero = true;
      foreach (var b in sample) {
        freq[b]++;
        if (b != 0) isZero = false;
      }

      var entropy = 0.0;
      var len = (double)bytesRead;
      var uniqueBytes = 0;
      var maxFreq = 0;
      byte dominantByte = 0;
      for (var j = 0; j < 256; j++) {
        if (freq[j] == 0) continue;
        uniqueBytes++;
        if (freq[j] > maxFreq) { maxFreq = freq[j]; dominantByte = (byte)j; }
        var p = freq[j] / len;
        entropy -= p * Math.Log2(p);
      }

      // Check for signatures.
      string? sigName = null;
      var scanLen = Math.Min(SignatureScanSize, bytesRead);
      var scanSpan = sample[..scanLen];
      foreach (var (name, magic) in Signatures) {
        if (magic.Length <= scanLen && scanSpan[..magic.Length].SequenceEqual(magic)) {
          sigName = name;
          break;
        }
      }

      // Classify.
      var type = isZero ? BlockType.Empty
        : sigName != null ? BlockType.HasSignature
        : entropy < 3.0 ? BlockType.LowEntropy
        : entropy < 5.5 ? BlockType.Structured
        : entropy < 7.5 ? BlockType.Compressed
        : BlockType.Random;

      blocks[i] = new() {
        Index = i,
        Offset = blockOffset,
        Size = actualBlockSize,
        Entropy = entropy,
        Type = type,
        IsZero = isZero,
        SignatureName = sigName,
        UniqueBytes = uniqueBytes,
        DominantByte = dominantByte,
        CanExpand = actualBlockSize > SampleSize
      };
    }

    return new() {
      Blocks = blocks,
      RegionOffset = regionOffset,
      RegionSize = regionSize,
      Depth = depth,
      Path = path
    };
  }

  /// <summary>
  /// Computes the child region for drilling into a specific block.
  /// </summary>
  public static (long Offset, long Size, string Path) GetChildRegion(LevelResult parent, int blockIndex) {
    var block = parent.Blocks[blockIndex];
    var newPath = $"{parent.Path} > Block {blockIndex} ({FormatSize(block.Offset)})";
    return (block.Offset, block.Size, newPath);
  }

  private static BlockInfo MakeEmptyBlock(int index, long offset, long size) => new() {
    Index = index, Offset = offset, Size = size, Entropy = 0,
    Type = BlockType.Empty, IsZero = true, UniqueBytes = 0, DominantByte = 0, CanExpand = false
  };

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
  };
}
