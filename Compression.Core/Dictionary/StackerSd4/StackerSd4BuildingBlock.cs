using System.Buffers.Binary;
using Compression.Core.FileSystems.Cvf;
using Compression.Registry;

namespace Compression.Core.Dictionary.StackerSd4;

/// <summary>
/// Stacker 4's SD-4 codec as a raw primitive: LZ77 over a 2720-byte window, Huffman-coded with a
/// run-length coded code table (<c>docs/CVF-CODECS-ON-DISK.md</c>). The building-block wrapper stores
/// the uncompressed size (32-bit little-endian) before one SD-4 stream covering the whole input.
/// </summary>
public sealed class StackerSd4BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_StackerSd4";
  /// <inheritdoc/>
  public string DisplayName => "Stacker SD-4";
  /// <inheritdoc/>
  public string Description => "Stac Stacker 4 cluster codec: LZ77 (2720-byte window) with canonical Huffman codes and a run-length coded table";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var stream = Sd4Codec.Encode(data, level: 3);
    var result = new byte[4 + stream.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    stream.CopyTo(result, 4);
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4) throw new InvalidDataException("Stacker SD-4: missing size header.");
    var size = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (size < 0) throw new InvalidDataException("Stacker SD-4: negative size.");
    var stream = data[4..].ToArray();
    return Sd4Codec.Decode(stream, stream.Length, size);
  }
}
