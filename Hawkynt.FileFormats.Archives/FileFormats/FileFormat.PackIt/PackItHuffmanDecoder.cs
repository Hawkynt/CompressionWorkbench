namespace FileFormat.PackIt;

/// <summary>
/// Decodes the Huffman stream of a "PMa4" PackIt entry.
/// </summary>
/// <remarks>
/// <para>The stream is MSB-first. It opens with the code tree in pre-order: a 1 bit is a leaf followed
/// by its 8-bit byte value; a 0 bit is a branch, followed by its 0-subtree and then its 1-subtree.
/// Every byte after that is one code walked from the root (0 = first subtree). The tree is sent once per
/// entry and the entry ends on a byte boundary.</para>
/// <para>Sourcing: the scheme is StuffIt's method 3, which PackIt II introduced; behaviour taken from
/// The Unarchiver's XADMaster (<c>XADStuffItHuffmanHandle.m</c>, <c>XADPackItParser.m</c>,
/// LGPL-2.1-or-later) and checked against real PackIt II archives, whose header and fork CRC-16s
/// the decoded bytes must match.</para>
/// </remarks>
internal sealed class PackItHuffmanDecoder {
  // Node i: Left/Right children; a negative child is ~byteValue (a leaf).
  private readonly List<(int Zero, int One)> _nodes = [];
  private readonly int _rootLeaf = int.MinValue;
  private readonly Stream _stream;
  private int _current;
  private int _bitsLeft;

  /// <summary>Gets the number of bytes consumed from the stream so far.</summary>
  public long BytesConsumed { get; private set; }

  /// <summary>Reads the code tree from <paramref name="stream"/>, positioned just after the entry magic.</summary>
  /// <exception cref="InvalidDataException">The tree is deeper than 256 levels or the stream ends inside it.</exception>
  public PackItHuffmanDecoder(Stream stream) {
    this._stream = stream;
    var root = this.ParseTree(0);
    if (root < 0)
      this._rootLeaf = ~root; // degenerate one-symbol tree: every code is empty
  }

  /// <summary>Decodes <paramref name="output"/>.Length bytes.</summary>
  public void Decode(Span<byte> output) {
    if (this._rootLeaf != int.MinValue) {
      output.Fill((byte)this._rootLeaf);
      return;
    }
    for (var i = 0; i < output.Length; ++i) {
      var node = 0;
      while (true) {
        var (zero, one) = this._nodes[node];
        var next = this.ReadBit() == 0 ? zero : one;
        if (next < 0) {
          output[i] = (byte)~next;
          break;
        }
        node = next;
      }
    }
  }

  private int ParseTree(int depth) {
    if (depth > 256)
      throw new InvalidDataException("PackIt Huffman tree is deeper than any 256-symbol code.");
    if (this.ReadBit() == 1) {
      var value = 0;
      for (var i = 0; i < 8; ++i)
        value = (value << 1) | this.ReadBit();
      return ~value;
    }
    var index = this._nodes.Count;
    this._nodes.Add((0, 0));
    var zero = this.ParseTree(depth + 1);
    var one = this.ParseTree(depth + 1);
    this._nodes[index] = (zero, one);
    return index;
  }

  private int ReadBit() {
    if (this._bitsLeft == 0) {
      var b = this._stream.ReadByte();
      if (b < 0)
        throw new InvalidDataException("PackIt Huffman stream ends before the entry is complete.");
      ++this.BytesConsumed;
      this._current = b;
      this._bitsLeft = 8;
    }
    --this._bitsLeft;
    return (this._current >> this._bitsLeft) & 1;
  }
}
