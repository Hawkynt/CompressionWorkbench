using System.Buffers.Binary;

namespace FileFormat.NuFx;

/// <summary>
/// Headerless Richard Greenlaw Squeeze stream used by NuFX thread format 1.
/// </summary>
/// <remarks>
/// Standalone Squeeze files prepend magic/checksum/filename fields. NuFX deliberately omits
/// that outer header and stores only the node table followed by the LSB-first Huffman stream.
/// The input is first transformed by the historical 0x90 run-length stage. NuFX v3 supplies
/// integrity through the thread CRC, so there is no standalone Squeeze checksum here.
/// </remarks>
internal static class SqueezeStream {
  private const byte RleDelimiter = 0x90;
  private const int EofSymbol = 256;
  private const int SymbolCount = 257;

  public static void Compress(Stream input, Stream output, string originalFilename = "") {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    _ = originalFilename;

    using var raw = new MemoryStream();
    input.CopyTo(raw);
    var rle = EncodeRle(raw.ToArray());

    // The historical representation of an empty stream is a zero-node tree with
    // no bitstream at all. EOF is implicit in that special case.
    if (rle.Length == 0) {
      WriteUInt16(output, 0);
      return;
    }

    var used = new bool[SymbolCount];
    foreach (var value in rle)
      used[value] = true;
    used[EofSymbol] = true;
    var symbols = Enumerable.Range(0, SymbolCount).Where(i => used[i]).ToArray();

    var root = BuildBalancedTree(symbols, 0, symbols.Length);
    var nodes = new List<SerializedNode>();
    _ = SerializeTree(root, nodes);
    if (nodes.Count > ushort.MaxValue)
      throw new InvalidDataException("Squeeze tree exceeds the 16-bit node-count field.");

    WriteUInt16(output, checked((ushort)nodes.Count));
    foreach (var node in nodes) {
      WriteInt16(output, checked((short)node.Left));
      WriteInt16(output, checked((short)node.Right));
    }

    var codes = new Code[SymbolCount];
    BuildCodes(root, 0, 0, codes);
    var bits = new LsbWriter(output);
    foreach (var value in rle) {
      var code = codes[value];
      bits.Write(code.Bits, code.Length);
    }
    var eof = codes[EofSymbol];
    bits.Write(eof.Bits, eof.Length);
    bits.FinishWithGuardByte();
  }

  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var nodeCount = ReadUInt16(input);
    if (nodeCount == 0)
      return;
    if (nodeCount > 256)
      throw new InvalidDataException($"NuFX Squeeze node count {nodeCount} exceeds the 257-symbol tree limit.");

    var nodes = new SerializedNode[nodeCount];
    for (var i = 0; i < nodes.Length; i++)
      nodes[i] = new SerializedNode(ReadInt16(input), ReadInt16(input));

    var reader = new LsbReader(input);
    var sawDelimiter = false;
    var haveLast = false;
    byte last = 0;

    while (true) {
      var symbol = DecodeSymbol(nodes, reader);
      if (symbol == EofSymbol)
        break;
      if (symbol is < 0 or > 255)
        throw new InvalidDataException($"NuFX Squeeze tree produced invalid symbol {symbol}.");

      var value = (byte)symbol;
      if (sawDelimiter) {
        if (value == 0) {
          output.WriteByte(RleDelimiter);
          last = RleDelimiter;
          haveLast = true;
        } else {
          if (!haveLast)
            throw new InvalidDataException("NuFX Squeeze RLE count appears before a literal value.");
          // The first copy of the run was already emitted before the delimiter.
          for (var i = 1; i < value; i++)
            output.WriteByte(last);
        }
        sawDelimiter = false;
        continue;
      }

      if (value == RleDelimiter) {
        sawDelimiter = true;
        continue;
      }

      output.WriteByte(value);
      last = value;
      haveLast = true;
    }

    if (sawDelimiter)
      throw new InvalidDataException("NuFX Squeeze stream ends in an incomplete RLE escape.");
  }

  private static byte[] EncodeRle(ReadOnlySpan<byte> source) {
    using var output = new MemoryStream(source.Length);
    var offset = 0;
    while (offset < source.Length) {
      var value = source[offset];
      if (value == RleDelimiter) {
        output.WriteByte(RleDelimiter);
        output.WriteByte(0);
        offset++;
        continue;
      }

      var count = 1;
      while (offset + count < source.Length && source[offset + count] == value && count < 255)
        count++;

      output.WriteByte(value);
      if (count == 2)
        output.WriteByte(value);
      else if (count >= 3) {
        output.WriteByte(RleDelimiter);
        output.WriteByte((byte)count);
      }
      offset += count;
    }
    return output.ToArray();
  }

  private static TreeNode BuildBalancedTree(int[] symbols, int start, int count) {
    if (count == 1)
      return new TreeNode(symbols[start], null, null);
    var leftCount = count / 2;
    return new TreeNode(null,
      BuildBalancedTree(symbols, start, leftCount),
      BuildBalancedTree(symbols, start + leftCount, count - leftCount));
  }

  private static int SerializeTree(TreeNode node, List<SerializedNode> nodes) {
    if (node.Symbol.HasValue)
      return -(node.Symbol.Value + 1);

    var index = nodes.Count;
    nodes.Add(default);
    var left = SerializeTree(node.Left!, nodes);
    var right = SerializeTree(node.Right!, nodes);
    nodes[index] = new SerializedNode(left, right);
    return index;
  }

  private static void BuildCodes(TreeNode node, uint bits, int depth, Code[] codes) {
    if (node.Symbol.HasValue) {
      if (depth is < 1 or > 16)
        throw new InvalidDataException($"Squeeze code length {depth} is outside the historical 1..16-bit range.");
      codes[node.Symbol.Value] = new Code(bits, depth);
      return;
    }
    BuildCodes(node.Left!, bits, depth + 1, codes);
    BuildCodes(node.Right!, bits | (1u << depth), depth + 1, codes);
  }

  private static int DecodeSymbol(SerializedNode[] nodes, LsbReader reader) {
    var node = 0;
    var guard = 0;
    while (true) {
      if ((uint)node >= (uint)nodes.Length)
        throw new InvalidDataException($"NuFX Squeeze tree references invalid node {node}.");
      if (++guard > nodes.Length + 1)
        throw new InvalidDataException("NuFX Squeeze tree contains a cycle.");

      var child = reader.ReadBit() == 0 ? nodes[node].Left : nodes[node].Right;
      if (child < 0)
        return -(child + 1);
      node = child;
    }
  }

  private static ushort ReadUInt16(Stream input) {
    Span<byte> bytes = stackalloc byte[2];
    input.ReadExactly(bytes);
    return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
  }

  private static short ReadInt16(Stream input) {
    Span<byte> bytes = stackalloc byte[2];
    input.ReadExactly(bytes);
    return BinaryPrimitives.ReadInt16LittleEndian(bytes);
  }

  private static void WriteUInt16(Stream output, ushort value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteInt16(Stream output, short value) {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private sealed record TreeNode(int? Symbol, TreeNode? Left, TreeNode? Right);
  private readonly record struct SerializedNode(int Left, int Right);
  private readonly record struct Code(uint Bits, int Length);

  private sealed class LsbWriter(Stream output) {
    private uint _bits;
    private int _count;

    public void Write(uint value, int width) {
      this._bits |= value << this._count;
      this._count += width;
      while (this._count >= 8) {
        output.WriteByte((byte)this._bits);
        this._bits >>= 8;
        this._count -= 8;
      }
    }

    public void FinishWithGuardByte() {
      if (this._count > 0)
        output.WriteByte((byte)this._bits);
      // Original SQ/USQ implementations commonly leave one zero look-ahead byte.
      // It is harmless after EOF and improves compatibility with decoders that read ahead.
      output.WriteByte(0);
      this._bits = 0;
      this._count = 0;
    }
  }

  private sealed class LsbReader(Stream input) {
    private int _current;
    private int _bitsLeft;

    public int ReadBit() {
      if (this._bitsLeft == 0) {
        this._current = input.ReadByte();
        if (this._current < 0)
          throw new InvalidDataException("NuFX Squeeze Huffman bitstream is truncated.");
        this._bitsLeft = 8;
      }
      var bit = this._current & 1;
      this._current >>= 1;
      this._bitsLeft--;
      return bit;
    }
  }
}
