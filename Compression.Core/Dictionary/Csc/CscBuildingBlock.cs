using Compression.Registry;

namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// Exposes libcsc (Fu Siyuan's CSC) as a benchmarkable building block. The payload is a complete
/// libcsc stream — the 10-byte property header plus the coded blocks — exactly as the reference
/// <c>csc</c> tool writes it at its default level 2.
/// </summary>
public sealed class CscBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Csc";
  /// <inheritdoc/>
  public string DisplayName => "CSC";
  /// <inheritdoc/>
  public string Description => "libcsc: LZ77 with LZMA-style range coding, optimal parsing and per-block data filters";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => CscCodec.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => CscCodec.Decompress(data);
}
