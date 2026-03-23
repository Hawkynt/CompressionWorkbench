using System.Buffers.Binary;

namespace FileFormat.Freeze;

/// <summary>
/// Provides static methods for compressing and decompressing data using the Freeze 2.0 format.
/// </summary>
/// <remarks>
/// Freeze/Melt is a Unix compression program from the early 1990s by Leonid A. Broukhis.
/// It uses LZ77 with Huffman coding. This implementation uses the <c>0x1F 0x9F</c> magic
/// (Freeze 2.0) and stores LZ77-compressed data with canonical Huffman trees.
/// </remarks>
public static class FreezeStream {

  private const byte Magic0 = 0x1F;
  private const byte Magic1 = 0x9F;
  private const int WindowSize = 16384; // 16 KB sliding window (Freeze 2)
  private const int MinMatchLength = 3;
  private const int MaxMatchLength = 258;
  private const int HashBits = 15;
  private const int HashSize = 1 << HashBits;
  private const int EndSymbol = 286;
  private const int LitLenSymbols = 287; // 0-255 literals, 256-285 lengths, 286 end
  private const int DistSymbols = 30;

  /// <summary>
  /// Compresses data from <paramref name="input"/> and writes a Freeze 2.0 stream to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing uncompressed data.</param>
  /// <param name="output">The stream to which the compressed data is written.</param>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var src = ms.ToArray();

    // Write magic.
    output.WriteByte(Magic0);
    output.WriteByte(Magic1);

    // Write original size (4 bytes LE).
    Span<byte> sizeBytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)src.Length);
    output.Write(sizeBytes);

    if (src.Length == 0) {
      WriteEmptyBitstream(output);
      return;
    }

    // LZ77 parse.
    var tokens = Lz77Parse(src);

    // Build frequency tables.
    var litLenFreq = new int[LitLenSymbols];
    var distFreq = new int[DistSymbols];

    foreach (var token in tokens) {
      if (token.Length == 0) {
        litLenFreq[token.Literal]++;
      } else {
        var (lengthSymbol, _, _) = EncodeLengthSymbol(token.Length);
        litLenFreq[lengthSymbol]++;
        var (distSymbol, _, _) = EncodeDistSymbol(token.Distance);
        distFreq[distSymbol]++;
      }
    }

    litLenFreq[EndSymbol]++;

    // Build Huffman trees.
    var litLenLengths = BuildCanonicalLengths(litLenFreq, 15);
    var distLengths = BuildCanonicalLengths(distFreq, 15);

    // Build canonical codes from lengths.
    var litLenCodes = BuildCanonicalCodes(litLenLengths);
    var distCodes = BuildCanonicalCodes(distLengths);

    // Write bitstream.
    var writer = new BitWriter();

    // Write lit/len tree: 287 symbols x 4 bits each.
    for (var i = 0; i < LitLenSymbols; i++)
      writer.WriteBits(litLenLengths[i], 4);

    // Write dist tree: 30 symbols x 4 bits each.
    for (var i = 0; i < DistSymbols; i++)
      writer.WriteBits(distLengths[i], 4);

    // Write tokens.
    foreach (var token in tokens) {
      if (token.Length == 0) {
        writer.WriteBits(litLenCodes[token.Literal].Code, litLenCodes[token.Literal].Length);
      } else {
        var (lengthSymbol, extraBits, extraValue) = EncodeLengthSymbol(token.Length);
        writer.WriteBits(litLenCodes[lengthSymbol].Code, litLenCodes[lengthSymbol].Length);
        if (extraBits > 0)
          writer.WriteBits(extraValue, extraBits);

        var (distSymbol, dExtraBits, dExtraValue) = EncodeDistSymbol(token.Distance);
        writer.WriteBits(distCodes[distSymbol].Code, distCodes[distSymbol].Length);
        if (dExtraBits > 0)
          writer.WriteBits(dExtraValue, dExtraBits);
      }
    }

    // End symbol.
    writer.WriteBits(litLenCodes[EndSymbol].Code, litLenCodes[EndSymbol].Length);

    var compressed = writer.ToArray();
    output.Write(compressed);
  }

  /// <summary>
  /// Decompresses a Freeze 2.0 stream from <paramref name="input"/> and writes the result to <paramref name="output"/>.
  /// </summary>
  /// <param name="input">The stream containing Freeze-compressed data.</param>
  /// <param name="output">The stream to which the decompressed data is written.</param>
  /// <exception cref="InvalidDataException">Thrown when the magic bytes are invalid or the data is corrupted.</exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // Read magic + size.
    Span<byte> header = stackalloc byte[6];
    input.ReadExactly(header);

    if (header[0] != Magic0 || header[1] != Magic1)
      throw new InvalidDataException($"Invalid Freeze magic: 0x{header[0]:X2}{header[1]:X2}, expected 0x1F9F.");

    var originalSize = BinaryPrimitives.ReadUInt32LittleEndian(header[2..]);

    if (originalSize == 0)
      return;

    // Read remaining data as bitstream.
    using var ms = new MemoryStream();
    input.CopyTo(ms);
    var data = ms.ToArray();
    var reader = new BitReader(data);

    // Read lit/len tree lengths (287 symbols, 4 bits each).
    var litLenLengths = new int[LitLenSymbols];
    for (var i = 0; i < LitLenSymbols; i++)
      litLenLengths[i] = reader.ReadBits(4);

    // Read dist tree lengths (30 symbols, 4 bits each).
    var distLengths = new int[DistSymbols];
    for (var i = 0; i < DistSymbols; i++)
      distLengths[i] = reader.ReadBits(4);

    // Build decoding tables.
    var litLenTable = BuildDecodingTable(litLenLengths);
    var distTable = BuildDecodingTable(distLengths);

    // Decode.
    var dst = new byte[originalSize];
    var pos = 0;

    while (pos < (int)originalSize) {
      var symbol = litLenTable.Decode(reader);

      if (symbol == EndSymbol)
        break;

      if (symbol < 256) {
        dst[pos++] = (byte)symbol;
      } else {
        var length = DecodeLengthValue(symbol, reader);
        var distSymbol = distTable.Decode(reader);
        var distance = DecodeDistValue(distSymbol, reader);

        if (distance > pos)
          throw new InvalidDataException($"Freeze match distance {distance} exceeds current position {pos}.");

        for (var i = 0; i < length; i++) {
          if (pos >= (int)originalSize)
            throw new InvalidDataException("Freeze decompressed data exceeds expected size.");
          dst[pos] = dst[pos - distance];
          pos++;
        }
      }
    }

    output.Write(dst, 0, pos);
  }

  #region LZ77 Parsing

  private readonly record struct Token(byte Literal, int Length, int Distance);

  private static List<Token> Lz77Parse(byte[] src) {
    var tokens = new List<Token>();
    var hashTable = new int[HashSize];
    var chain = new int[src.Length];
    Array.Fill(hashTable, -1);
    Array.Fill(chain, -1);

    var pos = 0;
    while (pos < src.Length) {
      var bestLen = 0;
      var bestDist = 0;

      if (pos + MinMatchLength <= src.Length) {
        var h = Hash3(src, pos);
        var candidate = hashTable[h];
        var minPos = Math.Max(0, pos - WindowSize);
        var maxLen = Math.Min(src.Length - pos, MaxMatchLength);
        var attempts = 128;

        while (candidate >= minPos && attempts-- > 0) {
          if (src[candidate + bestLen] == src[pos + bestLen]) {
            var len = 0;
            while (len < maxLen && src[candidate + len] == src[pos + len])
              len++;

            if (len >= MinMatchLength && len > bestLen) {
              bestLen = len;
              bestDist = pos - candidate;
              if (len == maxLen)
                break;
            }
          }

          var prev = chain[candidate];
          if (prev >= candidate)
            break;
          candidate = prev;
        }
      }

      if (bestLen >= MinMatchLength) {
        tokens.Add(new Token(0, bestLen, bestDist));
        for (var i = 0; i < bestLen && pos + i + 2 < src.Length; i++)
          InsertHash(hashTable, chain, src, pos + i);
        pos += bestLen;
      } else {
        tokens.Add(new Token(src[pos], 0, 0));
        if (pos + 2 < src.Length)
          InsertHash(hashTable, chain, src, pos);
        pos++;
      }
    }

    return tokens;
  }

  private static int Hash3(byte[] data, int pos) =>
    ((data[pos] << 8 | data[pos + 1]) * 0x9E37 + data[pos + 2]) & (HashSize - 1);

  private static void InsertHash(int[] hashTable, int[] chain, byte[] data, int pos) {
    var h = Hash3(data, pos);
    chain[pos] = hashTable[h];
    hashTable[h] = pos;
  }

  #endregion

  #region Length / Distance Encoding

  private static readonly (int Base, int Extra)[] LengthTable = [
    (3, 0), (4, 0), (5, 0), (6, 0), (7, 0), (8, 0), (9, 0), (10, 0),
    (11, 1), (13, 1), (15, 1), (17, 1),
    (19, 2), (23, 2), (27, 2), (31, 2),
    (35, 3), (43, 3), (51, 3), (59, 3),
    (67, 4), (83, 4), (99, 4), (115, 4),
    (131, 5), (163, 5), (195, 5), (227, 5),
    (258, 0), (258, 0)
  ];

  private static (int Symbol, int ExtraBits, int ExtraValue) EncodeLengthSymbol(int length) {
    for (var i = 29; i >= 0; i--) {
      if (length >= LengthTable[i].Base) {
        return (256 + i, LengthTable[i].Extra, length - LengthTable[i].Base);
      }
    }

    return (256, 0, 0);
  }

  private static int DecodeLengthValue(int symbol, BitReader reader) {
    var index = symbol - 256;
    if (index < 0 || index >= 30)
      throw new InvalidDataException($"Invalid Freeze length symbol: {symbol}.");
    var baseLen = LengthTable[index].Base;
    var extra = LengthTable[index].Extra;
    return extra > 0 ? baseLen + reader.ReadBits(extra) : baseLen;
  }

  private static readonly (int Base, int Extra)[] DistTable = [
    (1, 0), (2, 0), (3, 0), (4, 0),
    (5, 1), (7, 1),
    (9, 2), (13, 2),
    (17, 3), (25, 3),
    (33, 4), (49, 4),
    (65, 5), (97, 5),
    (129, 6), (193, 6),
    (257, 7), (385, 7),
    (513, 8), (769, 8),
    (1025, 9), (1537, 9),
    (2049, 10), (3073, 10),
    (4097, 11), (6145, 11),
    (8193, 12), (12289, 12),
    (16385, 13), (24577, 13)
  ];

  private static (int Symbol, int ExtraBits, int ExtraValue) EncodeDistSymbol(int distance) {
    for (var i = 29; i >= 0; i--) {
      if (distance >= DistTable[i].Base)
        return (i, DistTable[i].Extra, distance - DistTable[i].Base);
    }

    return (0, 0, 0);
  }

  private static int DecodeDistValue(int symbol, BitReader reader) {
    if (symbol < 0 || symbol >= 30)
      throw new InvalidDataException($"Invalid Freeze distance symbol: {symbol}.");
    var baseDist = DistTable[symbol].Base;
    var extra = DistTable[symbol].Extra;
    return extra > 0 ? baseDist + reader.ReadBits(extra) : baseDist;
  }

  #endregion

  #region Huffman Coding

  /// <summary>
  /// Builds canonical Huffman code lengths from frequency counts, clamped to <paramref name="maxBits"/>.
  /// </summary>
  private static int[] BuildCanonicalLengths(int[] frequencies, int maxBits) {
    var n = frequencies.Length;

    // Count non-zero symbols.
    var activeCount = 0;
    var singleActive = -1;
    for (var i = 0; i < n; i++) {
      if (frequencies[i] > 0) {
        activeCount++;
        singleActive = i;
      }
    }

    var lengths = new int[n];
    if (activeCount == 0)
      return lengths;

    if (activeCount == 1) {
      lengths[singleActive] = 1;
      return lengths;
    }

    // Build Huffman tree to get code lengths.
    lengths = BuildHuffmanTreeLengths(frequencies);

    // Clamp to maxBits and fix Kraft inequality if needed.
    var clamped = false;
    for (var i = 0; i < n; i++) {
      if (lengths[i] > maxBits) {
        lengths[i] = maxBits;
        clamped = true;
      }
    }

    if (clamped)
      FixKraftInequality(lengths, maxBits);

    return lengths;
  }

  private static int[] BuildHuffmanTreeLengths(int[] frequencies) {
    var n = frequencies.Length;
    var lengths = new int[n];

    // Collect active symbols.
    var active = new List<int>();
    for (var i = 0; i < n; i++) {
      if (frequencies[i] > 0)
        active.Add(i);
    }

    if (active.Count <= 1) {
      if (active.Count == 1)
        lengths[active[0]] = 1;
      return lengths;
    }

    // Build Huffman tree using a priority queue.
    var pq = new PriorityQueue<List<(int Symbol, int Depth)>, long>();
    foreach (var sym in active)
      pq.Enqueue([(sym, 0)], frequencies[sym]);

    while (pq.Count > 1) {
      pq.TryDequeue(out var left, out var freqLeft);
      pq.TryDequeue(out var right, out var freqRight);

      var merged = new List<(int Symbol, int Depth)>(left!.Count + right!.Count);
      foreach (var (sym, depth) in left)
        merged.Add((sym, depth + 1));
      foreach (var (sym, depth) in right)
        merged.Add((sym, depth + 1));

      pq.Enqueue(merged, freqLeft + freqRight);
    }

    pq.TryDequeue(out var root, out _);
    foreach (var (sym, depth) in root!)
      lengths[sym] = depth;

    return lengths;
  }

  /// <summary>
  /// Adjusts code lengths so that the Kraft inequality is satisfied after clamping.
  /// </summary>
  private static void FixKraftInequality(int[] lengths, int maxBits) {
    for (var iteration = 0; iteration < 200; iteration++) {
      long kraftNumerator = 0;
      for (var i = 0; i < lengths.Length; i++) {
        if (lengths[i] > 0)
          kraftNumerator += 1L << (maxBits - lengths[i]);
      }

      var kraftDenominator = 1L << maxBits;
      if (kraftNumerator <= kraftDenominator)
        break;

      // Find the shortest non-zero code and make it 1 bit longer.
      var minLen = maxBits + 1;
      var minIdx = -1;
      for (var i = 0; i < lengths.Length; i++) {
        if (lengths[i] > 0 && lengths[i] < minLen) {
          minLen = lengths[i];
          minIdx = i;
        }
      }

      if (minIdx >= 0 && minLen < maxBits)
        lengths[minIdx]++;
      else
        break;
    }
  }

  private readonly record struct HuffCode(int Code, int Length);

  /// <summary>
  /// Builds canonical Huffman codes from code lengths.
  /// </summary>
  private static HuffCode[] BuildCanonicalCodes(int[] lengths) {
    var n = lengths.Length;
    var codes = new HuffCode[n];

    var maxLen = 0;
    for (var i = 0; i < n; i++) {
      if (lengths[i] > maxLen)
        maxLen = lengths[i];
    }

    if (maxLen == 0)
      return codes;

    // Count codes of each length.
    var blCount = new int[maxLen + 1];
    for (var i = 0; i < n; i++) {
      if (lengths[i] > 0)
        blCount[lengths[i]]++;
    }

    // Find smallest code for each code length.
    var nextCode = new int[maxLen + 1];
    var code = 0;
    for (var bits = 1; bits <= maxLen; bits++) {
      code = (code + blCount[bits - 1]) << 1;
      nextCode[bits] = code;
    }

    // Assign canonical codes.
    for (var i = 0; i < n; i++) {
      var len = lengths[i];
      if (len > 0) {
        codes[i] = new HuffCode(nextCode[len], len);
        nextCode[len]++;
      }
    }

    return codes;
  }

  /// <summary>
  /// Builds a tree-based decoding table for canonical Huffman codes.
  /// </summary>
  private static HuffDecodingTable BuildDecodingTable(int[] lengths) {
    var codes = BuildCanonicalCodes(lengths);
    return new HuffDecodingTable(codes, lengths);
  }

  /// <summary>
  /// A binary-tree-based Huffman decoder.
  /// </summary>
  private sealed class HuffDecodingTable {
    private int[] _symbols;
    private int[] _left;
    private int[] _right;
    private int _nodeCount;

    public HuffDecodingTable(HuffCode[] codes, int[] lengths) {
      var initialCapacity = 2;
      for (var i = 0; i < lengths.Length; i++) {
        if (lengths[i] > 0)
          initialCapacity += lengths[i];
      }

      initialCapacity = Math.Max(initialCapacity, 4);
      _symbols = new int[initialCapacity];
      _left = new int[initialCapacity];
      _right = new int[initialCapacity];
      Array.Fill(_symbols, -1);
      Array.Fill(_left, -1);
      Array.Fill(_right, -1);
      _nodeCount = 1; // root is node 0

      for (var i = 0; i < codes.Length; i++) {
        if (lengths[i] <= 0)
          continue;

        var node = 0; // root
        for (var bit = lengths[i] - 1; bit >= 0; bit--) {
          var b = (codes[i].Code >> bit) & 1;
          if (b == 0) {
            if (_left[node] < 0) {
              _left[node] = _nodeCount;
              GrowIfNeeded();
              _nodeCount++;
            }

            node = _left[node];
          } else {
            if (_right[node] < 0) {
              _right[node] = _nodeCount;
              GrowIfNeeded();
              _nodeCount++;
            }

            node = _right[node];
          }
        }

        _symbols[node] = i;
      }
    }

    private void GrowIfNeeded() {
      if (_nodeCount < _symbols.Length)
        return;

      var newSize = _symbols.Length * 2;
      var newSymbols = new int[newSize];
      var newLeft = new int[newSize];
      var newRight = new int[newSize];
      Array.Fill(newSymbols, -1);
      Array.Fill(newLeft, -1);
      Array.Fill(newRight, -1);
      Array.Copy(_symbols, newSymbols, _symbols.Length);
      Array.Copy(_left, newLeft, _left.Length);
      Array.Copy(_right, newRight, _right.Length);
      _symbols = newSymbols;
      _left = newLeft;
      _right = newRight;
    }

    /// <summary>
    /// Decodes the next Huffman symbol from the bitstream.
    /// </summary>
    public int Decode(BitReader reader) {
      var node = 0;
      while (_symbols[node] < 0) {
        var bit = reader.ReadBit();
        node = bit == 0 ? _left[node] : _right[node];
        if (node < 0)
          throw new InvalidDataException("Invalid Freeze Huffman code encountered.");
      }

      return _symbols[node];
    }
  }

  #endregion

  #region Empty Bitstream

  private static void WriteEmptyBitstream(Stream output) {
    var writer = new BitWriter();

    // Lit/len tree: only end symbol (286) has length 1, rest are 0.
    for (var i = 0; i < LitLenSymbols; i++)
      writer.WriteBits(i == EndSymbol ? 1 : 0, 4);

    // Dist tree: all zeros.
    for (var i = 0; i < DistSymbols; i++)
      writer.WriteBits(0, 4);

    // End symbol: single-symbol canonical code = bit 0.
    writer.WriteBits(0, 1);

    output.Write(writer.ToArray());
  }

  #endregion

  #region Bit I/O

  /// <summary>
  /// MSB-first bit writer that accumulates bits into a byte buffer.
  /// </summary>
  private sealed class BitWriter {
    private readonly MemoryStream _buffer = new();
    private int _currentByte;
    private int _bitsUsed;

    /// <summary>
    /// Writes <paramref name="count"/> bits from <paramref name="value"/> (MSB-first).
    /// </summary>
    public void WriteBits(int value, int count) {
      for (var i = count - 1; i >= 0; i--) {
        _currentByte = (_currentByte << 1) | ((value >> i) & 1);
        _bitsUsed++;
        if (_bitsUsed == 8) {
          _buffer.WriteByte((byte)_currentByte);
          _currentByte = 0;
          _bitsUsed = 0;
        }
      }
    }

    /// <summary>
    /// Returns the accumulated bytes, flushing any partial byte with zero-padding.
    /// </summary>
    public byte[] ToArray() {
      if (_bitsUsed > 0) {
        _currentByte <<= (8 - _bitsUsed);
        _buffer.WriteByte((byte)_currentByte);
      }

      return _buffer.ToArray();
    }
  }

  /// <summary>
  /// MSB-first bit reader that reads bits from a byte array.
  /// </summary>
  private sealed class BitReader(byte[] data) {
    private int _bytePos;
    private int _bitPos = 8;

    /// <summary>
    /// Reads a single bit (0 or 1) from the bitstream.
    /// </summary>
    public int ReadBit() {
      if (_bitPos >= 8) {
        if (_bytePos >= data.Length)
          throw new InvalidDataException("Unexpected end of Freeze compressed data.");
        _bitPos = 0;
        _bytePos++;
      }

      var bit = (data[_bytePos - 1] >> (7 - _bitPos)) & 1;
      _bitPos++;
      return bit;
    }

    /// <summary>
    /// Reads <paramref name="count"/> bits from the bitstream, MSB-first.
    /// </summary>
    public int ReadBits(int count) {
      var value = 0;
      for (var i = 0; i < count; i++)
        value = (value << 1) | ReadBit();
      return value;
    }
  }

  #endregion
}
