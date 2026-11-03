using Compression.Registry;

namespace Compression.Core.Dictionary.Balz;

/// <summary>
/// BALZ — Ilya Muravyov's ROLZ compressor (version 1.20). The block's bytes are a
/// <c>.balz</c> file exactly as the reference program's <c>c</c> command writes it; see
/// <see cref="BalzCodec"/>.
/// </summary>
public sealed class BalzBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Balz";
  /// <inheritdoc/>
  public string DisplayName => "BALZ";
  /// <inheritdoc/>
  public string Description => "Muravyov's BALZ 1.20: ROLZ with 128 positions per two-byte context, binary arithmetic coding and an E8/E9 filter";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BalzCodec.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BalzCodec.Decompress(data);
}
