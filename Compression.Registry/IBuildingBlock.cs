namespace Compression.Registry;

/// <summary>
/// A raw compression/decompression building block (algorithm primitive) that can be benchmarked.
/// Unlike <see cref="IFormatDescriptor"/>, building blocks have no file format container —
/// they operate directly on raw byte data.
/// </summary>
public interface IBuildingBlock {
  /// <summary>Unique identifier (e.g. "Lz77", "Deflate").</summary>
  string Id { get; }

  /// <summary>Human-readable display name (e.g. "LZ77", "DEFLATE").</summary>
  string DisplayName { get; }

  /// <summary>Short description of the algorithm.</summary>
  string Description { get; }

  /// <summary>Algorithmic family for grouping.</summary>
  AlgorithmFamily Family { get; }

  /// <summary>Compress raw data and return the compressed bytes.</summary>
  byte[] Compress(ReadOnlySpan<byte> data);

  /// <summary>Decompress previously compressed data and return the original bytes.</summary>
  byte[] Decompress(ReadOnlySpan<byte> data);
}
