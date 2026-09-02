using System.Buffers.Binary;
using Compression.Core.BitIO;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzmw;

/// <summary>
/// Exposes the LZMW algorithm as a benchmarkable building block.
/// </summary>
public sealed class LzmwBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Lzmw";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "LZMW";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "Miller-Wegman LZW variant: adds the concatenation of the previous AND entire current match "
    + "to the dictionary, instead of LZW's previous match plus one character";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int MinBits = 9;
  private const int MaxBits = 16;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();

    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0)
      return ms.ToArray();

    var encoder = new LzmwEncoder(ms, MinBits, MaxBits, BitOrder.LsbFirst);
    encoder.Encode(data);
    return ms.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    using var input = new MemoryStream(data[4..].ToArray());
    var decoder = new LzmwDecoder(input, MinBits, MaxBits, BitOrder.LsbFirst);
    return decoder.Decode(originalSize);
  }
}
