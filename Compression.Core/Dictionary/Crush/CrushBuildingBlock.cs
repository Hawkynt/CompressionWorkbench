using Compression.Registry;

namespace Compression.Core.Dictionary.Crush;

/// <summary>
/// CRUSH — Ilya Muravyov's LZ77 file compressor (version 1.00). The block's bytes are a CRUSH
/// file exactly as the reference program's <c>c</c> command writes it; see
/// <see cref="CrushCodec"/>.
/// </summary>
public sealed class CrushBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Crush";
  /// <inheritdoc/>
  public string DisplayName => "Crush";
  /// <inheritdoc/>
  public string Description => "Muravyov's CRUSH 1.00: LZ77 with a 2 MiB window, two hash chains and prefix-coded lengths and offset slots";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => CrushCodec.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => CrushCodec.Decompress(data);
}
