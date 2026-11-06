namespace FileFormat.GenteeInstaller;

/// <summary>
/// LZGE, the LZ77 + Huffman coder of the Gentee language's GEA archives and launchers: literals,
/// match lengths and offset slots share one Huffman alphabet that is re-sent every 10000
/// symbols; code lengths travel through a 20-symbol pre-tree as deltas against the previous
/// block's lengths; the three most recent offsets are kept for reuse.
/// </summary>
/// <remarks>
/// Converted from the Gentee group's <c>LZGE/delzge.c</c>, <c>LZGE/lzge.c</c>,
/// <c>Huffman/dehuffman.c</c> and <c>Huffman/huffman.c</c> (Alexey Krivonogov, the Gentee Open
/// Source License, MIT), as Observer carries them; see THIRD-PARTY-NOTICE.Gentee.txt.
/// </remarks>
public static class Lzge {
  private const int HuffmanBlock = 10000;
  private const int Alphabet = 256;
  private const int MatchLength = 2;
  private const int MinOffset = 2;
  private const int SlotLength = 18;
  private const int MaxCode = 15;
  private const int PreTreeAlphabet = 20;
  private const int PreZero2 = 16, PreZero4 = 17, PreZero6 = 18, PreBig = 19;

  // Offset slot widths and length slot widths (slot 18 is set to the offset width per stream).
  private static readonly int[] OffsetBits = [
    0, 0, 0, 1, 1, 2, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
    14, 14, 15, 15, 16, 16, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17,
  ];

  private static readonly int[] LengthBitsTemplate = [0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 21];

  /// <summary>
  /// Decodes an LZGE stream into <paramref name="output"/> from <paramref name="solidOffset"/> up to
  /// <paramref name="end"/>; the bytes before <paramref name="solidOffset"/> are earlier output that
  /// matches may reach back into.
  /// </summary>
  /// <returns>The number of input bytes the stream took.</returns>
  /// <exception cref="InvalidDataException">The stream is damaged or runs out.</exception>
  public static int Decode(ReadOnlySpan<byte> input, Span<byte> output, int solidOffset, int end) {
    var maxOffset = (1 << Math.Min(21, BitsFor((uint)end))) - 1;

    // Offset slot ranges up to the largest offset this stream can use.
    var offsetMin = new int[OffsetBits.Length + 1];
    var slots = 3;
    for (var j = MinOffset; slots < OffsetBits.Length; ) {
      offsetMin[slots] = j;
      j += 1 << OffsetBits[slots];
      ++slots;
      if (j - 1 >= maxOffset)
        break;
    }
    var maxBits = BitsFor((uint)maxOffset);
    var lengthBits = (int[])LengthBitsTemplate.Clone();
    lengthBits[SlotLength] = maxBits;
    var lengthMin = new int[lengthBits.Length];
    for (int i = 0, j = MatchLength; i < lengthBits.Length; ++i) {
      lengthMin[i] = j;
      j += 1 << lengthBits[i];
    }

    int recent0 = 3, recent1 = 4, recent2 = 5;
    var bits = new BitReader(input);
    var huffman = new Huffman(Alphabet + SlotLength * slots + 1);
    var o = solidOffset;
    if (end > output.Length)
      throw new InvalidDataException("LZGE output exceeds its buffer");
    while (o < end) {
      huffman.ReadTree(ref bits);
      for (var n = 0; n < HuffmanBlock && o < end; ++n) {
        var value = huffman.ReadSymbol(ref bits);
        if (value < Alphabet) {
          output[o++] = (byte)value;
          continue;
        }
        value -= Alphabet;
        int lengthSlot, slot;
        if (value == SlotLength * slots) {
          slot = 0;
          lengthSlot = SlotLength;
        } else {
          lengthSlot = value % SlotLength;
          slot = value / SlotLength;
        }
        var length = (int)bits.Read(lengthBits[lengthSlot]) + lengthMin[lengthSlot];
        int offset;
        if (lengthSlot == SlotLength)
          offset = (int)bits.Read(maxBits);
        else {
          offset = (int)bits.Read(OffsetBits[slot]) + offsetMin[slot];
          switch (slot) {
            case 0:
              offset = recent0;
              break;
            case 1:
              offset = recent1;
              recent1 = recent0;
              recent0 = offset;
              break;
            default:
              if (slot == 2)
                offset = recent2;
              recent2 = recent1;
              recent1 = recent0;
              recent0 = offset;
              break;
          }
        }
        if (offset <= 0 || offset > o || o + length > end)
          throw new InvalidDataException("LZGE match out of range");
        for (var k = 0; k < length; ++k, ++o)
          output[o] = output[o - offset];
      }
    }
    return bits.Position + 1;
  }

  private static int BitsFor(uint value) {
    for (var i = 1; i < 32; ++i)
      if (value < 1u << i)
        return i;
    return 32;
  }

  /// <summary>Most significant bit first, starting at bit 7 of the first byte.</summary>
  internal ref struct BitReader(ReadOnlySpan<byte> data) {
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bit = 7;

    public int Position { get; private set; }

    public uint ReadBit() {
      if (this.Position >= this._data.Length)
        throw new InvalidDataException("LZGE stream ends early");
      var value = (uint)(this._data[this.Position] >> this._bit) & 1;
      if (this._bit == 0) {
        this._bit = 7;
        ++this.Position;
      } else
        --this._bit;
      return value;
    }

    /// <summary>Reads <paramref name="count"/> bits, the first one as the least significant.</summary>
    public uint Read(int count) {
      uint result = 0;
      for (var i = 0; i < count; ++i)
        result |= this.ReadBit() << i;
      return result;
    }
  }

  /// <summary>A Huffman code rebuilt from code lengths, with the per-symbol length memory the deltas need.</summary>
  private sealed class Huffman {
    private readonly int _symbols;
    private readonly bool _fixed;
    private readonly int[] _left, _right, _length;
    private readonly byte[] _previous;
    private int _root;

    public Huffman(int symbols, bool isFixed = false) {
      this._symbols = symbols;
      this._fixed = isFixed;
      var nodes = (symbols + (symbols & 1)) << 1;
      this._left = new int[nodes];
      this._right = new int[nodes];
      this._length = new int[nodes];
      this._previous = new byte[Math.Max(2048, symbols)];
      Array.Fill(this._left, -1);
      Array.Fill(this._right, -1);
    }

    public int ReadSymbol(ref BitReader bits) {
      var node = this._root;
      do {
        node = bits.ReadBit() != 0 ? this._right[node] : this._left[node];
        if (node < 0)
          throw new InvalidDataException("LZGE code outside its Huffman tree");
      } while (this._left[node] >= 0);
      return node;
    }

    public void ReadTree(ref BitReader bits) {
      var width = (int)bits.Read(this._symbols < 32 ? 2 : 3) + 1;
      var min = (int)bits.Read(width);
      bits.Read(width); // max - min
      var lengths = new int[this._symbols];
      if (this._fixed)
        for (var i = 0; i < this._symbols; ++i)
          lengths[i] = (int)bits.Read(width);
      else {
        var pre = new Huffman(PreTreeAlphabet, isFixed: true);
        pre.ReadTree(ref bits);
        for (var i = 0; i < this._symbols; ++i) {
          var code = pre.ReadSymbol(ref bits);
          if (code <= MaxCode) {
            lengths[i] = code;
            continue;
          }
          var run = code switch {
            PreZero2 => (int)bits.Read(2) + 4,
            PreZero4 => (int)bits.Read(4) + 8,
            PreZero6 => (int)bits.Read(6) + 24,
            _ => (int)bits.Read(width),
          };
          if (code == PreBig) {
            lengths[i] = run;
            continue;
          }
          if (i + run > this._symbols)
            throw new InvalidDataException("LZGE code length run past the alphabet");
          i += run - 1; // the run's entries stay 0
        }
      }
      for (var i = 0; i < this._symbols; ++i) {
        var previous = this._previous[i];
        this._previous[i] = 0;
        if (lengths[i] <= MaxCode) {
          lengths[i] = (lengths[i] + previous) % (MaxCode + 1);
          this._previous[i] = (byte)lengths[i];
        }
        if (lengths[i] != 0)
          lengths[i] += min - 1;
      }
      this.Build(lengths);
    }

    /// <summary>
    /// Pairs equal-length nodes in symbol order, each pair becoming a node one shorter, until the
    /// root (length 0) appears.
    /// </summary>
    private void Build(int[] lengths) {
      var length = this._length;
      Array.Clear(length);
      lengths.CopyTo(length, 0);
      var pending = new int[2048];
      Array.Fill(pending, -1);
      var codes = this._symbols;
      var node = -1;
      for (;;) {
        var created = false;
        var count = 0;
        for (var cur = 0; cur < codes; ++cur) {
          if (length[cur] == 0)
            continue;
          if (length[cur] >= pending.Length)
            throw new InvalidDataException("LZGE code length out of range");
          ++count;
          var mate = pending[length[cur]];
          if (mate >= 0) {
            if (codes >= length.Length)
              throw new InvalidDataException("LZGE Huffman tree overflows");
            node = codes++;
            length[node] = length[cur] - 1;
            this._left[node] = mate;
            this._right[node] = cur;
            pending[length[cur]] = -1;
            created = true;
            if (length[node] == 0)
              break;
          } else
            pending[length[cur]] = cur;
          length[cur] = 0;
        }
        if (count == 1) {
          if (pending[1] < 0 || codes >= length.Length)
            throw new InvalidDataException("LZGE Huffman tree is not complete");
          node = codes;
          length[node] = 0;
          this._left[node] = pending[1];
          this._right[node] = -1;
          created = true;
        }
        if (node >= 0 && length[node] == 0)
          break;
        if (!created)
          throw new InvalidDataException("LZGE Huffman tree is not complete");
      }
      this._root = node;
    }
  }
}
