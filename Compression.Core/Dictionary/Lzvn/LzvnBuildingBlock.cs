using Compression.Registry;

namespace Compression.Core.Dictionary.Lzvn;

/// <summary>
/// Exposes LZVN as a benchmarkable building block.
/// </summary>
/// <remarks>
/// See <see cref="LzvnConstants"/> for the format layout and provenance notes: this
/// is an original opcode stream following the documented shape of Apple's LZVN
/// codec, not a byte-exact reproduction of its (undocumented) real bitstream.
/// </remarks>
public sealed class LzvnBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Lzvn";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "LZVN";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Byte-oriented opcode LZ77 in the spirit of Apple's fast LZVN codec, with tiered distance encoding";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) =>
    LzvnCompressor.Compress(data);

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) =>
    LzvnDecompressor.Decompress(data);
}
