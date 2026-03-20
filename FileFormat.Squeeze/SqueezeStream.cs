using System.Buffers.Binary;

namespace FileFormat.Squeeze;

/// <summary>
/// Provides static methods for reading and writing the CP/M Squeeze (.sqz / .??q) file format.
/// </summary>
/// <remarks>
/// Richard Greenlaw's file squeezer (1981) uses a stored Huffman tree with an explicit node array.
/// The format consists of:
/// <list type="bullet">
///   <item><description>2-byte magic (0x76, 0xFF = 0xFF76 LE).</description></item>
///   <item><description>Null-terminated ASCII original filename.</description></item>
///   <item><description>2-byte checksum (sum of all original bytes mod 65536, LE).</description></item>
///   <item><description>2-byte node count (LE).</description></item>
///   <item><description>Node array: each node is two signed 16-bit LE values (left, right).
///   Non-negative values are child node indices; negative values encode leaves as -(symbol + 1).</description></item>
///   <item><description>Huffman-coded bitstream (LSB-first bit order) terminated by EOF symbol (256).</description></item>
/// </list>
/// </remarks>
public static class SqueezeStream {

  /// <summary>
  /// Decompresses a Squeeze-format stream from <paramref name="input"/> and writes the result to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing Squeeze-compressed data.</param>
  /// <param name="output">The stream to which the decompressed data is written.</param>
  /// <exception cref="InvalidDataException">
  /// Thrown when the magic bytes are invalid, the tree is malformed, or the checksum does not match.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read 2-byte magic
    Span<byte> magicBuf = stackalloc byte[2];
    input.ReadExactly(magicBuf);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(magicBuf);
    if (magic != SqueezeConstants.Magic)
      throw new InvalidDataException($"Invalid Squeeze magic: 0x{magic:X4}, expected 0x{SqueezeConstants.Magic:X4}.");

    // Read null-terminated original filename (discard)
    ReadNullTerminatedString(input);

    // Read 2-byte checksum
    Span<byte> checksumBuf = stackalloc byte[2];
    input.ReadExactly(checksumBuf);
    var expectedChecksum = BinaryPrimitives.ReadUInt16LittleEndian(checksumBuf);

    // Read node count
    Span<byte> countBuf = stackalloc byte[2];
    input.ReadExactly(countBuf);
    var nodeCount = BinaryPrimitives.ReadUInt16LittleEndian(countBuf);

    if (nodeCount == 0)
      throw new InvalidDataException("Squeeze node count is zero.");
    if (nodeCount > SqueezeConstants.MaxNodes)
      throw new InvalidDataException($"Squeeze node count {nodeCount} exceeds maximum {SqueezeConstants.MaxNodes}.");

    // Read node array
    var left = new short[nodeCount];
    var right = new short[nodeCount];
    Span<byte> nodeBuf = stackalloc byte[4];
    for (int i = 0; i < nodeCount; i++) {
      input.ReadExactly(nodeBuf);
      left[i] = BinaryPrimitives.ReadInt16LittleEndian(nodeBuf);
      right[i] = BinaryPrimitives.ReadInt16LittleEndian(nodeBuf[2..]);
    }

    // Decode bitstream (LSB-first)
    using var result = new MemoryStream();
    int currentByte = 0;
    int bitsLeft = 0;
    ushort checksum = 0;

    while (true) {
      // Walk tree from root (node 0)
      int node = 0;
      while (true) {
        if (node < 0 || node >= nodeCount)
          throw new InvalidDataException($"Squeeze tree references invalid node index {node}.");

        // Get next bit
        if (bitsLeft == 0) {
          currentByte = input.ReadByte();
          if (currentByte < 0)
            throw new InvalidDataException("Unexpected end of Squeeze bitstream.");
          bitsLeft = 8;
        }

        int bit = currentByte & 1;
        currentByte >>= 1;
        bitsLeft--;

        // Navigate: 0 = left, 1 = right
        short child = bit == 0 ? left[node] : right[node];

        if (child < 0) {
          // Leaf: symbol = -(child + 1)
          int symbol = -(child + 1);
          if (symbol == SqueezeConstants.EofMarker)
            goto done;
          if (symbol is < 0 or > 255)
            throw new InvalidDataException($"Squeeze tree contains invalid symbol {symbol}.");
          result.WriteByte((byte)symbol);
          checksum += (ushort)symbol;
          break;
        }

        node = child;
      }
    }

    done:
    if (checksum != expectedChecksum)
      throw new InvalidDataException($"Squeeze checksum mismatch: computed 0x{checksum:X4}, expected 0x{expectedChecksum:X4}.");

    result.Position = 0;
    result.CopyTo(output);
  }

  /// <summary>
  /// Compresses data from <paramref name="input"/> and writes a Squeeze-format stream to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing uncompressed data.</param>
  /// <param name="output">The stream to which the Squeeze-compressed data is written.</param>
  /// <param name="originalFilename">
  /// The original filename to embed in the header. Defaults to an empty string.
  /// </param>
  public static void Compress(Stream input, Stream output, string originalFilename = "") {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read all input
    var data = ReadAllBytes(input);

    // Compute checksum
    ushort checksum = 0;
    for (int i = 0; i < data.Length; i++)
      checksum += data[i];

    // Build frequency table (256 byte symbols + EOF)
    var freq = new long[257];
    for (int i = 0; i < data.Length; i++)
      freq[data[i]]++;
    freq[SqueezeConstants.EofMarker] = 1;

    // Build Huffman tree and serialize to node array
    BuildTree(freq, out var left, out var right, out var codes, out var codeLens);
    int nodeCount = left.Length;

    // Write header
    Span<byte> header = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(header, SqueezeConstants.Magic);
    output.Write(header);

    // Write null-terminated filename
    foreach (var ch in originalFilename)
      output.WriteByte((byte)ch);
    output.WriteByte(0);

    // Write checksum
    Span<byte> checksumBuf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(checksumBuf, checksum);
    output.Write(checksumBuf);

    // Write node count
    Span<byte> countBuf = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(countBuf, (ushort)nodeCount);
    output.Write(countBuf);

    // Write node array
    Span<byte> nodeBuf = stackalloc byte[4];
    for (int i = 0; i < nodeCount; i++) {
      BinaryPrimitives.WriteInt16LittleEndian(nodeBuf, left[i]);
      BinaryPrimitives.WriteInt16LittleEndian(nodeBuf[2..], right[i]);
      output.Write(nodeBuf);
    }

    // Encode data + EOF (LSB-first)
    int bitBuffer = 0;
    int bitCount = 0;

    for (int i = 0; i < data.Length; i++)
      WriteBits(output, codes[data[i]], codeLens[data[i]], ref bitBuffer, ref bitCount);

    // Write EOF symbol
    WriteBits(output, codes[SqueezeConstants.EofMarker], codeLens[SqueezeConstants.EofMarker], ref bitBuffer, ref bitCount);

    // Flush remaining bits (pad with zeros)
    if (bitCount > 0)
      output.WriteByte((byte)bitBuffer);
  }

  private static void WriteBits(Stream output, uint code, int length, ref int bitBuffer, ref int bitCount) {
    // Codes are stored LSB-first: we emit the lowest bit of code first.
    // code is already in LSB-first order (bit 0 = first bit to emit).
    bitBuffer |= (int)((long)code << bitCount);
    bitCount += length;

    while (bitCount >= 8) {
      output.WriteByte((byte)(bitBuffer & 0xFF));
      bitBuffer >>= 8;
      bitCount -= 8;
    }
  }

  /// <summary>
  /// Builds a Huffman tree from symbol frequencies and serializes it into the Squeeze node-array format.
  /// </summary>
  private static void BuildTree(long[] freq, out short[] left, out short[] right, out uint[] codes, out int[] codeLens) {
    // Count active symbols
    int symbolCount = 0;
    for (int i = 0; i < freq.Length; i++)
      if (freq[i] > 0)
        symbolCount++;

    if (symbolCount == 0)
      throw new InvalidOperationException("No symbols to encode.");

    // Special case: single symbol (only EOF or single byte + EOF)
    if (symbolCount == 1) {
      // Create a minimal tree: one root node with the symbol on both sides
      int sym = -1;
      for (int i = 0; i < freq.Length; i++)
        if (freq[i] > 0) { sym = i; break; }

      left = new short[1];
      right = new short[1];
      left[0] = (short)(-(sym + 1));
      right[0] = (short)(-(sym + 1));
      codes = new uint[257];
      codeLens = new int[257];
      codes[sym] = 0;
      codeLens[sym] = 1;
      return;
    }

    // Build Huffman tree using a priority queue (min-heap by frequency)
    // Each entry: (frequency, nodeIndex). Leaf nodes get negative indices: -(symbol+1).
    // Internal nodes get indices starting from 0 in the node list.
    var nodeLeft = new List<short>();
    var nodeRight = new List<short>();

    // Priority queue: (frequency, identifier)
    // Identifier < 0: leaf = -(symbol+1), Identifier >= 0: internal node index
    var pq = new PriorityQueue<int, long>();
    for (int i = 0; i < freq.Length; i++)
      if (freq[i] > 0)
        pq.Enqueue(-(i + 1), freq[i]);

    while (pq.Count > 1) {
      pq.TryDequeue(out int id1, out long f1);
      pq.TryDequeue(out int id2, out long f2);

      int nodeIndex = nodeLeft.Count;
      // Convert identifiers to node-array values:
      // Negative ids are already leaf encodings (-(symbol+1))
      // Non-negative ids are internal node indices
      nodeLeft.Add(IdToNodeValue(id1));
      nodeRight.Add(IdToNodeValue(id2));

      pq.Enqueue(nodeIndex, f1 + f2);
    }

    // The last item in the queue is the root
    pq.TryDequeue(out int rootId, out _);

    // If the root is a leaf (only possible with 1 symbol, handled above), wrap it
    if (rootId < 0) {
      int nodeIndex = nodeLeft.Count;
      nodeLeft.Add((short)rootId);
      nodeRight.Add((short)rootId);
      rootId = nodeIndex;
    }

    // The root must be the last node added. The Squeeze format expects node 0 = root.
    // We need to remap so the root is at index 0.
    int totalNodes = nodeLeft.Count;
    left = new short[totalNodes];
    right = new short[totalNodes];

    // Build a remapping: root goes to index 0, others shift
    var remap = new int[totalNodes];
    remap[rootId] = 0;
    int next = 1;
    for (int i = 0; i < totalNodes; i++) {
      if (i == rootId) continue;
      remap[i] = next++;
    }

    // Apply remapping
    for (int i = 0; i < totalNodes; i++) {
      int newIdx = remap[i];
      left[newIdx] = RemapChild(nodeLeft[i], remap);
      right[newIdx] = RemapChild(nodeRight[i], remap);
    }

    // Generate codes by walking the tree (LSB-first: first bit is lowest bit)
    codes = new uint[257];
    codeLens = new int[257];
    GenerateCodes(left, right, 0, 0, 0, codes, codeLens);
  }

  private static short IdToNodeValue(int id) =>
    id < 0 ? (short)id : (short)id;

  private static short RemapChild(short child, int[] remap) =>
    child >= 0 ? (short)remap[child] : child;

  private static void GenerateCodes(short[] left, short[] right, int node, uint code, int depth, uint[] codes, int[] codeLens) {
    short l = left[node];
    short r = right[node];

    // Going left adds a 0-bit at position 'depth' (code unchanged), going right adds a 1-bit.
    // Each child is one level deeper, so the code length is depth + 1.

    if (l < 0) {
      int sym = -(l + 1);
      int len = Math.Max(depth + 1, 1);
      codes[sym] = code;
      codeLens[sym] = len;
    } else {
      GenerateCodes(left, right, l, code, depth + 1, codes, codeLens);
    }

    if (r < 0) {
      int sym = -(r + 1);
      int len = Math.Max(depth + 1, 1);
      codes[sym] = code | (1u << depth);
      codeLens[sym] = len;
    } else {
      GenerateCodes(left, right, r, code | (1u << depth), depth + 1, codes, codeLens);
    }
  }

  private static string ReadNullTerminatedString(Stream stream) {
    var bytes = new List<byte>();
    while (true) {
      int b = stream.ReadByte();
      if (b <= 0) break;
      bytes.Add((byte)b);
    }

    return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
  }

  private static byte[] ReadAllBytes(Stream stream) {
    if (stream is MemoryStream ms && ms.Position == 0)
      return ms.ToArray();

    using var buf = new MemoryStream();
    stream.CopyTo(buf);
    return buf.ToArray();
  }
}
