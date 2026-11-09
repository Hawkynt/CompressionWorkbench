using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// Exposes the SQX 2.0 LZ77 + Huffman stream (archive method 2) as a benchmarkable building
/// block: the stream exactly as it sits in an SQX archive, behind a 4-byte little-endian original
/// size, which the archive keeps in the file header instead.
/// </summary>
public sealed class SqxBuildingBlock : IBuildingBlock {
  private const int DictionarySize = 256 * 1024;
  private const int Effort = 2;

  /// <inheritdoc/>
  public string Id => "BB_Sqx";
  /// <inheritdoc/>
  public string DisplayName => "SQX";
  /// <inheritdoc/>
  public string Description => "SQX 2.0 LZ77 + Huffman stream (archive method 2, 256 KiB dictionary)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var stream = SqxLzEncoder.Encode(data, SqxTables.Format20, DictionarySize, Effort);
    var result = new byte[4 + stream.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    stream.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4)
      throw new InvalidDataException("SQX block too short.");
    var size = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (size < 0)
      throw new InvalidDataException("SQX block size is negative.");
    return size == 0 ? [] : new SqxLzDecoder(DictionarySize).Decode(data[4..], size, solid: false, extendedHeader: false, SqxTables.Format20);
  }
}
