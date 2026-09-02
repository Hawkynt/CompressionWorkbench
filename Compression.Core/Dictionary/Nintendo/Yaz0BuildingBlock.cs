using Compression.Registry;

namespace Compression.Core.Dictionary.Nintendo;

/// <summary>
/// Exposes Nintendo Yaz0 grouped-flag LZ compression as a benchmarkable building block.
/// </summary>
/// <remarks>
/// Yaz0 uses a 4 KiB sliding window, literals/back-references selected by MSB-first
/// flag bytes, and a 16-byte big-endian header. Reference:
/// https://www.amnoid.de/gc/yaz0.txt
/// </remarks>
public sealed class Yaz0BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Yaz0";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Yaz0";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Nintendo Yaz0 grouped-flag LZ compression";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => NintendoLzCodecs.CompressYaz0(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => NintendoLzCodecs.DecompressYaz0(data);
}
