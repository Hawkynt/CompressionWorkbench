using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// Exposes the LZMS algorithm as a benchmarkable building block.
/// </summary>
/// <remarks>
/// LZMS is a chunked codec: a resource is cut into pieces of at most 128 KB and
/// each is coded on its own, because the offset alphabet is sized by the piece it
/// codes. This wrapper does the same rather than handing the codec one enormous
/// chunk, and frames each piece with its two sizes so the whole can be put back
/// together.
/// </remarks>
public sealed class LzmsBuildingBlock : IBuildingBlock {
  private const int ChunkSize = 128 * 1024;

  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Lzms";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "LZMS";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "LZ+Markov+Shannon compression with delta matching, used in Windows WIM";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    Span<byte> header = stackalloc byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    BinaryPrimitives.WriteInt32LittleEndian(header[4..], ChunkSize);
    output.Write(header);

    var compressor = new LzmsCompressor();
    var size = new byte[4];
    for (var at = 0; at < data.Length; at += ChunkSize) {
      var length = Math.Min(ChunkSize, data.Length - at);
      var chunk = compressor.Compress(data.Slice(at, length));

      // A chunk that did not get smaller is stored, exactly as a WIM does it.
      var stored = chunk.Length >= length;
      BinaryPrimitives.WriteInt32LittleEndian(size, stored ? -length : chunk.Length);
      output.Write(size);
      if (stored) output.Write(data.Slice(at, length));
      else output.Write(chunk);
    }

    return output.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var total = BinaryPrimitives.ReadInt32LittleEndian(data);
    var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
    var result = new byte[total];
    var decompressor = new LzmsDecompressor();
    var at = 8;
    var produced = 0;
    while (produced < total) {
      var length = Math.Min(chunkSize, total - produced);
      var stored = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
      at += 4;
      if (stored < 0) {
        data.Slice(at, length).CopyTo(result.AsSpan(produced));
        at += length;
      } else {
        decompressor.Decompress(data.Slice(at, stored), length).CopyTo(result.AsSpan(produced));
        at += stored;
      }

      produced += length;
    }

    return result;
  }
}
