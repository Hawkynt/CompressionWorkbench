#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Hcom;

/// <summary>
/// Encodes 8-bit unsigned PCM into an HCOM data fork compatible with
/// <see cref="HcomFormatDescriptor"/> (and sox's <c>hcom.c</c> container layout):
/// the header (<c>HCOM</c> magic, sample count, checksum, delta compress type, rate
/// divisor, dictionary size), a Huffman tree over the byte-wise deltas stored as
/// <c>(leftson, rightson)</c> int16 child pairs (leaves: <c>leftson = -1</c>,
/// <c>rightson = delta</c>), a padding byte, then the MSB-first big-endian 32-bit-word
/// bitstream. Always emits delta mode (<c>compresstype = 1</c>).
/// </summary>
internal static class HcomEncoder {

  public static byte[] Encode(byte[] pcm8, int divisor) {
    if (divisor is < 1 or > 4) divisor = 1;

    // 1. Delta sequence: first delta is from an implicit 0 start, matching the decoder
    //    which seeds its running sample at 0.
    var deltas = new int[pcm8.Length];
    var prev = 0;
    for (var i = 0; i < pcm8.Length; ++i) {
      deltas[i] = unchecked((byte)(pcm8[i] - prev));   // 0..255 delta byte
      prev = pcm8[i];
    }

    // 2. Frequency table over the 256 possible delta bytes.
    var freq = new long[256];
    foreach (var d in deltas)
      ++freq[d];

    // 3. Build a Huffman tree. Nodes live in a list; leaves carry their delta byte.
    var nodes = new List<Node>();
    var heap = new List<int>();   // indices into `nodes`, kept as a simple priority list
    for (var sym = 0; sym < 256; ++sym) {
      if (freq[sym] == 0) continue;
      nodes.Add(new Node { Freq = freq[sym], Left = -1, Right = -1, Symbol = sym });
      heap.Add(nodes.Count - 1);
    }

    // Degenerate inputs: ensure at least two leaves so the tree has a real bit code.
    if (heap.Count == 0) {
      nodes.Add(new Node { Freq = 1, Left = -1, Right = -1, Symbol = 0 });
      heap.Add(0);
    }
    if (heap.Count == 1) {
      var only = nodes[heap[0]].Symbol;
      var filler = only == 0 ? 1 : 0;
      nodes.Add(new Node { Freq = 0, Left = -1, Right = -1, Symbol = filler });
      heap.Add(nodes.Count - 1);
    }

    while (heap.Count > 1) {
      var (a, b) = TwoSmallest(nodes, heap);
      var parent = new Node {
        Freq = nodes[a].Freq + nodes[b].Freq, Left = a, Right = b, Symbol = -1,
      };
      nodes.Add(parent);
      heap.Add(nodes.Count - 1);
    }
    var root = heap[0];

    // 4. Lay the tree out into the dict array with the ROOT at index 0 (the decoder
    //    starts there). Walk the node tree, assigning each node a dict slot.
    var dictLeft = new List<short>();
    var dictRight = new List<short>();
    var codes = new (uint Bits, int Len)[256];
    AssignSlot(nodes, root, dictLeft, dictRight);
    BuildCodes(nodes, root, 0u, 0, codes);

    // 5. Emit the MSB-first bitstream as 32-bit big-endian words.
    var bits = new BitWriter();
    foreach (var d in deltas)
      bits.Write(codes[d].Bits, codes[d].Len);
    var (streamBytes, checksum) = bits.ToWordsBigEndian();

    // 6. Assemble the fork.
    var dictSize = dictLeft.Count;
    using var ms = new MemoryStream();
    Span<byte> u32 = stackalloc byte[4];
    Span<byte> u16 = stackalloc byte[2];

    ms.Write("HCOM"u8);
    BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)pcm8.Length); ms.Write(u32);
    BinaryPrimitives.WriteUInt32BigEndian(u32, checksum); ms.Write(u32);
    BinaryPrimitives.WriteUInt32BigEndian(u32, 1); ms.Write(u32);              // delta
    BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)divisor); ms.Write(u32);
    BinaryPrimitives.WriteUInt16BigEndian(u16, (ushort)dictSize); ms.Write(u16);
    for (var i = 0; i < dictSize; ++i) {
      BinaryPrimitives.WriteInt16BigEndian(u16, dictLeft[i]); ms.Write(u16);
      BinaryPrimitives.WriteInt16BigEndian(u16, dictRight[i]); ms.Write(u16);
    }
    ms.WriteByte(0);            // padding byte before the bitstream
    ms.Write(streamBytes);
    return ms.ToArray();
  }

  private struct Node {
    public long Freq;
    public int Left, Right; // child node indices, -1 for a leaf
    public int Symbol;      // delta byte for a leaf, -1 otherwise
    public int Slot;        // assigned dict index (filled during layout)
  }

  private static (int, int) TwoSmallest(List<Node> nodes, List<int> heap) {
    var i0 = 0;
    for (var k = 1; k < heap.Count; ++k)
      if (nodes[heap[k]].Freq < nodes[heap[i0]].Freq) i0 = k;
    var a = heap[i0];
    heap.RemoveAt(i0);
    var i1 = 0;
    for (var k = 1; k < heap.Count; ++k)
      if (nodes[heap[k]].Freq < nodes[heap[i1]].Freq) i1 = k;
    var b = heap[i1];
    heap.RemoveAt(i1);
    return (a, b);
  }

  /// <summary>Lays the node tree into parallel left/right child-index arrays, root first.</summary>
  private static int AssignSlot(List<Node> nodes, int node, List<short> left, List<short> right) {
    var slot = left.Count;
    left.Add(0);
    right.Add(0);
    var n = nodes[node];
    n.Slot = slot;
    nodes[node] = n;

    if (n.Left < 0) {
      // Leaf: leftson = -1, rightson = delta value.
      left[slot] = -1;
      right[slot] = (short)n.Symbol;
    } else {
      var l = AssignSlot(nodes, n.Left, left, right);
      var r = AssignSlot(nodes, n.Right, left, right);
      left[slot] = (short)l;
      right[slot] = (short)r;
    }
    return slot;
  }

  private static void BuildCodes(List<Node> nodes, int node, uint bits, int len, (uint, int)[] codes) {
    var n = nodes[node];
    if (n.Left < 0) {
      codes[n.Symbol] = (bits, Math.Max(1, len));
      return;
    }
    BuildCodes(nodes, n.Left, bits << 1, len + 1, codes);          // bit 0 → left
    BuildCodes(nodes, n.Right, (bits << 1) | 1, len + 1, codes);   // bit 1 → right
  }

  private sealed class BitWriter {
    private readonly List<bool> _bits = [];

    public void Write(uint bits, int len) {
      for (var i = len - 1; i >= 0; --i)
        _bits.Add(((bits >> i) & 1) != 0);
    }

    /// <summary>Packs the bits MSB-first into big-endian 32-bit words; returns bytes + checksum.</summary>
    public (byte[] Bytes, uint Checksum) ToWordsBigEndian() {
      var wordCount = (_bits.Count + 31) / 32;
      if (wordCount == 0) wordCount = 1;
      var bytes = new byte[wordCount * 4];
      uint checksum = 0;
      for (var w = 0; w < wordCount; ++w) {
        uint word = 0;
        for (var b = 0; b < 32; ++b) {
          var idx = w * 32 + b;
          var bit = idx < _bits.Count && _bits[idx];
          word = (word << 1) | (bit ? 1u : 0u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(w * 4, 4), word);
        checksum += word;
      }
      return (bytes, checksum);
    }
  }
}
