using System.Buffers.Binary;
using Compression.Core.Dictionary.MatchFinders;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lz77;

/// <summary>
/// Exposes the LZ77 algorithm as a benchmarkable building block.
/// Serializes tokens to a compact binary format for round-trip benchmarking.
/// </summary>
public sealed class Lz77BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lz77";
  /// <inheritdoc/>
  public string DisplayName => "LZ77";
  /// <inheritdoc/>
  public string Description => "Sliding-window dictionary compression with distance/length tokens";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var matchFinder = new HashChainMatchFinder(32768, 64);
    var compressor = new Lz77Compressor(matchFinder, 32768, 258, 3);
    var tokens = compressor.Compress(data);
    return SerializeTokens(tokens);
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var tokens = DeserializeTokens(data);
    return Lz77Decompressor.Decompress(tokens);
  }

  /// <summary>
  /// Token format: flag byte (0=literal, 1=match), then:
  /// - Literal: 1 byte value
  /// - Match: 2-byte LE distance, 2-byte LE length
  /// </summary>
  private static byte[] SerializeTokens(List<Lz77Token> tokens) {
    using var ms = new MemoryStream();
    Span<byte> buf = stackalloc byte[4];
    foreach (var token in tokens) {
      if (token.IsLiteral) {
        ms.WriteByte(0);
        ms.WriteByte(token.Literal);
      } else {
        ms.WriteByte(1);
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)token.Distance);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[2..], (ushort)token.Length);
        ms.Write(buf[..4]);
      }
    }
    return ms.ToArray();
  }

  private static List<Lz77Token> DeserializeTokens(ReadOnlySpan<byte> data) {
    var tokens = new List<Lz77Token>();
    var pos = 0;
    while (pos < data.Length) {
      var flag = data[pos++];
      if (flag == 0) {
        tokens.Add(Lz77Token.CreateLiteral(data[pos++]));
      } else {
        var distance = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
        tokens.Add(Lz77Token.CreateMatch(distance, length));
        pos += 4;
      }
    }
    return tokens;
  }
}
