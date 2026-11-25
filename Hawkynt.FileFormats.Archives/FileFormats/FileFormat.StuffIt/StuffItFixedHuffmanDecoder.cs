using System.Buffers.Binary;

namespace FileFormat.StuffIt;

/// <summary>
/// StuffIt method 6, "Fixed Huffman": the "Faster" setting of StuffIt 1.x. A fork is a run of blocks;
/// each block is either PackBits bytes behind a fixed 257-symbol prefix code, or PackBits bytes alone.
/// </summary>
/// <remarks>
/// <para>Layout (big-endian), as documented in <c>docs/STUFFIT-METHOD6-ON-DISK.md</c>:</para>
/// <list type="bullet">
///   <item><c>int32 size</c>, counting the field itself. A negative size marks a PackBits-only block of
///   <c>-size</c> bytes.</item>
///   <item>A coded block continues with <c>uint32 count</c> (PackBits bytes it carries),
///   <c>int16 entries</c> and that many translation bytes, then the prefix code, most-significant bit
///   first, up to the end of the block. Code <c>i</c> stands for translation byte <c>i</c>; code 256
///   ends the block early. Translation bytes a block does not redefine keep their earlier values.</item>
/// </list>
/// <para>Each block's PackBits run is expanded on its own; the fork ends at its declared length.</para>
/// </remarks>
internal static class StuffItFixedHuffmanDecoder {
  private const int Symbols = 257;
  private const int EndSymbol = 256;

  /// <summary>Code lengths of symbols 0..256 in order, as (number of symbols, length) runs.</summary>
  private static ReadOnlySpan<byte> LengthRuns => [
    1, 3, 1, 4, 4, 5, 12, 6, 32, 7, 16, 8, 49, 9, 2, 10, 2, 9, 40, 10, 95, 11, 2, 13, 1, 12,
  ];

  /// <summary>Child pointers of the decoding tree: index 2n is the 0-branch of node n, 2n+1 the 1-branch.
  /// Values &gt;= 0 are inner nodes, ~symbol marks a leaf.</summary>
  private static readonly int[] Tree = BuildTree();

  /// <summary>Decodes <paramref name="outputLength"/> bytes from a method-6 fork.</summary>
  /// <exception cref="InvalidDataException">A block header is out of range or the fork ends early.</exception>
  public static byte[] Decode(byte[] input, int outputLength) {
    var output = new byte[outputLength];
    var written = 0;
    var translation = new byte[256];
    var pos = 0;
    while (written < outputLength) {
      if (pos + 4 > input.Length) throw new InvalidDataException("StuffIt fixed-Huffman fork ended before its declared length.");
      var size = BinaryPrimitives.ReadInt32BigEndian(input.AsSpan(pos));
      if (size < 0) {
        var end = pos - (long)size;
        if (size == int.MinValue || size > -4 || end > input.Length)
          throw new InvalidDataException("StuffIt fixed-Huffman PackBits block is out of range.");
        written = UnpackBits(input.AsSpan(pos + 4, (int)end - pos - 4), output, written);
        pos = (int)end;
        continue;
      }

      if (size < 10 || (long)pos + size > input.Length)
        throw new InvalidDataException("StuffIt fixed-Huffman block is out of range.");
      var blockEnd = pos + size;
      var count = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(pos + 4));
      var entries = BinaryPrimitives.ReadInt16BigEndian(input.AsSpan(pos + 8));
      var tableStart = pos + 10;
      if (entries is < 0 or > 256 || tableStart + entries > blockEnd)
        throw new InvalidDataException("StuffIt fixed-Huffman translation table is out of range.");
      input.AsSpan(tableStart, entries).CopyTo(translation);
      var codeStart = tableStart + entries;
      if (count > (blockEnd - codeStart) * 8L)
        throw new InvalidDataException("StuffIt fixed-Huffman block claims more symbols than it has bits.");

      var packed = new byte[count];
      var reader = new StuffItBitReader(input, codeStart, blockEnd - codeStart);
      var produced = 0;
      while (produced < packed.Length) {
        var symbol = ReadSymbol(reader);
        if (symbol == EndSymbol) break;
        packed[produced++] = translation[symbol];
      }

      written = UnpackBits(packed.AsSpan(0, produced), output, written);
      pos = blockEnd;
    }

    return output;
  }

  private static int ReadSymbol(StuffItBitReader reader) {
    var node = 0;
    while (true) {
      var next = Tree[(node << 1) | reader.ReadBit()];
      if (next < 0) return ~next;
      node = next;
    }
  }

  /// <summary>Expands Apple PackBits into <paramref name="output"/>, stopping when it is full.</summary>
  private static int UnpackBits(ReadOnlySpan<byte> src, byte[] output, int written) {
    var i = 0;
    while (i < src.Length && written < output.Length) {
      var n = (sbyte)src[i++];
      if (n >= 0) {
        var literal = n + 1;
        if (i + literal > src.Length) throw new InvalidDataException("StuffIt fixed-Huffman PackBits literal runs past its block.");
        var take = Math.Min(literal, output.Length - written);
        src.Slice(i, take).CopyTo(output.AsSpan(written));
        written += take;
        i += literal;
      } else if (n != -128) {
        if (i >= src.Length) throw new InvalidDataException("StuffIt fixed-Huffman PackBits repeat runs past its block.");
        var take = Math.Min(1 - n, output.Length - written);
        output.AsSpan(written, take).Fill(src[i++]);
        written += take;
      }
    }

    return written;
  }

  /// <summary>
  /// Assigns codes in symbol order, not canonically: a symbol as long as the previous one takes the
  /// next code; a longer one takes the next code shifted up to its length; a shorter one takes the
  /// previous code shifted down to its length, plus one.
  /// </summary>
  private static int[] BuildTree() {
    var tree = new int[2 * (Symbols - 1)]; // a complete code over 257 symbols has 256 inner nodes
    tree.AsSpan().Fill(int.MinValue);
    var nodes = 1;
    int symbol = 0, previousLength = 0, previousCode = 0;
    var runs = LengthRuns;
    for (var r = 0; r < runs.Length; r += 2)
      for (var k = 0; k < runs[r]; ++k, ++symbol) {
        int length = runs[r + 1];
        var code = previousLength == 0 ? 0
          : length < previousLength ? (previousCode >> (previousLength - length)) + 1
          : (previousCode + 1) << (length - previousLength);
        (previousLength, previousCode) = (length, code);

        var node = 0;
        for (var b = length - 1; b >= 0; --b) {
          var slot = (node << 1) | ((code >> b) & 1);
          if (b == 0) {
            if (tree[slot] != int.MinValue) throw new InvalidOperationException("Method 6 code table is not prefix-free.");
            tree[slot] = ~symbol;
          } else {
            if (tree[slot] == int.MinValue) {
              if (nodes == Symbols - 1) throw new InvalidOperationException("Method 6 code table is over-full.");
              tree[slot] = nodes++;
            }
            else if (tree[slot] < 0) throw new InvalidOperationException("Method 6 code table is not prefix-free.");
            node = tree[slot];
          }
        }
      }

    if (symbol != Symbols || tree.Contains(int.MinValue))
      throw new InvalidOperationException("Method 6 code table must be a complete code over 257 symbols.");
    return tree;
  }
}
