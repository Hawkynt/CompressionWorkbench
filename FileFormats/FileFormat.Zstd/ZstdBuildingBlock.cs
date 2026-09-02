using Compression.Core.Streams;
using Compression.Registry;

namespace FileFormat.Zstd;

/// <summary>
/// Exposes Zstandard as a benchmarkable building block.
/// Produces a spec-compliant Zstandard frame (magic 0xFD2FB528, frame header,
/// data blocks, content checksum), so the payload is self-terminating and no extra
/// uncompressed-size header is prepended.
/// </summary>
/// <remarks>
/// Frame and block layout per RFC 8878 ("Zstandard Compression and the
/// application/zstd Media Type"), sections 3.1.1 (frame) and 3.1.1.2 (blocks).
/// </remarks>
public sealed class ZstdBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Zstd";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Zstandard";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "LZ77 matching with FSE and Huffman entropy stages, designed by Facebook (RFC 8878)";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    using (var zstd = new ZstdStream(output, CompressionStreamMode.Compress, ZstdCompressionLevel.Default, leaveOpen: true))
      zstd.Write(data);

    return output.ToArray();
  }

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray());
    using var zstd = new ZstdStream(input, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    zstd.CopyTo(output);
    return output.ToArray();
  }
}
