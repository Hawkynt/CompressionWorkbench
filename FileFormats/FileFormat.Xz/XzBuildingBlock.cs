using Compression.Core.Streams;
using Compression.Registry;

namespace FileFormat.Xz;

/// <summary>
/// Exposes XZ (LZMA2 inside the .xz container) as a benchmarkable building block.
/// Produces a complete .xz stream — stream header with the magic 0xFD "7zXZ" 0x00,
/// one block carrying the LZMA2 filter chain, the index and the stream footer
/// ending in "YZ" — so the payload is self-terminating and no extra
/// uncompressed-size header is prepended.
/// </summary>
/// <remarks>
/// Container layout per "The .xz File Format" version 1.2.0 (Lasse Collin),
/// sections 2.1 (stream header), 3 (block), 4 (index) and 2.2 (stream footer);
/// the LZMA2 chunk framing inside the block follows the same specification's
/// filter list, filter ID 0x21.
/// </remarks>
public sealed class XzBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Xz";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "XZ/LZMA2";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "LZMA2 range coding wrapped in the .xz container with CRC-64 integrity checking";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <summary>LZMA2 dictionary size used for the benchmark stream (1 MB).</summary>
  private const int DictionarySize = 1 << 20;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    using (var xz = new XzStream(output, CompressionStreamMode.Compress, DictionarySize, leaveOpen: true))
      xz.Write(data);

    return output.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray());
    using var xz = new XzStream(input, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    xz.CopyTo(output);
    return output.ToArray();
  }
}
