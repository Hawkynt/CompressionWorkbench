namespace FileFormat.Rzip;

/// <summary>
/// Provides static methods for compressing and decompressing data in the RZIP format.
/// </summary>
/// <remarks>
/// <para>
/// Two-stage design after Andrew Tridgell, "Efficient Algorithms for Sorting and
/// Synchronization" (PhD thesis, ANU, 1999), chapter 3. Stage 1 indexes the whole input
/// with a polynomial rolling hash over a 16-byte window and never discards a position,
/// so a match can reach arbitrarily far back rather than only within a 32K or 64K
/// sliding window - that reach is the entire point of rzip. Stage 2 entropy codes the
/// literal bytes.
/// </para>
/// <para>
/// Upstream rzip hands stage 2 to an external bzip2. This implementation carries its own
/// order-0 canonical Huffman coder instead, so the format is self-contained: a reader
/// needs nothing but this file, and the byte stream does not move whenever the bzip2
/// encoder's heuristics change. The layout is therefore ours, not upstream rzip's, and
/// the two are not interchangeable.
/// </para>
/// <para>
/// Format layout:
/// <code>
/// Header:
///   "RZIP"                4 bytes
///   major, minor          1 byte each
///   original size         4 bytes, big-endian
/// Body:
///   token count           LEB128 varint
///   tokens, repeated:
///     0x00 + length             literal run of that many bytes
///     0x01 + distance + length  copy from that far back in the output
///                               (all three values LEB128 varints)
///   literal count         LEB128 varint
///   literal mode          1 byte: 0 raw, 1 canonical Huffman
///   mode 0: the literal bytes verbatim
///   mode 1: 256 code lengths, one byte each, then the packed codes
/// </code>
/// The literal bytes of every literal run are concatenated and coded once, at the end,
/// so a single Huffman table covers the whole input.
/// </para>
/// </remarks>
public static class RzipStream {

  /// <summary>A literal run, or a match of <see cref="Length"/> bytes <see cref="Distance"/> back.</summary>
  private readonly record struct Token(bool IsLiteral, long Distance, long Length);

  /// <summary>
  /// Compresses data into RZIP format.
  /// </summary>
  /// <param name="input">The input stream containing data to compress.</param>
  /// <param name="output">The output stream to write RZIP-compressed data to.</param>
  public static void Compress(Stream input, Stream output) {
    byte[] data;
    using (var buffer = new MemoryStream()) {
      input.CopyTo(buffer);
      data = buffer.ToArray();
    }

    Span<byte> header = stackalloc byte[RzipConstants.HeaderSize];
    RzipConstants.Magic.CopyTo(header);
    header[4] = RzipConstants.VersionMajor;
    header[5] = RzipConstants.VersionMinor;
    WriteUInt32BigEndian(header[6..], (uint)data.Length);
    output.Write(header);

    var body = new List<byte>(data.Length / 2 + 64);
    var literals = new List<byte>(data.Length);
    var tokens = Tokenize(data, literals);

    WriteVarInt(body, tokens.Count);
    foreach (var token in tokens) {
      if (token.IsLiteral) {
        body.Add(RzipConstants.TagLiteral);
        WriteVarInt(body, token.Length);
        continue;
      }

      body.Add(RzipConstants.TagMatch);
      WriteVarInt(body, token.Distance);
      WriteVarInt(body, token.Length);
    }

    WriteLiteralStream(body, literals);

    var bodyArray = body.ToArray();
    output.Write(bodyArray, 0, bodyArray.Length);
  }

  /// <summary>
  /// Decompresses an RZIP stream.
  /// </summary>
  /// <param name="input">The input stream containing RZIP-compressed data.</param>
  /// <param name="output">The output stream to write decompressed data to.</param>
  /// <exception cref="InvalidDataException">The stream does not contain valid RZIP data.</exception>
  public static void Decompress(Stream input, Stream output) {
    byte[] data;
    using (var buffer = new MemoryStream()) {
      input.CopyTo(buffer);
      data = buffer.ToArray();
    }

    if (data.Length < RzipConstants.HeaderSize)
      throw new InvalidDataException("Truncated RZIP header.");
    if (!data.AsSpan(0, 4).SequenceEqual(RzipConstants.Magic))
      throw new InvalidDataException("Invalid RZIP magic bytes.");

    var major = data[4];
    if (major != RzipConstants.VersionMajor)
      throw new InvalidDataException($"Unsupported RZIP version: {major}.{data[5]}");

    var originalSize = ReadUInt32BigEndian(data.AsSpan(6, 4));
    var position = RzipConstants.HeaderSize;

    var tokenCount = ReadVarInt(data, ref position);
    var tokens = new List<Token>((int)Math.Min(tokenCount, 1 << 20));
    for (long i = 0; i < tokenCount; ++i) {
      var tag = ReadByte(data, ref position);
      if (tag == RzipConstants.TagLiteral) {
        tokens.Add(new(true, 0, ReadVarInt(data, ref position)));
        continue;
      }

      if (tag != RzipConstants.TagMatch)
        throw new InvalidDataException($"Unknown RZIP token tag: {tag}");

      var distance = ReadVarInt(data, ref position);
      tokens.Add(new(false, distance, ReadVarInt(data, ref position)));
    }

    var literals = ReadLiteralStream(data, ref position);

    var result = new byte[originalSize];
    var produced = 0;
    var literalPosition = 0;

    foreach (var token in tokens) {
      if (token.IsLiteral) {
        if (literalPosition + token.Length > literals.Length || produced + token.Length > result.Length)
          throw new InvalidDataException("RZIP literal run overruns the stream.");

        for (long i = 0; i < token.Length; ++i)
          result[produced++] = literals[literalPosition++];

        continue;
      }

      if (token.Distance <= 0 || token.Distance > produced)
        throw new InvalidDataException(
          $"RZIP match distance {token.Distance} exceeds the {produced} bytes decoded so far.");
      if (produced + token.Length > result.Length)
        throw new InvalidDataException("RZIP match overruns the declared size.");

      // Byte by byte: a match may overlap itself, which is how runs are encoded.
      var source = produced - (int)token.Distance;
      for (long i = 0; i < token.Length; ++i)
        result[produced++] = result[source++];
    }

    if (produced != result.Length)
      throw new InvalidDataException(
        $"RZIP produced {produced} bytes but the header declared {result.Length}.");

    output.Write(result, 0, result.Length);
  }

  /// <summary>
  /// Greedy long-range parse. Every position's rolling hash is recorded and never
  /// expired by distance, so a match can reach back to the start of the input.
  /// </summary>
  /// <remarks>
  /// Each hash value keeps the most recent <see cref="RzipConstants.HashBucketCapacity"/>
  /// positions in increasing order; the newest
  /// <see cref="RzipConstants.CandidateSearchLimit"/> of them are examined newest first,
  /// and a candidate must be strictly longer than the incumbent to displace it, so among
  /// equally long matches the nearest wins. The current position joins its bucket after
  /// the search, and positions covered by an emitted match are never indexed.
  /// </remarks>
  private static List<Token> Tokenize(byte[] data, List<byte> literals) {
    var tokens = new List<Token>();
    var length = data.Length;
    if (length == 0)
      return tokens;

    if (length < RzipConstants.MinMatch) {
      tokens.Add(new(true, 0, length));
      literals.AddRange(data);
      return tokens;
    }

    var basePower = 1u;
    for (var i = 0; i < RzipConstants.MinMatch - 1; ++i)
      basePower = unchecked(basePower * RzipConstants.RollingHashBase);

    var buckets = new Dictionary<uint, List<int>>();
    var lastPosition = length - RzipConstants.MinMatch;
    var literalStart = 0;
    var hash = HashAt(data, 0);
    var hashValid = true;

    for (var position = 0; position <= lastPosition;) {
      if (!hashValid) {
        hash = HashAt(data, position);
        hashValid = true;
      }

      var bestLength = 0;
      var bestPosition = -1;

      if (buckets.TryGetValue(hash, out var bucket)) {
        var stop = Math.Max(0, bucket.Count - RzipConstants.CandidateSearchLimit);
        for (var index = bucket.Count - 1; index >= stop; --index) {
          var candidate = bucket[index];
          var limit = length - position;
          var matched = 0;
          while (matched < limit && data[candidate + matched] == data[position + matched])
            ++matched;

          if (matched < RzipConstants.MinMatch || matched <= bestLength)
            continue;

          bestLength = matched;
          bestPosition = candidate;
        }
      } else
        buckets[hash] = bucket = [];

      bucket.Add(position);
      if (bucket.Count > RzipConstants.HashBucketCapacity)
        bucket.RemoveAt(0);

      if (bestLength >= RzipConstants.MinMatch) {
        if (position > literalStart) {
          tokens.Add(new(true, 0, position - literalStart));
          literals.AddRange(data.AsSpan(literalStart, position - literalStart));
        }

        tokens.Add(new(false, position - bestPosition, bestLength));
        position += bestLength;
        literalStart = position;
        hashValid = false;
        continue;
      }

      if (position < lastPosition)
        hash = unchecked((hash - (uint)data[position] * basePower) * RzipConstants.RollingHashBase
          + data[position + RzipConstants.MinMatch]);

      ++position;
    }

    if (literalStart < length) {
      tokens.Add(new(true, 0, length - literalStart));
      literals.AddRange(data.AsSpan(literalStart, length - literalStart));
    }

    return tokens;
  }

  private static uint HashAt(byte[] data, int position) {
    var hash = 0u;
    for (var i = 0; i < RzipConstants.MinMatch; ++i)
      hash = unchecked(hash * RzipConstants.RollingHashBase + data[position + i]);

    return hash;
  }

  /// <summary>
  /// Orders the coded symbols the way the canonical assignment does - by code length,
  /// then by symbol - which is the order a canonical decoder walks.
  /// </summary>
  private static (int[] CountPerLength, int[] SymbolsInCodeOrder) BuildDecodeTables(int[] codeLengths) {
    var countPerLength = new int[RzipConstants.MaxCodeLength + 1];
    foreach (var codeLength in codeLengths) {
      if (codeLength == 0)
        continue;

      if (codeLength > RzipConstants.MaxCodeLength)
        throw new InvalidDataException($"RZIP literal code length {codeLength} exceeds the maximum.");

      ++countPerLength[codeLength];
    }

    var ordered = new List<int>();
    for (var length = 1; length <= RzipConstants.MaxCodeLength; ++length)
      for (var symbol = 0; symbol < codeLengths.Length; ++symbol)
        if (codeLengths[symbol] == length)
          ordered.Add(symbol);

    return (countPerLength, ordered.ToArray());
  }

  private static void WriteLiteralStream(List<byte> output, List<byte> literals) {
    WriteVarInt(output, literals.Count);
    if (literals.Count == 0) {
      output.Add(RzipConstants.LiteralModeRaw);
      return;
    }

    var frequencies = new int[RzipConstants.LiteralAlphabetSize];
    foreach (var literal in literals)
      ++frequencies[literal];

    var codeLengths = BuildHuffmanLengths(frequencies);
    var longest = 0;
    long codedBits = 0;
    for (var symbol = 0; symbol < RzipConstants.LiteralAlphabetSize; ++symbol) {
      if (codeLengths[symbol] > longest)
        longest = codeLengths[symbol];

      codedBits += (long)frequencies[symbol] * codeLengths[symbol];
    }

    // The table costs a fixed 256 bytes, so coding only pays once the codes save more
    // than that. Without this check a short literal run inflates: 256 literals of
    // distinct bytes coded at 8 bits each cost 256 bytes of table for no gain at all.
    var codedCost = RzipConstants.LiteralAlphabetSize + (codedBits + 7) / 8;

    if (longest == 0 || longest > RzipConstants.MaxCodeLength || codedCost >= literals.Count) {
      output.Add(RzipConstants.LiteralModeRaw);
      output.AddRange(literals);
      return;
    }

    output.Add(RzipConstants.LiteralModeHuffman);
    foreach (var codeLength in codeLengths)
      output.Add((byte)codeLength);

    var codes = BuildCanonicalCodes(codeLengths);
    var writer = new BitWriter(output);
    foreach (var literal in literals)
      writer.WriteBits(codes[literal], codeLengths[literal]);

    writer.Flush();
  }

  private static byte[] ReadLiteralStream(byte[] data, ref int position) {
    var count = ReadVarInt(data, ref position);
    var mode = ReadByte(data, ref position);
    if (count == 0)
      return [];

    if (count > data.Length * 8L)
      throw new InvalidDataException($"RZIP declares {count} literals, more than the stream can hold.");

    var literals = new byte[count];
    if (mode == RzipConstants.LiteralModeRaw) {
      for (long i = 0; i < count; ++i)
        literals[i] = ReadByte(data, ref position);

      return literals;
    }

    if (mode != RzipConstants.LiteralModeHuffman)
      throw new InvalidDataException($"Unknown RZIP literal mode: {mode}");

    var codeLengths = new int[RzipConstants.LiteralAlphabetSize];
    for (var symbol = 0; symbol < RzipConstants.LiteralAlphabetSize; ++symbol)
      codeLengths[symbol] = ReadByte(data, ref position);

    var (countPerLength, symbolsInCodeOrder) = BuildDecodeTables(codeLengths);
    var reader = new BitReader(data, position);
    for (long i = 0; i < count; ++i)
      literals[i] = (byte)reader.DecodeSymbol(countPerLength, symbolsInCodeOrder);

    position += reader.BytesConsumed;
    return literals;
  }

  /// <summary>
  /// Builds order-0 Huffman code lengths from observed frequencies. Symbols that never
  /// occur keep length 0; a single distinct symbol is forced to length 1.
  /// </summary>
  /// <remarks>
  /// Nodes are combined smallest first. Ties are broken by creation order - the source
  /// symbols in ascending value, then each combined node as it is made - which makes the
  /// resulting lengths a total function of the frequencies alone, with no dependence on
  /// how the candidate set happens to be stored.
  /// </remarks>
  private static int[] BuildHuffmanLengths(int[] frequencies) {
    var lengths = new int[RzipConstants.LiteralAlphabetSize];

    var nodes = new List<Node>();
    for (var symbol = 0; symbol < RzipConstants.LiteralAlphabetSize; ++symbol)
      if (frequencies[symbol] > 0)
        nodes.Add(new(frequencies[symbol], nodes.Count, symbol, null, null));

    if (nodes.Count == 0)
      return lengths;

    if (nodes.Count == 1) {
      lengths[nodes[0].Symbol] = 1;
      return lengths;
    }

    var sequence = nodes.Count;
    while (nodes.Count > 1) {
      var first = IndexOfSmallest(nodes, -1);
      var second = IndexOfSmallest(nodes, first);
      var left = nodes[first];
      var right = nodes[second];

      nodes.RemoveAt(Math.Max(first, second));
      nodes.RemoveAt(Math.Min(first, second));
      nodes.Add(new(left.Frequency + right.Frequency, sequence++, -1, left, right));
    }

    AssignLengths(nodes[0], 0, lengths);
    return lengths;
  }

  private sealed record Node(long Frequency, int Sequence, int Symbol, Node? Left, Node? Right);

  private static int IndexOfSmallest(List<Node> nodes, int skip) {
    var best = -1;
    for (var i = 0; i < nodes.Count; ++i) {
      if (i == skip)
        continue;

      if (best >= 0 && (nodes[i].Frequency > nodes[best].Frequency
          || (nodes[i].Frequency == nodes[best].Frequency && nodes[i].Sequence > nodes[best].Sequence)))
        continue;

      best = i;
    }

    return best;
  }

  private static void AssignLengths(Node node, int depth, int[] lengths) {
    if (node.Symbol >= 0) {
      lengths[node.Symbol] = depth;
      return;
    }

    AssignLengths(node.Left!, depth + 1, lengths);
    AssignLengths(node.Right!, depth + 1, lengths);
  }

  /// <summary>
  /// Assigns canonical codes: shorter codes first, and equal-length codes in ascending
  /// symbol order.
  /// </summary>
  private static int[] BuildCanonicalCodes(int[] codeLengths) {
    var longest = 0;
    foreach (var codeLength in codeLengths)
      if (codeLength > longest)
        longest = codeLength;

    var codes = new int[codeLengths.Length];
    if (longest == 0)
      return codes;

    var countPerLength = new int[longest + 1];
    foreach (var codeLength in codeLengths)
      if (codeLength > 0)
        ++countPerLength[codeLength];

    var nextCode = new int[longest + 1];
    var code = 0;
    for (var bits = 1; bits <= longest; ++bits) {
      code = (code + countPerLength[bits - 1]) * 2;
      nextCode[bits] = code;
    }

    for (var symbol = 0; symbol < codeLengths.Length; ++symbol)
      if (codeLengths[symbol] != 0)
        codes[symbol] = nextCode[codeLengths[symbol]]++;

    return codes;
  }

  /// <summary>Packs bits into bytes least-significant bit first; each code is written most-significant bit first.</summary>
  private sealed class BitWriter(List<byte> output) {
    private int _buffer;
    private int _count;

    public void WriteBits(int value, int length) {
      for (var i = length - 1; i >= 0; --i) {
        this._buffer |= ((value >> i) & 1) << this._count;
        if (++this._count < 8)
          continue;

        output.Add((byte)this._buffer);
        this._buffer = 0;
        this._count = 0;
      }
    }

    public void Flush() {
      if (this._count == 0)
        return;

      output.Add((byte)this._buffer);
      this._buffer = 0;
      this._count = 0;
    }
  }

  /// <summary>Reads the bit packing produced by <see cref="BitWriter"/>.</summary>
  private sealed class BitReader(byte[] data, int start) {
    private int _bytePosition;
    private int _buffer;
    private int _bitPosition = 8;

    public int BytesConsumed => this._bytePosition;

    private int ReadBit() {
      if (this._bitPosition == 8) {
        if (start + this._bytePosition >= data.Length)
          throw new InvalidDataException("Unexpected end of the RZIP literal bit stream.");

        this._buffer = data[start + this._bytePosition++];
        this._bitPosition = 0;
      }

      return (this._buffer >> this._bitPosition++) & 1;
    }

    /// <summary>
    /// Walks the canonical code space one bit at a time. At each length the codes of
    /// that length occupy one contiguous run, so the accumulated bits identify a symbol
    /// as soon as they fall inside that run.
    /// </summary>
    public int DecodeSymbol(int[] countPerLength, int[] symbolsInCodeOrder) {
      int code = 0, firstCode = 0, firstIndex = 0;
      for (var length = 1; length <= RzipConstants.MaxCodeLength; ++length) {
        code |= this.ReadBit();
        var count = countPerLength[length];
        if (code - firstCode < count)
          return symbolsInCodeOrder[firstIndex + code - firstCode];

        firstIndex += count;
        firstCode = (firstCode + count) << 1;
        code <<= 1;
      }

      throw new InvalidDataException("Invalid Huffman code in the RZIP literal stream.");
    }
  }

  private static void WriteVarInt(List<byte> output, long value) {
    for (var remaining = value;;) {
      var chunk = (byte)(remaining & 0x7F);
      remaining >>= 7;
      if (remaining == 0) {
        output.Add(chunk);
        return;
      }

      output.Add((byte)(chunk | 0x80));
    }
  }

  private static long ReadVarInt(byte[] data, ref int position) {
    long result = 0;
    var shift = 0;
    for (;;) {
      var current = ReadByte(data, ref position);
      result |= (long)(current & 0x7F) << shift;
      if ((current & 0x80) == 0)
        return result;

      shift += 7;
      if (shift > 56)
        throw new InvalidDataException("Overlong varint in the RZIP stream.");
    }
  }

  private static byte ReadByte(byte[] data, ref int position) {
    if (position >= data.Length)
      throw new InvalidDataException("Unexpected end of the RZIP stream.");

    return data[position++];
  }

  private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> buffer)
    => (uint)(buffer[0] << 24 | buffer[1] << 16 | buffer[2] << 8 | buffer[3]);

  private static void WriteUInt32BigEndian(Span<byte> buffer, uint value) {
    buffer[0] = (byte)(value >> 24);
    buffer[1] = (byte)(value >> 16);
    buffer[2] = (byte)(value >> 8);
    buffer[3] = (byte)value;
  }
}
