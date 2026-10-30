using Compression.Registry;
using FileFormat.Lzfse;

namespace Compression.Core.Dictionary.Lzvn;

/// <summary>
/// Exposes Apple's LZVN as a benchmarkable building block.
/// </summary>
/// <remarks>
/// The block is the LZVN codec of <see cref="LzfseStream"/>, not a second one. Compressing
/// yields a bare LZVN stream ending with its <c>eos</c> instruction: the payload of an LZFSE
/// <c>bvxn</c> block and what HFS+/APFS transparent compression stores. Decompressing runs to
/// that <c>eos</c>, so no length travels with the stream.
/// </remarks>
public sealed class LzvnBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzvn";
  /// <inheritdoc/>
  public string DisplayName => "LZVN";
  /// <inheritdoc/>
  public string Description => "Apple's LZVN: byte-oriented opcode LZ77, the fast codec inside LZFSE and HFS+/APFS compression";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => LzfseStream.EncodeLzvn(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => FileFormat.Lzfse.Lzvn.DecompressToEnd(data);
}
