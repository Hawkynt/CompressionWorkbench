using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lz78;

/// <summary>
/// Exposes the LZ78 algorithm as a benchmarkable building block.
/// Serializes tokens to a compact binary format for round-trip benchmarking.
/// </summary>
public sealed class Lz78BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lz78";
  /// <inheritdoc/>
  public string DisplayName => "LZ78";
  /// <inheritdoc/>
  public string Description => "Dictionary compression building phrases from input, predecessor to LZW";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressor = new Lz78Compressor(12);
    var tokens = compressor.Compress(data);
    return SerializeTokens(tokens);
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var tokens = DeserializeTokens(data);
    return Lz78Decompressor.Decompress(tokens, 12);
  }

  /// <summary>
  /// Token format: 2-byte LE dictionary index, 1-byte next (0xFF = terminal with flag byte 1).
  /// We use a flag byte: 0 = normal token (index + byte), 1 = terminal token (index only).
  /// </summary>
  private static byte[] SerializeTokens(List<Lz78Token> tokens) {
    using var ms = new MemoryStream();
    Span<byte> buf = stackalloc byte[2];
    foreach (var token in tokens) {
      BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)token.DictionaryIndex);
      ms.Write(buf);
      if (token.NextByte.HasValue) {
        ms.WriteByte(0);
        ms.WriteByte(token.NextByte.Value);
      } else {
        ms.WriteByte(1);
      }
    }
    return ms.ToArray();
  }

  private static List<Lz78Token> DeserializeTokens(ReadOnlySpan<byte> data) {
    var tokens = new List<Lz78Token>();
    var pos = 0;
    while (pos < data.Length) {
      var index = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
      pos += 2;
      var flag = data[pos++];
      if (flag == 0) {
        tokens.Add(new Lz78Token(index, data[pos++]));
      } else {
        tokens.Add(new Lz78Token(index, null));
      }
    }
    return tokens;
  }
}
