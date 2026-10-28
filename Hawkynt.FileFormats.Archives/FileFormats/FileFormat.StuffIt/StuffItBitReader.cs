namespace FileFormat.StuffIt;

/// <summary>
/// Bit reader over a compressed fork. StuffIt's coders read in two orders: most-significant bit
/// of each byte first (Huffman, LZAH, Arsenic) and least-significant bit first (method 13, the
/// LZW of method 2). Reading past the end yields zero bits up to a small slack, then throws, so a
/// truncated fork cannot loop forever or decode into garbage silently.
/// </summary>
internal sealed class StuffItBitReader(byte[] data, int offset = 0, int length = -1) {
  private const int SlackBytes = 8;

  private readonly byte[] _data = data;
  private readonly int _end = length < 0 ? data.Length : offset + length;
  private long _bitPos = (long)offset * 8;

  private int ByteAt(long index) {
    if (index < this._end) return this._data[index];
    if (index < this._end + SlackBytes) return 0;
    throw new InvalidDataException("StuffIt compressed fork ended early.");
  }

  /// <summary>Next bit, most-significant bit of each byte first.</summary>
  public int ReadBit() {
    var bit = (ByteAt(this._bitPos >> 3) >> (7 - (int)(this._bitPos & 7))) & 1;
    ++this._bitPos;
    return bit;
  }

  /// <summary>Next <paramref name="count"/> bits MSB-first, first bit read becomes the value's top bit.</summary>
  public int ReadBits(int count) {
    var v = 0;
    for (var i = 0; i < count; ++i) v = (v << 1) | this.ReadBit();
    return v;
  }

  /// <summary>Next bit, least-significant bit of each byte first.</summary>
  public int ReadBitLe() {
    var bit = (ByteAt(this._bitPos >> 3) >> (int)(this._bitPos & 7)) & 1;
    ++this._bitPos;
    return bit;
  }

  /// <summary>Next <paramref name="count"/> bits LSB-first, first bit read becomes the value's bit 0.</summary>
  public int ReadBitsLe(int count) {
    var v = 0;
    for (var i = 0; i < count; ++i) v |= this.ReadBitLe() << i;
    return v;
  }

  /// <summary>Skips <paramref name="count"/> bits.</summary>
  public void Skip(long count) => this._bitPos += count;

  /// <summary>Next whole byte (the reader must be byte-aligned).</summary>
  public int ReadByte() {
    var b = ByteAt(this._bitPos >> 3);
    this._bitPos += 8;
    return b;
  }

  /// <summary>Whether every input byte has been consumed.</summary>
  public bool AtEnd => (this._bitPos >> 3) >= this._end;
}

/// <summary>
/// A binary prefix-code tree. Codes are added most-significant bit first: the first bit read
/// from the stream picks the branch at the root, in whichever bit order the stream uses.
/// </summary>
internal sealed class StuffItPrefixCode {
  // Node layout: two child slots per node. A child value >= 0 is a node index, < 0 encodes leaf ~symbol.
  private readonly List<int> _children = [Open, Open];
  private const int Open = int.MinValue;

  /// <summary>Canonical code from code lengths: shorter codes first, ties by symbol, all-zero code first.</summary>
  public static StuffItPrefixCode FromLengths(ReadOnlySpan<int> lengths, int maxLength = 32) {
    var code = new StuffItPrefixCode();
    uint next = 0;
    var remaining = 0;
    foreach (var l in lengths) if (l > 0) ++remaining;
    for (var length = 1; length <= maxLength && remaining > 0; ++length) {
      for (var symbol = 0; symbol < lengths.Length; ++symbol) {
        if (lengths[symbol] != length) continue;
        code.Add(symbol, next, length);
        ++next;
        --remaining;
      }
      next <<= 1;
    }
    return code;
  }

  /// <summary>Adds a code given with its first-read bit as the most significant of <paramref name="length"/> bits.</summary>
  public void Add(int symbol, uint codeBits, int length) {
    var node = 0;
    for (var i = length - 1; i >= 0; --i) {
      var bit = (int)((codeBits >> i) & 1);
      var slot = node * 2 + bit;
      var child = this._children[slot];
      if (i == 0) {
        if (child != Open) throw new InvalidDataException("StuffIt prefix code is not prefix-free.");
        this._children[slot] = ~symbol;
        return;
      }
      if (child == Open) {
        child = this._children.Count / 2;
        this._children.Add(Open);
        this._children.Add(Open);
        this._children[slot] = child;
      } else if (child < 0) {
        throw new InvalidDataException("StuffIt prefix code is not prefix-free.");
      }
      node = child;
    }
  }

  /// <summary>Adds a code given least-significant bit first (the first-read bit is bit 0).</summary>
  public void AddLowBitFirst(int symbol, uint codeBits, int length) {
    uint reversed = 0;
    for (var i = 0; i < length; ++i) reversed = (reversed << 1) | ((codeBits >> i) & 1);
    this.Add(symbol, reversed, length);
  }

  /// <summary>Starts a tree built by explicit branching (method 3's serialised tree).</summary>
  internal int NewNode() {
    var index = this._children.Count / 2;
    this._children.Add(Open);
    this._children.Add(Open);
    return index;
  }

  internal void SetChild(int node, int bit, int child) => this._children[node * 2 + bit] = child;

  internal static int Leaf(int symbol) => ~symbol;

  /// <summary>Decodes one symbol reading bits MSB-first.</summary>
  public int Decode(StuffItBitReader reader) => this.Walk(reader, lsbFirst: false);

  /// <summary>Decodes one symbol reading bits LSB-first.</summary>
  public int DecodeLe(StuffItBitReader reader) => this.Walk(reader, lsbFirst: true);

  private int Walk(StuffItBitReader reader, bool lsbFirst) {
    var node = 0;
    for (var depth = 0; depth < 64; ++depth) {
      var bit = lsbFirst ? reader.ReadBitLe() : reader.ReadBit();
      var child = this._children[node * 2 + bit];
      if (child == Open) throw new InvalidDataException("Invalid StuffIt prefix code in bitstream.");
      if (child < 0) return ~child;
      node = child;
    }
    throw new InvalidDataException("Invalid StuffIt prefix code in bitstream.");
  }
}
