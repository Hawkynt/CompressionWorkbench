namespace Compression.Core.Entropy.PackMp3;

/// <summary>
/// Reads MP3 main data most significant bit first, counts the bits taken since the last reset,
/// can rewind, and decodes the ISO 11172-3 Huffman codes (the role of packMP3's
/// <c>huffman_reader</c> over <c>abitreader</c>). Bits past the end read as zero.
/// </summary>
internal sealed class PmpBitReader(byte[] data, int length) {
  private long _bit;

  public int Count { get; private set; }

  public void ResetCounter() => this.Count = 0;

  public int ReadBit() {
    var at = this._bit >> 3;
    var bit = at < length ? (data[at] >> (7 - (int)(this._bit & 7))) & 1 : 0;
    ++this._bit;
    ++this.Count;
    return bit;
  }

  public int ReadBits(int n) {
    var v = 0;
    for (var i = 0; i < n; ++i) v = (v << 1) | this.ReadBit();
    return v;
  }

  public void Rewind(int n) {
    this._bit = Math.Max(0, this._bit - n);
    this.Count = this.Count > n ? this.Count - n : 0;
  }

  /// <summary>Decodes one big-value pair with the given table.</summary>
  public (int V0, int V1) DecodePair(PmpHuffman.Tree tree) {
    var node = 0;
    while (true) {
      node = tree.Next[node * 2 + this.ReadBit()];
      if (node < 0) {
        var leaf = ~node;
        return (leaf >> 8, leaf & 0xFF);
      }
      if (node == 0) throw new InvalidDataException("packMP3: invalid Huffman code.");
    }
  }

  /// <summary>Decodes one small-value quadruple, giving the four magnitudes (0 or 1).</summary>
  public int DecodeQuadruple(PmpHuffman.Tree tree) {
    var (v0, _) = this.DecodePair(tree);
    return v0;
  }
}

/// <summary>Writes MP3 main data most significant bit first (packMP3's <c>huffman_writer</c>).</summary>
internal sealed class PmpBitWriter {
  private readonly List<byte> _bytes = [];
  private int _current, _filled;

  public int Count { get; private set; }

  public void ResetCounter() => this.Count = 0;

  /// <summary>Whole bytes written so far.</summary>
  public int BytePosition => this._bytes.Count;

  public void WriteBit(int bit) {
    this._current = (this._current << 1) | (bit & 1);
    if (++this._filled == 8) {
      this._bytes.Add((byte)this._current);
      this._current = this._filled = 0;
    }
    ++this.Count;
  }

  public void WriteBits(int value, int n) {
    for (var i = n - 1; i >= 0; --i) this.WriteBit((value >> i) & 1);
  }

  /// <summary>The data, the last byte filled up with one bits.</summary>
  public byte[] Finish() {
    while (this._filled != 0) this.WriteBit(1);
    return [.. this._bytes];
  }
}

/// <summary>The ISO 11172-3 Huffman codes, as packMP3's tables carry them, with decoding trees built from them.</summary>
internal static class PmpHuffman {
  /// <summary>A binary decoding tree: child index per node and bit, leaves stored as ~(v0 &lt;&lt; 8 | v1).</summary>
  internal sealed class Tree(int[] next) {
    public readonly int[] Next = next;
  }

  /// <summary>Code lengths and codes of one table, row-major over (v0, v1).</summary>
  internal sealed record Code(byte[] Lengths, ushort[] Codes, int Width);

  private static Code Make(ReadOnlySpan<byte> lengths, ReadOnlySpan<ushort> codes) =>
    new(lengths.ToArray(), codes.ToArray(), (int)Math.Round(Math.Sqrt(lengths.Length)));

  public static readonly Code QuadA = new(PackMp3Tables.QuadALength.ToArray(), PackMp3Tables.QuadACode.ToArray(), 1);
  public static readonly Code QuadB = new(PackMp3Tables.QuadBLength.ToArray(), PackMp3Tables.QuadBCode.ToArray(), 1);

  private static readonly Dictionary<int, Code> Pairs = new() {
    [1] = Make(PackMp3Tables.Pair1Length, PackMp3Tables.Pair1Code),
    [2] = Make(PackMp3Tables.Pair2Length, PackMp3Tables.Pair2Code),
    [3] = Make(PackMp3Tables.Pair3Length, PackMp3Tables.Pair3Code),
    [5] = Make(PackMp3Tables.Pair5Length, PackMp3Tables.Pair5Code),
    [6] = Make(PackMp3Tables.Pair6Length, PackMp3Tables.Pair6Code),
    [7] = Make(PackMp3Tables.Pair7Length, PackMp3Tables.Pair7Code),
    [8] = Make(PackMp3Tables.Pair8Length, PackMp3Tables.Pair8Code),
    [9] = Make(PackMp3Tables.Pair9Length, PackMp3Tables.Pair9Code),
    [10] = Make(PackMp3Tables.Pair10Length, PackMp3Tables.Pair10Code),
    [11] = Make(PackMp3Tables.Pair11Length, PackMp3Tables.Pair11Code),
    [12] = Make(PackMp3Tables.Pair12Length, PackMp3Tables.Pair12Code),
    [13] = Make(PackMp3Tables.Pair13Length, PackMp3Tables.Pair13Code),
    [15] = Make(PackMp3Tables.Pair15Length, PackMp3Tables.Pair15Code),
    [16] = Make(PackMp3Tables.Pair16Length, PackMp3Tables.Pair16Code),
    [24] = Make(PackMp3Tables.Pair24Length, PackMp3Tables.Pair24Code),
  };

  private static readonly Dictionary<Code, Tree> Trees = new(ReferenceEqualityComparer.Instance);

  /// <summary>The pair code of big-value table <paramref name="table"/>, or null where the standard defines none.</summary>
  public static Code? BigValues(int table) {
    var pair = PackMp3Tables.BigValueTable[table];
    return pair <= 0 ? null : Pairs[pair];
  }

  public static int Linbits(int table) => PackMp3Tables.BigValueLinbits[table];

  public static Tree TreeOf(Code code) {
    lock (Trees) {
      if (Trees.TryGetValue(code, out var tree)) return tree;
      var next = new List<int> { 0, 0 };
      for (var symbol = 0; symbol < code.Lengths.Length; ++symbol) {
        int length = code.Lengths[symbol];
        if (length == 0) continue;
        var node = 0;
        for (var b = length - 1; b >= 0; --b) {
          var slot = node * 2 + ((code.Codes[symbol] >> b) & 1);
          if (b == 0) {
            next[slot] = ~((symbol / code.Width) << 8 | (symbol % code.Width));
            break;
          }
          if (next[slot] <= 0) {
            next[slot] = next.Count / 2;
            next.Add(0);
            next.Add(0);
          }
          node = next[slot];
        }
      }
      tree = new([.. next]);
      Trees[code] = tree;
      return tree;
    }
  }
}
