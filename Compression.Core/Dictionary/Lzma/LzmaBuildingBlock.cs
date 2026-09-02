using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// Exposes the LZMA algorithm as a benchmarkable building block.
/// Format: 5-byte properties + 4-byte LE uncompressed size + compressed data.
/// </summary>
public sealed class LzmaBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Lzma";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "LZMA";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Lempel-Ziv-Markov chain Algorithm with range coding and sophisticated matching";
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
    var encoder = new LzmaEncoder(dictionarySize: 1 << 20); // 1 MB dictionary for BB
    using var ms = new MemoryStream();

    // Write properties (5 bytes)
    ms.Write(encoder.Properties);
    // Write uncompressed size (4 bytes LE)
    Span<byte> sizeHeader = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(sizeHeader, data.Length);
    ms.Write(sizeHeader);
    // Write compressed data
    encoder.Encode(ms, data);

    return ms.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    var properties = data[..5].ToArray();
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data[5..]);
    using var ms = new MemoryStream(data[9..].ToArray());
    var decoder = new LzmaDecoder(ms, properties, originalSize);
    return decoder.Decode();
  }

  /// <summary>
  /// Decodes a bare LZMA1 stream — no properties byte, no dictionary size, no length
  /// field, just the range-coded data — using coding parameters supplied by the caller.
  /// </summary>
  /// <remarks>
  /// Executable packers and other embedders drop the 13-byte container and keep lc/lp/pb
  /// plus the uncompressed size in a header of their own, so the payload they hand over
  /// starts at the range coder's first byte. A stream that ends before
  /// <paramref name="uncompressedSize"/> bytes have been produced is fed zero bytes, which
  /// mirrors what an in-memory decompressor sees when the packed data is followed by the
  /// zero fill of a section's virtual tail.
  /// </remarks>
  /// <param name="data">The range-coded stream.</param>
  /// <param name="literalContextBits">The number of literal context bits (0-8).</param>
  /// <param name="literalPositionBits">The number of literal position bits (0-4).</param>
  /// <param name="positionBits">The number of position bits (0-4).</param>
  /// <param name="uncompressedSize">The exact number of bytes to produce.</param>
  /// <param name="dictionarySize">The dictionary size in bytes; defaults to <paramref name="uncompressedSize"/>.</param>
  /// <returns>The decompressed data, exactly <paramref name="uncompressedSize"/> bytes long.</returns>
  public static byte[] DecompressRaw(
    ReadOnlySpan<byte> data,
    int literalContextBits,
    int literalPositionBits,
    int positionBits,
    int uncompressedSize,
    int dictionarySize = 0) {
    ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize);

    using var input = new MemoryStream(data.ToArray(), writable: false);
    var decoder = new LzmaDecoder(
      input,
      literalContextBits,
      literalPositionBits,
      positionBits,
      Math.Max(dictionarySize > 0 ? dictionarySize : uncompressedSize, 1),
      uncompressedSize);

    using var output = new MemoryStream(uncompressedSize);
    decoder.Decode(output);
    var result = output.ToArray();
    if (result.Length < uncompressedSize)
      throw new InvalidDataException($"LZMA stream produced {result.Length} bytes, expected {uncompressedSize}.");

    // A final match may straddle the requested end; the surplus bytes are not part of the payload.
    return result.Length == uncompressedSize ? result : result[..uncompressedSize];
  }
}
