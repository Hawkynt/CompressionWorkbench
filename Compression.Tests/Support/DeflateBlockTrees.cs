#pragma warning disable CS1591
namespace Compression.Tests.Support;

/// <summary>
/// Reads the Huffman tables out of a raw deflate stream, block by block.
/// </summary>
/// <remarks>
/// This walks the stream the way a decoder does — a block's tables cannot be
/// found without decoding the block before it — but throws the output away and
/// keeps the tables. It exists so a test can check what we actually emit rather
/// than what a particular decoder is willing to accept.
/// </remarks>
public static class DeflateBlockTrees {

  public sealed record Block(
    bool IsDynamic,
    IReadOnlyList<int> CodeLengthLengths,
    IReadOnlyList<int> LiteralLengths,
    IReadOnlyList<int> DistanceLengths);

  private static readonly int[] CodeLengthOrder =
    [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

  private static readonly int[] LengthBase = [
    3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
    35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

  private static readonly int[] LengthExtra = [
    0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
    3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

  private static readonly int[] DistanceExtra = [
    0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
    7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

  /// <summary>Reads every block of <paramref name="stream"/>.</summary>
  public static List<Block> Read(byte[] stream) {
    ArgumentNullException.ThrowIfNull(stream);

    var reader = new BitReader(stream);
    var blocks = new List<Block>();

    while (true) {
      var final = reader.Bits(1) == 1;
      var type = reader.Bits(2);

      switch (type) {
        case 0: {
          reader.AlignToByte();
          var length = reader.Bits(16);
          reader.Bits(16);                                 // one's complement
          for (var i = 0; i < length; ++i) reader.Bits(8);
          blocks.Add(new(false, [], [], []));
          break;
        }
        case 1:
          blocks.Add(new(false, [], [], []));
          SkipSymbols(reader, StaticLiteralLengths(), StaticDistanceLengths());
          break;
        case 2: {
          var hlit = (int)reader.Bits(5) + 257;
          var hdist = (int)reader.Bits(5) + 1;
          var hclen = (int)reader.Bits(4) + 4;

          var clLengths = new int[19];
          for (var i = 0; i < hclen; ++i)
            clLengths[CodeLengthOrder[i]] = (int)reader.Bits(3);

          var clDecoder = new Canonical(clLengths);
          var combined = new int[hlit + hdist];
          var at = 0;
          while (at < combined.Length) {
            var symbol = clDecoder.Decode(reader);
            switch (symbol) {
              case < 16:
                combined[at++] = symbol;
                break;
              case 16: {
                var repeat = (int)reader.Bits(2) + 3;
                var previous = combined[at - 1];
                while (repeat-- > 0) combined[at++] = previous;
                break;
              }
              case 17: {
                var repeat = (int)reader.Bits(3) + 3;
                while (repeat-- > 0) combined[at++] = 0;
                break;
              }
              default: {
                var repeat = (int)reader.Bits(7) + 11;
                while (repeat-- > 0) combined[at++] = 0;
                break;
              }
            }
          }

          var literal = combined[..hlit];
          var distance = combined[hlit..];
          blocks.Add(new(true, clLengths, literal, distance));
          SkipSymbols(reader, literal, distance);
          break;
        }
        default:
          throw new InvalidDataException("deflate block type 3 is reserved");
      }

      if (final) break;
    }

    return blocks;
  }

  private static void SkipSymbols(BitReader reader, int[] literal, int[] distance) {
    var literals = new Canonical(literal);
    var distances = new Canonical(distance);

    while (true) {
      var symbol = literals.Decode(reader);
      if (symbol == 256) return;
      if (symbol < 256) continue;

      var lengthCode = symbol - 257;
      reader.Bits(LengthExtra[lengthCode]);
      _ = LengthBase[lengthCode];

      var distanceCode = distances.Decode(reader);
      reader.Bits(DistanceExtra[distanceCode]);
    }
  }

  private static int[] StaticLiteralLengths() {
    var lengths = new int[288];
    for (var i = 0; i < 144; ++i) lengths[i] = 8;
    for (var i = 144; i < 256; ++i) lengths[i] = 9;
    for (var i = 256; i < 280; ++i) lengths[i] = 7;
    for (var i = 280; i < 288; ++i) lengths[i] = 8;
    return lengths;
  }

  private static int[] StaticDistanceLengths() {
    var lengths = new int[30];
    Array.Fill(lengths, 5);
    return lengths;
  }

  /// <summary>Canonical Huffman decoding, most significant bit of the code first.</summary>
  private sealed class Canonical {
    private readonly Dictionary<(int Length, int Code), int> _symbols = [];
    private readonly int _maxLength;

    public Canonical(IReadOnlyList<int> lengths) {
      this._maxLength = lengths.Count == 0 ? 0 : lengths.Max();
      if (this._maxLength == 0) return;

      var counts = new int[this._maxLength + 1];
      foreach (var length in lengths)
        if (length > 0) ++counts[length];

      var next = new int[this._maxLength + 2];
      var code = 0;
      for (var length = 1; length <= this._maxLength; ++length) {
        code = (code + counts[length - 1]) << 1;
        next[length] = code;
      }

      for (var symbol = 0; symbol < lengths.Count; ++symbol) {
        var length = lengths[symbol];
        if (length > 0) this._symbols[(length, next[length]++)] = symbol;
      }
    }

    public int Decode(BitReader reader) {
      var code = 0;
      for (var length = 1; length <= this._maxLength; ++length) {
        code = (code << 1) | (int)reader.Bits(1);
        if (this._symbols.TryGetValue((length, code), out var symbol)) return symbol;
      }
      throw new InvalidDataException("no Huffman code matches the bits in the stream");
    }
  }

  /// <summary>Deflate's bit order: least significant bit of each byte first.</summary>
  private sealed class BitReader(byte[] data) {
    private int _position;

    public uint Bits(int count) {
      var value = 0u;
      for (var i = 0; i < count; ++i) {
        var bit = (data[this._position >> 3] >> (this._position & 7)) & 1;
        value |= (uint)bit << i;
        ++this._position;
      }
      return value;
    }

    public void AlignToByte() => this._position = (this._position + 7) & ~7;
  }
}
