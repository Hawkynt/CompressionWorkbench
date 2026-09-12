using System.Buffers.Binary;

namespace FileFormat.Arc;

/// <summary>
/// ARC Squeeze (method 4): static Huffman coding.
/// </summary>
/// <remarks>
/// The compressed data starts with a binary tree stored as 16-bit LE node pairs.
/// The first int16 is the number of tree nodes. Each node has two 16-bit children.
/// Negative values (bit 15 set) indicate leaves; the low byte is the symbol value.
/// Value -256 (0xFF00 unsigned) indicates end-of-data.
/// After the tree, data is encoded LSB-first using the tree for decoding.
/// </remarks>
internal static class ArcSqueeze {
  private const short EndOfData = unchecked((short)0x0100);

  /// <summary>
  /// Decodes ARC squeezed data.
  /// </summary>
  public static byte[] Decode(byte[] compressed, int originalSize) {
    if (compressed.Length < 2)
      throw new InvalidDataException("ARC squeezed data too short.");

    var pos = 0;
    var nodeCount = BinaryPrimitives.ReadInt16LittleEndian(compressed.AsSpan(pos));
    pos += 2;

    if (nodeCount < 0 || nodeCount > 32767)
      throw new InvalidDataException($"ARC squeezed: invalid node count {nodeCount}.");

    // Read tree nodes: each is 2×int16 (left, right children)
    var left = new short[nodeCount];
    var right = new short[nodeCount];

    for (var i = 0; i < nodeCount; ++i) {
      if (pos + 4 > compressed.Length)
        throw new InvalidDataException("ARC squeezed: tree data truncated.");
      left[i] = BinaryPrimitives.ReadInt16LittleEndian(compressed.AsSpan(pos));
      pos += 2;
      right[i] = BinaryPrimitives.ReadInt16LittleEndian(compressed.AsSpan(pos));
      pos += 2;
    }

    // Decode bits LSB-first
    var output = new byte[originalSize];
    var outPos = 0;
    var bitBuf = 0;
    var bitsLeft = 0;

    while (outPos < originalSize) {
      var node = 0; // start at root

      while (node >= 0) {
        if (bitsLeft == 0) {
          if (pos >= compressed.Length)
            throw new InvalidDataException("ARC squeezed: compressed data truncated.");
          bitBuf = compressed[pos++];
          bitsLeft = 8;
        }

        var bit = bitBuf & 1;
        bitBuf >>= 1;
        --bitsLeft;

        node = bit == 0 ? left[node] : right[node];
      }

      // node is negative: leaf
      var symbol = -(node + 1);
      if (symbol == 256) // end of data marker
        break;

      output[outPos++] = (byte)symbol;
    }

    return output;
  }

  /// <summary>
  /// Encodes data using ARC squeeze (static Huffman).
  /// </summary>
  public static byte[] Encode(byte[] data) {
    // Count frequencies
    var freq = new int[257]; // 256 symbols + 1 end marker
    foreach (var b in data)
      ++freq[b];
    freq[256] = 1; // end-of-data marker

    // Build Huffman tree bottom-up
    // Use a simple priority queue approach
    var nodeCapacity = 257 * 2;
    var leftNodes = new short[nodeCapacity];
    var rightNodes = new short[nodeCapacity];
    var nextNode = 0;

    // Create leaf nodes and insert into priority queue
    var pq = new SortedList<(long Freq, int Order), int>(); // freq → node index
    var order = 0;
    for (var sym = 0; sym <= 256; ++sym) {
      if (freq[sym] > 0) {
        var leaf = -(sym + 1); // encode leaf as negative
        pq.Add((freq[sym], order++), leaf);
      }
    }

    if (pq.Count == 0)
      pq.Add((1, order++), -(0 + 1)); // at least one symbol

    // Build tree
    while (pq.Count > 1) {
      var key1 = pq.Keys[0];
      var node1 = pq[key1];
      pq.RemoveAt(0);

      var key2 = pq.Keys[0];
      var node2 = pq[key2];
      pq.RemoveAt(0);

      var parent = nextNode++;
      leftNodes[parent] = (short)node1;
      rightNodes[parent] = (short)node2;

      var combinedFreq = key1.Freq + key2.Freq;
      pq.Add((combinedFreq, order++), parent);
    }

    var root = pq.Count > 0 ? pq.Values[0] : 0;
    var totalNodes = nextNode;

    // If only one symbol, create a dummy root
    if (totalNodes == 0) {
      leftNodes[0] = (short)root;
      rightNodes[0] = (short)root;
      totalNodes = 1;
      root = 0;
    }

    // Remap nodes so root is at index 0 (decoder expects root at 0).
    // Tree was built bottom-up, so root is at totalNodes-1.
    if (root != 0) {
      var newLeft = new short[totalNodes];
      var newRight = new short[totalNodes];
      for (var i = 0; i < totalNodes; ++i) {
        var newIdx = totalNodes - 1 - i;
        newLeft[newIdx] = leftNodes[i] >= 0
          ? (short)(totalNodes - 1 - leftNodes[i]) : leftNodes[i];
        newRight[newIdx] = rightNodes[i] >= 0
          ? (short)(totalNodes - 1 - rightNodes[i]) : rightNodes[i];
      }
      leftNodes = newLeft;
      rightNodes = newRight;
      root = 0;
    }

    // Build code table by traversing tree
    var codes = new int[257];
    var codeLens = new int[257];
    BuildCodes(root, leftNodes, rightNodes, 0, 0, codes, codeLens);

    // Write output
    using var ms = new MemoryStream();

    // Write node count
    Span<byte> buf2 = stackalloc byte[2];
    BinaryPrimitives.WriteInt16LittleEndian(buf2, (short)totalNodes);
    ms.Write(buf2);

    // Write tree nodes
    Span<byte> buf4 = stackalloc byte[4];
    for (var i = 0; i < totalNodes; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(buf4, leftNodes[i]);
      BinaryPrimitives.WriteInt16LittleEndian(buf4[2..], rightNodes[i]);
      ms.Write(buf4);
    }

    // Write encoded data LSB-first
    var bitBuffer = 0;
    var bitCount = 0;

    void WriteBits(int code, int len) {
      bitBuffer |= code << bitCount;
      bitCount += len;
      while (bitCount >= 8) {
        ms.WriteByte((byte)(bitBuffer & 0xFF));
        bitBuffer >>= 8;
        bitCount -= 8;
      }
    }

    foreach (var b in data)
      WriteBits(codes[b], codeLens[b]);

    // Write end marker
    WriteBits(codes[256], codeLens[256]);

    // Flush remaining bits
    if (bitCount > 0)
      ms.WriteByte((byte)(bitBuffer & 0xFF));

    return ms.ToArray();
  }

  private static void BuildCodes(int node, short[] left, short[] right,
      int code, int depth, int[] codes, int[] codeLens) {
    if (node < 0) {
      var sym = -(node + 1);
      if (sym >= 0 && sym <= 256) {
        codes[sym] = code;
        codeLens[sym] = Math.Max(depth, 1);
      }
      return;
    }

    BuildCodes(left[node], left, right, code, depth + 1, codes, codeLens);
    BuildCodes(right[node], left, right, code | (1 << depth), depth + 1, codes, codeLens);
  }
}
