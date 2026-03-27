namespace FileFormat.Rnc;

/// <summary>
/// Compressor and decompressor for the Rob Northen Compression (RNC) format.
/// RNC is a Huffman + LZSS scheme used in many classic Amiga and DOS games.
/// This implementation supports Method 1 (Huffman + LZSS).
/// </summary>
public static class RncStream {

  private const int HeaderSize = 18;
  private const int MinMatchLength = 2;
  private const int MaxHuffSymbol = 30; // 5-bit numCodes => max 31 entries => symbols 0..30

  // ── Public API ────────────────────────────────────────────────────────────

  /// <summary>
  /// Decompresses an RNC-compressed stream and writes the original data to
  /// <paramref name="output"/>.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    output.Write(DecompressCore(ReadAllBytes(input)));
  }

  /// <summary>
  /// Compresses raw data into the RNC Method 1 format and writes the result to
  /// <paramref name="output"/>.
  /// </summary>
  public static void Compress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    output.Write(CompressCore(ReadAllBytes(input)));
  }

  /// <summary>
  /// Computes the RNC CRC-16 checksum using the custom polynomial 0xCC01.
  /// </summary>
  public static ushort Crc16(ReadOnlySpan<byte> data) => RncCrc16(data);

  // ── CRC-16 ────────────────────────────────────────────────────────────────

  private static ushort RncCrc16(ReadOnlySpan<byte> data) {
    ushort crc = 0;
    foreach (var b in data) {
      crc ^= (ushort)(b << 8);
      for (var i = 0; i < 8; i++)
        crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0xCC01 : crc << 1);
    }
    return crc;
  }

  // ── Decompression ─────────────────────────────────────────────────────────

  private static byte[] DecompressCore(ReadOnlySpan<byte> data) {
    if (data.Length < HeaderSize)
      throw new InvalidDataException("Input is shorter than the minimum RNC header size.");

    if (data[0] != 'R' || data[1] != 'N' || data[2] != 'C')
      throw new InvalidDataException("Invalid RNC magic bytes.");

    var method = data[3];
    if (method == 0) {
      // Method 0: stored (uncompressed)
      var storedSize = ReadBE32(data, 4);
      return data.Slice(HeaderSize, (int)storedSize).ToArray();
    }
    if (method != 1)
      throw new NotSupportedException(
        $"RNC method {method} is not supported. Only methods 0 and 1 are implemented.");

    var uncompressedSize = ReadBE32(data, 4);
    var compressedSize = ReadBE32(data, 8);
    var uncompressedCrc = ReadBE16(data, 12);
    var compressedCrc = ReadBE16(data, 14);
    var packChunks = data[17];

    if (data.Length < HeaderSize + (int)compressedSize)
      throw new InvalidDataException("Input is truncated.");

    var compressedSpan = data.Slice(HeaderSize, (int)compressedSize);

    var actualCompCrc = RncCrc16(compressedSpan);
    if (actualCompCrc != compressedCrc)
      throw new InvalidDataException(
        $"Compressed CRC mismatch: expected 0x{compressedCrc:X4}, got 0x{actualCompCrc:X4}.");

    var output = new byte[uncompressedSize];
    var outPos = 0;
    var br = new BitReader(compressedSpan);

    // Discard 2 initial waste bits
    br.ReadBits(2);

    for (var chunk = 0; chunk < packChunks; chunk++) {
      var rawTbl = ReadHuffmanTable(ref br);
      var distTbl = ReadHuffmanTable(ref br);
      var lenTbl = ReadHuffmanTable(ref br);

      var tupleCount = br.ReadBits(16);

      for (var t = 0; t < tupleCount; t++) {
        var rawCount = DecodeHuffman(ref br, rawTbl);

        for (var r = 0; r < rawCount; r++) {
          if (outPos >= output.Length)
            throw new InvalidDataException("Decompressed data exceeds declared size.");
          output[outPos++] = br.ReadRawByte();
        }

        if (t < tupleCount - 1) {
          var distHigh = DecodeHuffman(ref br, distTbl);
          var distLow = br.ReadBits(8);
          var distance = (distHigh << 8) | distLow;
          if (distance == 0) distance = 1;

          var length = DecodeHuffman(ref br, lenTbl) + MinMatchLength;

          var srcPos = outPos - distance;
          if (srcPos < 0)
            throw new InvalidDataException("Match distance references before start of output.");

          for (var m = 0; m < length; m++) {
            if (outPos >= output.Length)
              throw new InvalidDataException("Decompressed data exceeds declared size.");
            output[outPos++] = output[srcPos + m];
          }
        }
      }
    }

    var actualDecCrc = RncCrc16(output);
    if (actualDecCrc != uncompressedCrc)
      throw new InvalidDataException(
        $"Uncompressed CRC mismatch: expected 0x{uncompressedCrc:X4}, got 0x{actualDecCrc:X4}.");

    return output;
  }

  // ── Huffman table ─────────────────────────────────────────────────────────

  private readonly struct HuffmanTable {
    public readonly int[] Lengths;
    public readonly int[] Codes;
    public readonly int[] Symbols;
    public readonly int Count;
    public readonly bool IsSingleZero;

    private HuffmanTable(bool singleZero) {
      Lengths = []; Codes = []; Symbols = []; Count = 0; IsSingleZero = singleZero;
    }

    private HuffmanTable(int[] lengths, int[] codes, int[] symbols, int count) {
      Lengths = lengths; Codes = codes; Symbols = symbols; Count = count; IsSingleZero = false;
    }

    public static HuffmanTable SingleZero => new(singleZero: true);

    public static HuffmanTable Build(int[] bitLengths) {
      var n = bitLengths.Length;
      var activeCount = 0;
      for (var i = 0; i < n; i++)
        if (bitLengths[i] > 0) activeCount++;

      if (activeCount == 0) return SingleZero;

      var lengths = new int[activeCount];
      var symbols = new int[activeCount];
      var idx = 0;
      for (var i = 0; i < n; i++)
        if (bitLengths[i] > 0) { lengths[idx] = bitLengths[i]; symbols[idx] = i; idx++; }

      // Sort by (length, symbol) for canonical code assignment
      for (var i = 1; i < activeCount; i++) {
        var kl = lengths[i]; var ks = symbols[i]; var j = i - 1;
        while (j >= 0 && (lengths[j] > kl || (lengths[j] == kl && symbols[j] > ks))) {
          lengths[j + 1] = lengths[j]; symbols[j + 1] = symbols[j]; j--;
        }
        lengths[j + 1] = kl; symbols[j + 1] = ks;
      }

      var codes = new int[activeCount];
      var code = 0; var prevLen = lengths[0];
      for (var i = 1; i < activeCount; i++) {
        code++;
        if (lengths[i] > prevLen) { code <<= (lengths[i] - prevLen); prevLen = lengths[i]; }
        codes[i] = code;
      }

      return new HuffmanTable(lengths, codes, symbols, activeCount);
    }

    public (int Code, int Length) Encode(int symbol) {
      for (var i = 0; i < Count; i++)
        if (Symbols[i] == symbol) return (Codes[i], Lengths[i]);
      throw new InvalidOperationException($"Symbol {symbol} not found.");
    }
  }

  private static HuffmanTable ReadHuffmanTable(ref BitReader reader) {
    var numCodes = reader.ReadBits(5);
    if (numCodes == 0) return HuffmanTable.SingleZero;
    var bl = new int[numCodes];
    for (var i = 0; i < numCodes; i++) bl[i] = reader.ReadBits(4);
    return HuffmanTable.Build(bl);
  }

  private static int DecodeHuffman(ref BitReader reader, HuffmanTable table) {
    if (table.IsSingleZero) return 0;
    var code = 0; var len = 0;
    while (len < 16) {
      code = (code << 1) | reader.ReadBits(1); len++;
      for (var i = 0; i < table.Count; i++)
        if (table.Lengths[i] == len && table.Codes[i] == code) return table.Symbols[i];
    }
    throw new InvalidDataException("Failed to decode Huffman symbol.");
  }

  // ── Bit reader (decompression) ────────────────────────────────────────────

  private ref struct BitReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _bytePos;
    private uint _buffer;
    private int _bitsLeft;

    public BitReader(ReadOnlySpan<byte> data) {
      _data = data; _bytePos = 0; _buffer = 0; _bitsLeft = 0;
      Refill();
    }

    private void Refill() {
      while (_bitsLeft <= 16 && _bytePos + 1 < _data.Length) {
        var word = (uint)(_data[_bytePos] | (_data[_bytePos + 1] << 8));
        _bytePos += 2; _buffer |= word << (16 - _bitsLeft); _bitsLeft += 16;
      }
    }

    public int ReadBits(int count) {
      if (count == 0) return 0;
      if (_bitsLeft < count) Refill();
      var result = (int)(_buffer >> (32 - count));
      _buffer <<= count; _bitsLeft -= count;
      return result;
    }

    public byte ReadRawByte() {
      if (_bytePos >= _data.Length)
        throw new InvalidDataException("Unexpected end of data reading raw byte.");
      return _data[_bytePos++];
    }
  }

  // ── Compression ───────────────────────────────────────────────────────────

  private static byte[] CompressCore(ReadOnlySpan<byte> input) {
    if (input.Length == 0)
      return BuildHeader(1, 0, 0, 0, 0, 0, 0, []);

    // Build chunks. Each chunk produces one set of Huffman tables + tuples.
    var chunks = BuildChunks(input);

    if (chunks.Count > MaxChunks) {
      // Too many chunks for the header byte — fall back to method 0 (stored).
      return BuildHeader(0, (uint)input.Length, (uint)input.Length, 0, 0, 0, 0, input);
    }

    var compressedData = EncodeAllChunks(chunks);

    var uncompressedCrc = RncCrc16(input);
    var compressedCrc = RncCrc16(compressedData);

    return BuildHeader(1, (uint)input.Length, (uint)compressedData.Length,
      uncompressedCrc, compressedCrc, 0, (byte)chunks.Count, compressedData);
  }

  /// <summary>
  /// A chunk contains one or more tuples. The last tuple in a chunk has no match.
  /// </summary>
  private sealed class Chunk {
    public readonly List<ChunkTuple> Tuples = [];
  }

  private readonly struct ChunkTuple {
    public readonly byte[] RawBytes;
    public readonly int Distance;
    public readonly int Length;

    public ChunkTuple(byte[] rawBytes, int distance, int length) {
      RawBytes = rawBytes; Distance = distance; Length = length;
    }

    public bool HasMatch => Length >= MinMatchLength;
  }

  /// <summary>
  /// Maximum number of chunks allowed (header stores pack-chunks as a single byte).
  /// </summary>
  private const int MaxChunks = 255;

  /// <summary>
  /// Builds chunks from input data. Uses greedy LZSS matching.
  /// Every non-last tuple in a chunk must have a match (the decompressor expects this).
  /// When no hash-chain match is found, ends the current chunk — unless the chunk limit
  /// (255, from the header byte) is reached, in which case a brute-force match is forced.
  /// </summary>
  private static List<Chunk> BuildChunks(ReadOnlySpan<byte> data) {
    var chunks = new List<Chunk>();
    var pos = 0;
    var maxDist = (MaxHuffSymbol << 8) | 0xFF;
    var maxMatchLen = MaxHuffSymbol + MinMatchLength;

    var hashHead = new int[65536];
    Array.Fill(hashHead, -1);
    var hashPrev = new int[data.Length > 0 ? data.Length : 1];
    Array.Fill(hashPrev, -1);

    var currentChunk = new Chunk();

    while (pos < data.Length) {
      var rawStart = pos;
      var rawCount = 0;

      // Collect raw bytes until we find a match or hit the symbol limit
      while (pos < data.Length && rawCount < MaxHuffSymbol) {
        if (pos >= MinMatchLength) {
          var (_, ml) = FindMatch(data, pos, maxDist, Math.Min(maxMatchLen, data.Length - pos), hashHead, hashPrev);
          if (ml >= MinMatchLength) break;
        }
        HashInsert(data, pos, hashHead, hashPrev);
        rawCount++; pos++;
      }

      if (pos >= data.Length) {
        // End of data: last tuple (no match)
        currentChunk.Tuples.Add(new ChunkTuple(data.Slice(rawStart, rawCount).ToArray(), 0, 0));
        chunks.Add(currentChunk);
        currentChunk = new Chunk();
      } else {
        var (dist, len) = FindMatch(data, pos, maxDist, Math.Min(maxMatchLen, data.Length - pos), hashHead, hashPrev);
        if (len >= MinMatchLength) {
          currentChunk.Tuples.Add(new ChunkTuple(data.Slice(rawStart, rawCount).ToArray(), dist, len));
          for (var i = 0; i < len; i++)
            HashInsert(data, pos + i, hashHead, hashPrev);
          pos += len;
        } else {
          // No match and rawCount = maxRaw. End this chunk.
          currentChunk.Tuples.Add(new ChunkTuple(data.Slice(rawStart, rawCount).ToArray(), 0, 0));
          chunks.Add(currentChunk);
          currentChunk = new Chunk();
        }
      }
    }

    if (currentChunk.Tuples.Count > 0) {
      if (currentChunk.Tuples[^1].HasMatch)
        currentChunk.Tuples.Add(new ChunkTuple([], 0, 0));
      chunks.Add(currentChunk);
    }

    if (chunks.Count == 0) {
      var c = new Chunk();
      c.Tuples.Add(new ChunkTuple([], 0, 0));
      chunks.Add(c);
    }

    return chunks;
  }

  // ── Event-based encoding ──────────────────────────────────────────────────

  private enum EvType : byte { Bits, Raw }

  private readonly struct Ev {
    public readonly EvType Type;
    public readonly int Value;
    public readonly int BitCount;

    private Ev(EvType type, int value, int bitCount) {
      Type = type; Value = value; BitCount = bitCount;
    }

    public static Ev Bits(int value, int count) => new(EvType.Bits, value, count);
    public static Ev Raw(byte value) => new(EvType.Raw, value, 0);
  }

  private static byte[] EncodeAllChunks(List<Chunk> chunks) {
    var events = new List<Ev>();

    // 2 waste bits at the very start
    events.Add(Ev.Bits(0, 2));

    foreach (var chunk in chunks) {
      EncodeChunkEvents(events, chunk);
    }

    return BuildInterleavedStream(events);
  }

  private static void EncodeChunkEvents(List<Ev> events, Chunk chunk) {
    var tuples = chunk.Tuples;

    // Determine Huffman tables for this chunk
    var maxRawSym = 0; var maxDistSym = 0; var maxLenSym = 0;
    foreach (var t in tuples) {
      if (t.RawBytes.Length > maxRawSym) maxRawSym = t.RawBytes.Length;
      if (t.HasMatch) {
        var dh = t.Distance >> 8;
        if (dh > maxDistSym) maxDistSym = dh;
        var ls = t.Length - MinMatchLength;
        if (ls > maxLenSym) maxLenSym = ls;
      }
    }

    var rawBl = BuildFlatBitLengths(maxRawSym + 1);
    var hasMatches = tuples.Exists(t => t.HasMatch);
    var distBl = hasMatches ? BuildFlatBitLengths(maxDistSym + 1) : [];
    var lenBl = hasMatches ? BuildFlatBitLengths(maxLenSym + 1) : [];

    var rawTbl = HuffmanTable.Build(rawBl);
    var distTbl = distBl.Length > 0 ? HuffmanTable.Build(distBl) : HuffmanTable.SingleZero;
    var lenTbl = lenBl.Length > 0 ? HuffmanTable.Build(lenBl) : HuffmanTable.SingleZero;

    // Huffman tables
    AddTableEvents(events, rawBl);
    AddTableEvents(events, distBl);
    AddTableEvents(events, lenBl);

    // Tuple count
    events.Add(Ev.Bits(tuples.Count, 16));

    // Tuples
    for (var t = 0; t < tuples.Count; t++) {
      AddHuffEvents(events, rawTbl, tuples[t].RawBytes.Length);
      foreach (var b in tuples[t].RawBytes)
        events.Add(Ev.Raw(b));

      if (t < tuples.Count - 1 && tuples[t].HasMatch) {
        AddHuffEvents(events, distTbl, tuples[t].Distance >> 8);
        events.Add(Ev.Bits(tuples[t].Distance & 0xFF, 8));
        AddHuffEvents(events, lenTbl, tuples[t].Length - MinMatchLength);
      }
    }
  }

  private static void AddTableEvents(List<Ev> events, int[] bl) {
    events.Add(Ev.Bits(bl.Length, 5));
    for (var i = 0; i < bl.Length; i++) events.Add(Ev.Bits(bl[i], 4));
  }

  private static void AddHuffEvents(List<Ev> events, HuffmanTable tbl, int sym) {
    if (tbl.IsSingleZero) return;
    var (code, len) = tbl.Encode(sym);
    // Emit 1 bit at a time to match the decompressor's DecodeHuffman
    // which calls ReadBits(1) in a loop. This ensures refill timing matches.
    for (var b = len - 1; b >= 0; b--)
      events.Add(Ev.Bits((code >> b) & 1, 1));
  }

  /// <summary>
  /// Builds the interleaved RNC byte stream from events.
  /// The RNC BitReader loads 16-bit LE words for bits and reads raw bytes sequentially.
  /// We simulate the reader's exact behavior to produce the correct byte ordering.
  /// Uses iterative convergence: the refill condition depends on stream length,
  /// which depends on the refill pattern. Converges in 1-2 iterations.
  /// </summary>
  private static byte[] BuildInterleavedStream(List<Ev> events) {
    // Pack all bit events into sequential 16-bit words (MSB-first within word)
    var totalBits = 0;
    var rawByteCount = 0;
    foreach (var e in events) {
      if (e.Type == EvType.Bits) totalBits += e.BitCount;
      else rawByteCount++;
    }

    var wordCount = (totalBits + 15) / 16;
    var words = new ushort[wordCount];
    var wBuf = 0u; var wBits = 0; var wIdx = 0;

    foreach (var e in events) {
      if (e.Type != EvType.Bits) continue;
      for (var b = e.BitCount - 1; b >= 0; b--) {
        wBuf = (wBuf << 1) | (uint)((e.Value >> b) & 1);
        wBits++;
        if (wBits == 16) { words[wIdx++] = (ushort)wBuf; wBuf = 0; wBits = 0; }
      }
    }
    if (wBits > 0) { wBuf <<= (16 - wBits); words[wIdx] = (ushort)wBuf; }

    // Iteratively find stable stream length. Start with base estimate.
    // The reader's Refill checks bytePos+1 < dataLen, so stream length affects layout.
    var streamLen = wordCount * 2 + rawByteCount;
    for (var attempt = 0; attempt < 4; attempt++) {
      var result = SimulateInterleave(events, words, wordCount, streamLen);
      if (result.Length == streamLen)
        return result;
      streamLen = result.Length;
    }
    return SimulateInterleave(events, words, wordCount, streamLen);
  }

  /// <summary>
  /// Simulates the RNC BitReader with a given stream length to produce the interleaved byte stream.
  /// </summary>
  private static byte[] SimulateInterleave(List<Ev> events, ushort[] words, int wordCount, int streamLen) {
    var output = new List<byte>(streamLen + 4);
    var simBits = 0;
    var nextWord = 0;

    void PlaceWord() {
      // Place actual word data, or zero padding if all words exhausted
      var w = nextWord < wordCount ? words[nextWord++] : (ushort)0;
      output.Add((byte)(w & 0xFF));
      output.Add((byte)(w >> 8));
      simBits += 16;
    }

    // Mirror the reader's Refill exactly: while bitsLeft <= 16 && bytePos+1 < dataLen
    void Refill() {
      while (simBits <= 16 && output.Count + 1 < streamLen) {
        PlaceWord();
      }
    }

    // Constructor calls Refill
    Refill();

    // Process events exactly as the reader would
    foreach (var e in events) {
      if (e.Type == EvType.Bits) {
        // ReadBits: if bitsLeft < count, Refill
        if (simBits < e.BitCount)
          Refill();
        simBits -= e.BitCount;
      } else {
        // ReadRawByte: read from current bytePos
        output.Add((byte)e.Value);
      }
    }

    return output.ToArray();
  }

  private static int[] BuildFlatBitLengths(int numSymbols) {
    if (numSymbols <= 0) return [];
    if (numSymbols == 1) return [1];
    var bits = 1;
    while ((1 << bits) < numSymbols) bits++;
    var lengths = new int[numSymbols];
    for (var i = 0; i < numSymbols; i++) lengths[i] = bits;
    return lengths;
  }

  private const int MaxChainSteps = 64;

  private static (int Distance, int Length) FindMatch(
    ReadOnlySpan<byte> data, int pos, int maxDist, int maxLen,
    int[] hashHead, int[] hashPrev
  ) {
    if (maxLen < MinMatchLength || pos + 1 >= data.Length) return (0, 0);
    var bestDist = 0; var bestLen = 0;
    var minPos = Math.Max(0, pos - maxDist);
    var hash = (data[pos] << 8) | data[pos + 1];
    var s = hashHead[hash];
    var steps = 0;
    while (s >= 0 && s >= minPos && steps < MaxChainSteps) {
      if (data[s] == data[pos]) {
        var len = 0;
        while (len < maxLen && pos + len < data.Length && data[s + len] == data[pos + len]) len++;
        if (len >= MinMatchLength && len > bestLen) {
          bestLen = len; bestDist = pos - s;
          if (len >= maxLen) break;
        }
      }
      s = hashPrev[s];
      steps++;
    }
    return (bestDist, bestLen);
  }

  private static void HashInsert(ReadOnlySpan<byte> data, int pos, int[] hashHead, int[] hashPrev) {
    if (pos + 1 >= data.Length) return;
    var hash = (data[pos] << 8) | data[pos + 1];
    hashPrev[pos] = hashHead[hash];
    hashHead[hash] = pos;
  }

  // ── Header ────────────────────────────────────────────────────────────────

  private static byte[] BuildHeader(
    byte method, uint uncompressedSize, uint compressedSize,
    ushort uncompressedCrc, ushort compressedCrc,
    byte leeway, byte packChunks, ReadOnlySpan<byte> compressedData
  ) {
    var result = new byte[HeaderSize + compressedData.Length];
    result[0] = (byte)'R'; result[1] = (byte)'N'; result[2] = (byte)'C'; result[3] = method;
    WriteBE32(result, 4, uncompressedSize);
    WriteBE32(result, 8, compressedSize);
    WriteBE16(result, 12, uncompressedCrc);
    WriteBE16(result, 14, compressedCrc);
    result[16] = leeway; result[17] = packChunks;
    compressedData.CopyTo(result.AsSpan(HeaderSize));
    return result;
  }

  private static uint ReadBE32(ReadOnlySpan<byte> d, int o) =>
    ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

  private static ushort ReadBE16(ReadOnlySpan<byte> d, int o) =>
    (ushort)((d[o] << 8) | d[o + 1]);

  private static void WriteBE32(Span<byte> d, int o, uint v) {
    d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16);
    d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
  }

  private static void WriteBE16(Span<byte> d, int o, ushort v) {
    d[o] = (byte)(v >> 8); d[o + 1] = (byte)v;
  }

  private static byte[] ReadAllBytes(Stream s) {
    using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
  }
}
