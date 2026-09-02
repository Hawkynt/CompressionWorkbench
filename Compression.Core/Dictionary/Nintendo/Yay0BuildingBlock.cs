using Compression.Registry;

namespace Compression.Core.Dictionary.Nintendo;

/// <summary>
/// Exposes Nintendo Yay0 split-table LZ compression as a benchmarkable building block.
/// </summary>
/// <remarks>
/// Yay0 uses the same 4 KiB LZ reference grammar as Yaz0 but stores 32-bit mask words,
/// 16-bit link records, and literal/long-length bytes in separate tables. Reference:
/// https://www.amnoid.de/gc/yay0.txt
/// </remarks>
public sealed class Yay0BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Yay0";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Yay0";

  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Nintendo Yay0 split-table LZ compression";

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
    => NintendoLzCodecs.CompressYay0(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => NintendoLzCodecs.DecompressYay0(data);
}
