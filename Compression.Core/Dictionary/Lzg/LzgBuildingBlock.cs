using Compression.Registry;
using FileFormat.Lzg;

namespace Compression.Core.Dictionary.Lzg;

/// <summary>
/// Exposes Marcus Geelnard's LZG as a benchmarkable building block.
/// </summary>
/// <remarks>
/// The block is the <see cref="LzgStream"/> codec, not a second one: it writes the 16-byte
/// header and LZG1 data that liblzg's <c>LZG_Decode</c> reads, and reads what
/// <c>LZG_Encode</c> writes.
/// </remarks>
public sealed class LzgBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzg";
  /// <inheritdoc/>
  public string DisplayName => "LZG";
  /// <inheritdoc/>
  public string Description => "Geelnard's liblzg LZG1: marker-byte LZ77 behind liblzg's 16-byte checksummed header, tuned for a small dependency-free decoder";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    LzgStream.Compress(new MemoryStream(data.ToArray(), writable: false), output);
    return output.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    LzgStream.Decompress(new MemoryStream(data.ToArray(), writable: false), output);
    return output.ToArray();
  }
}
