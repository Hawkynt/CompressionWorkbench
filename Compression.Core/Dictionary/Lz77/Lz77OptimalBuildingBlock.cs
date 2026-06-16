using System.Buffers.Binary;
using Compression.Core.Dictionary.MatchFinders;
using Compression.Core.Dictionary.Parsing;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lz77;

/// <summary>
/// LZ77 driven by the reusable <see cref="Lz77OptimalParser"/> primitive instead of a greedy
/// match walk. It uses the same compact token serialization (and decoder) as
/// <see cref="Lz77BuildingBlock"/>, so the two differ only in how the parse is chosen — making
/// the optimal parser's benefit directly observable in benchmarks.
/// </summary>
public sealed class Lz77OptimalBuildingBlock : IBuildingBlock {
  private const int WindowSize = 32768;
  private const int MaxMatch = 258;
  private const int MinMatch = 3;

  /// <inheritdoc/>
  public string Id => "BB_Lz77Optimal";
  /// <inheritdoc/>
  public string DisplayName => "LZ77-Optimal";
  /// <inheritdoc/>
  public string Description => "LZ77 with cost-based optimal (shortest-path) parsing";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var tokens = Parse(data);
    return SerializeTokens(tokens);
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var tokens = DeserializeTokens(data);
    return Lz77Decompressor.Decompress(tokens);
  }

  /// <summary>
  /// Produces the optimal LZ77 parse of <paramref name="data"/> using a hash-chain match finder
  /// and the default cost model. Exposed so tests can compare it against a greedy parse.
  /// </summary>
  /// <param name="data">The input bytes.</param>
  /// <returns>The optimal token sequence.</returns>
  public static List<Lz77Token> Parse(ReadOnlySpan<byte> data) {
    var finder = new HashChainMatchFinder(WindowSize, maxChainDepth: 128);

    // Price tokens in the exact units this coder serializes them in: a literal is 2 bytes
    // (16 bits), a match is 5 bytes (40 bits) regardless of length/distance. With a flat cost
    // model that mirrors the real byte cost, the optimal parser directly minimizes the
    // serialized output size.
    var costModel = new FixedLzCostModel(literalBits: 16.0, matchBits: 40.0);
    var parser = new Lz77OptimalParser(costModel, MinMatch, MaxMatch, niceLength: 128);

    var parsed = parser.Parse(
      data,
      (ReadOnlySpan<byte> buf, int pos) => finder.FindMatch(buf, pos, WindowSize, MaxMatch, MinMatch));

    var tokens = new List<Lz77Token>(parsed.Count);
    foreach (var t in parsed)
      tokens.Add(t.IsLiteral
        ? Lz77Token.CreateLiteral(t.Literal)
        : Lz77Token.CreateMatch(t.Distance, t.Length));

    return tokens;
  }

  private static byte[] SerializeTokens(List<Lz77Token> tokens) {
    using var ms = new MemoryStream();
    Span<byte> buf = stackalloc byte[4];
    foreach (var token in tokens)
      if (token.IsLiteral) {
        ms.WriteByte(0);
        ms.WriteByte(token.Literal);
      } else {
        ms.WriteByte(1);
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)token.Distance);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[2..], (ushort)token.Length);
        ms.Write(buf[..4]);
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
