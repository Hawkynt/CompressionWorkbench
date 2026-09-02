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
  public string Id => "BB_Lzvn";
  /// <inheritdoc/>
  public string DisplayName => "LZVN";
  /// <inheritdoc/>
  public string Description => "Byte-oriented opcode LZ77 in the spirit of Apple's fast LZVN codec, with tiered distance encoding";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) =>
    LzvnCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) =>
    LzvnDecompressor.Decompress(data);
}
