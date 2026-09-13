using System.Buffers.Binary;
using System.Numerics;

namespace FileFormat.Lzfse;

/// <summary>
/// Writes Apple's entropy-coded LZFSE <c>bvx2</c> blocks: an LZ77 parse whose literal stream and
/// literal-length / match-length / distance triples are coded with the fixed-state FSE (tANS)
/// tables the format defines.
/// </summary>
/// <remarks>
/// <para>The bucket tables, state counts, header packing and frequency-table code are those of
/// Apple's published LZFSE reference implementation (BSD-3-Clause); the constants themselves live
/// once, next to the reader, in <see cref="LzfseFseDecoder"/>. The match parser here is
/// deliberately a plain hash-chain search rather than Apple's tuned one, with the chain depth
/// exposed as the compression level.</para>
/// <para>The encoder is fail-soft: <see cref="EncodeBlock"/> returns <see langword="null"/>
/// whenever the block cannot be represented — no matches were found, the parse exceeds the
/// per-block literal or match caps, or the entropy-coded form is not smaller than the input —
/// which lets the stream writer fall back to an LZVN or stored block.</para>
/// </remarks>
internal static class LzfseFseEncoder {

  /// <summary>Largest raw byte count one <c>bvx2</c> block carries in this writer.</summary>
  internal const int MaxBlockRawBytes = 30_000;

  private const int MaxLiteralValue = 315;
  private const int MaxMatchValue = 2359;
  private const int MinMatchLength = 3;
  private const int HashBits = 15;

  private readonly record struct EncoderEntry(short Threshold, byte BitCount, short Delta0, short Delta1);

  private readonly record struct Match(int Position, int Length, int Distance);

  /// <summary>
  /// Encodes one raw block as a complete <c>bvx2</c> block (magic, header, literal payload and
  /// LMD payload). Returns <see langword="null"/> when the block is not representable or not
  /// worth coding.
  /// </summary>
  internal static byte[]? EncodeBlock(ReadOnlySpan<byte> source, int searchDepth) {
    if (source.IsEmpty || source.Length > MaxBlockRawBytes)
      return null;

    var data = source.ToArray();
    var matches = FindMatches(data, searchDepth);
    if (matches.Count == 0)
      return null;

    var literals = new List<byte>(data.Length);
    var literalLengths = new List<int>();
    var matchLengths = new List<int>();
    var distances = new List<int>();
    var literalPosition = 0;

    foreach (var match in matches) {
      if (match.Position < literalPosition)
        continue;
      literals.AddRange(data.AsSpan(literalPosition, match.Position - literalPosition));
      AppendRecord(literalLengths, matchLengths, distances,
        match.Position - literalPosition, match.Length, match.Distance);
      literalPosition = match.Position + match.Length;
    }

    if (literalPosition < data.Length) {
      literals.AddRange(data.AsSpan(literalPosition));
      AppendRecord(literalLengths, matchLengths, distances, data.Length - literalPosition, 0, 1);
    }

    // The four interleaved literal FSE streams consume literals four at a time.
    while ((literals.Count & 3) != 0)
      literals.Add(0);

    if (literals.Count == 0 || literals.Count > LzfseFseDecoder.LiteralsPerBlock ||
        literalLengths.Count == 0 || literalLengths.Count > LzfseFseDecoder.MatchesPerBlock)
      return null;

    // A repeated distance is carried as the reserved value 0.
    var encodedDistances = new int[distances.Count];
    var previousDistance = 0;
    for (var i = 0; i < distances.Count; ++i) {
      if (distances[i] == previousDistance) {
        encodedDistances[i] = 0;
        continue;
      }
      encodedDistances[i] = distances[i];
      previousDistance = distances[i];
    }

    var lSymbols = new byte[literalLengths.Count];
    var mSymbols = new byte[matchLengths.Count];
    var dSymbols = new byte[encodedDistances.Length];
    var lCounts = new int[LzfseFseDecoder.LSymbols];
    var mCounts = new int[LzfseFseDecoder.MSymbols];
    var dCounts = new int[LzfseFseDecoder.DSymbols];
    var literalCounts = new int[LzfseFseDecoder.LiteralSymbols];
    foreach (var literal in literals)
      ++literalCounts[literal];

    for (var i = 0; i < literalLengths.Count; ++i) {
      lSymbols[i] = checked((byte)FindBucket(literalLengths[i], LzfseFseDecoder.LBaseValue));
      mSymbols[i] = checked((byte)FindBucket(matchLengths[i], LzfseFseDecoder.MBaseValue));
      dSymbols[i] = checked((byte)FindBucket(encodedDistances[i], LzfseFseDecoder.DBaseValue));
      ++lCounts[lSymbols[i]];
      ++mCounts[mSymbols[i]];
      ++dCounts[dSymbols[i]];
    }

    EnsureNonZero(lCounts);
    EnsureNonZero(mCounts);
    EnsureNonZero(dCounts);
    EnsureNonZero(literalCounts);

    var lFrequency = Normalize(lCounts, LzfseFseDecoder.LStates);
    var mFrequency = Normalize(mCounts, LzfseFseDecoder.MStates);
    var dFrequency = Normalize(dCounts, LzfseFseDecoder.DStates);
    var literalFrequency = Normalize(literalCounts, LzfseFseDecoder.LiteralStates);
    var lTable = BuildEncoderTable(lFrequency, LzfseFseDecoder.LStates);
    var mTable = BuildEncoderTable(mFrequency, LzfseFseDecoder.MStates);
    var dTable = BuildEncoderTable(dFrequency, LzfseFseDecoder.DStates);
    var literalTable = BuildEncoderTable(literalFrequency, LzfseFseDecoder.LiteralStates);

    // Both entropy streams are read backwards by the decoder, so they are written
    // from the last record to the first.
    var lmdOutput = new OutputBitStream();
    ushort lState = 0, mState = 0, dState = 0;
    for (var i = literalLengths.Count - 1; i >= 0; --i) {
      EncodeValue(ref dState, dTable, dSymbols[i], encodedDistances[i],
        LzfseFseDecoder.DBaseValue, LzfseFseDecoder.DExtraBits, ref lmdOutput);
      lmdOutput.Flush();
      EncodeValue(ref mState, mTable, mSymbols[i], matchLengths[i],
        LzfseFseDecoder.MBaseValue, LzfseFseDecoder.MExtraBits, ref lmdOutput);
      lmdOutput.Flush();
      EncodeValue(ref lState, lTable, lSymbols[i], literalLengths[i],
        LzfseFseDecoder.LBaseValue, LzfseFseDecoder.LExtraBits, ref lmdOutput);
      lmdOutput.Flush();
    }
    var lmdBits = lmdOutput.Finish();
    var encodedLmd = lmdOutput.ToArray();

    // The decoder's backward reader refills eight bytes at a time, so the LMD
    // payload starts with eight bytes of headroom it may read but never uses.
    var lmdPayload = new byte[8 + encodedLmd.Length];
    encodedLmd.CopyTo(lmdPayload, 8);

    var literalOutput = new OutputBitStream();
    var literalStates = new ushort[4];
    for (var i = literals.Count - 4; i >= 0; i -= 4)
      for (var channel = 3; channel >= 0; --channel) {
        EncodeSymbol(ref literalStates[channel], literalTable, literals[i + channel], ref literalOutput);
        literalOutput.Flush();
      }
    var literalBits = literalOutput.Finish();
    var literalPayload = literalOutput.ToArray();

    var frequencyBytes = EncodeFrequencyTables(lFrequency, mFrequency, dFrequency, literalFrequency);
    var headerBytes = checked(LzfseFseDecoder.V2FixedHeaderSize + frequencyBytes.Length);
    var total = checked(headerBytes + literalPayload.Length + lmdPayload.Length);
    if (total >= data.Length)
      return null;

    var result = new byte[total];
    BinaryPrimitives.WriteUInt32LittleEndian(result, LzfseFseDecoder.MagicV2);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)data.Length);

    var packed0 = (ulong)(uint)literals.Count
      | (ulong)(uint)literalPayload.Length << 20
      | (ulong)(uint)literalLengths.Count << 40
      | (ulong)(uint)(literalBits + 7) << 60;
    var packed1 = (ulong)literalStates[0]
      | (ulong)literalStates[1] << 10
      | (ulong)literalStates[2] << 20
      | (ulong)literalStates[3] << 30
      | (ulong)(uint)lmdPayload.Length << 40
      | (ulong)(uint)(lmdBits + 7) << 60;
    var packed2 = (ulong)(uint)headerBytes
      | (ulong)lState << 32
      | (ulong)mState << 42
      | (ulong)dState << 52;
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), packed0);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), packed1);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24), packed2);
    frequencyBytes.CopyTo(result, LzfseFseDecoder.V2FixedHeaderSize);
    literalPayload.CopyTo(result, headerBytes);
    lmdPayload.CopyTo(result, headerBytes + literalPayload.Length);

    return result;
  }

  /// <summary>Splits an L/M/D record that exceeds the largest representable bucket value.</summary>
  private static void AppendRecord(List<int> literalLengths, List<int> matchLengths, List<int> distances,
      int literalLength, int matchLength, int distance) {
    while (literalLength > MaxLiteralValue) {
      literalLengths.Add(MaxLiteralValue);
      matchLengths.Add(0);
      distances.Add(distance);
      literalLength -= MaxLiteralValue;
    }
    while (matchLength > MaxMatchValue) {
      literalLengths.Add(literalLength);
      matchLengths.Add(MaxMatchValue);
      distances.Add(distance);
      literalLength = 0;
      matchLength -= MaxMatchValue;
    }
    literalLengths.Add(literalLength);
    matchLengths.Add(matchLength);
    distances.Add(distance);
  }

  /// <summary>Greedy hash-chain match search; <paramref name="depth"/> candidates per position.</summary>
  private static List<Match> FindMatches(byte[] data, int depth) {
    const int hashSize = 1 << HashBits;
    var heads = new int[hashSize];
    var previous = new int[data.Length];
    Array.Fill(heads, -1);
    Array.Fill(previous, -1);
    var result = new List<Match>(data.Length / 8);

    var position = 0;
    while (position + 4 <= data.Length) {
      var hash = Hash(data, position);
      var candidate = heads[hash];
      previous[position] = candidate;
      heads[hash] = position;

      var bestLength = 0;
      var bestDistance = 0;
      for (var probe = 0; candidate >= 0 && probe < depth; ++probe) {
        var distance = position - candidate;
        if (distance > LzfseFseDecoder.MaxMatchDistance)
          break;
        if (distance > 0 && data[candidate] == data[position]) {
          var length = 0;
          var maximum = Math.Min(MaxMatchValue, data.Length - position);
          while (length < maximum && data[candidate + length] == data[position + length])
            ++length;
          if (length >= MinMatchLength &&
              (length > bestLength || (length == bestLength && distance < bestDistance))) {
            bestLength = length;
            bestDistance = distance;
          }
        }
        candidate = previous[candidate];
      }

      if (bestLength < MinMatchLength) {
        ++position;
        continue;
      }

      result.Add(new Match(position, bestLength, bestDistance));
      var end = Math.Min(position + bestLength, data.Length - 3);
      for (var p = position + 1; p < end; ++p) {
        var innerHash = Hash(data, p);
        previous[p] = heads[innerHash];
        heads[innerHash] = p;
      }
      position += bestLength;
    }

    return result;
  }

  private static int Hash(byte[] data, int position) {
    var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
    return (int)(unchecked(value * 2654435761u) >> (32 - HashBits));
  }

  /// <summary>Writes the bucket's extra bits, then the bucket symbol itself.</summary>
  private static void EncodeValue(ref ushort state, ReadOnlySpan<EncoderEntry> table, byte symbol,
      int value, ReadOnlySpan<int> baseValues, ReadOnlySpan<byte> extraBits,
      ref OutputBitStream output) {
    output.Push(extraBits[symbol], checked((ulong)(value - baseValues[symbol])));
    EncodeSymbol(ref state, table, symbol, ref output);
  }

  private static int FindBucket(int value, ReadOnlySpan<int> baseValues) {
    for (var symbol = baseValues.Length - 1; symbol >= 0; --symbol)
      if (value >= baseValues[symbol])
        return symbol;
    return 0;
  }

  /// <summary>An all-zero histogram has no representable table; give it one state.</summary>
  private static void EnsureNonZero(Span<int> counts) {
    foreach (var count in counts)
      if (count != 0)
        return;
    counts[0] = 1;
  }

  /// <summary>Scales a histogram onto exactly <paramref name="stateCount"/> states.</summary>
  private static ushort[] Normalize(ReadOnlySpan<int> counts, int stateCount) {
    long totalLong = 0;
    foreach (var count in counts)
      totalLong += count;
    if (totalLong is <= 0 or > uint.MaxValue)
      throw new InvalidOperationException("LZFSE FSE normalization requires a non-empty histogram.");

    var total = (uint)totalLong;
    var result = new ushort[counts.Length];
    var remaining = stateCount;
    var largestFrequency = 0;
    var largestSymbol = -1;
    var shift = BitOperations.LeadingZeroCount((uint)stateCount) - 1;
    var step = (1u << 31) / total;

    for (var symbol = 0; symbol < counts.Length; ++symbol) {
      if (counts[symbol] == 0)
        continue;

      var scaled = unchecked((uint)counts[symbol] * step);
      var frequency = Math.Max((int)(((scaled >> shift) + 1) >> 1), 1);
      result[symbol] = checked((ushort)frequency);
      remaining -= frequency;
      if (frequency > largestFrequency) {
        largestFrequency = frequency;
        largestSymbol = symbol;
      }
    }

    if (largestSymbol < 0)
      throw new InvalidOperationException("LZFSE FSE histogram unexpectedly contains no symbols.");

    if (-remaining < (largestFrequency >> 2)) {
      var adjusted = result[largestSymbol] + remaining;
      if (adjusted <= 0)
        throw new InvalidOperationException("LZFSE FSE normalization produced a non-positive frequency.");
      result[largestSymbol] = checked((ushort)adjusted);
    } else {
      while (remaining != 0) {
        var changed = false;
        for (var adjustmentShift = 3; adjustmentShift >= 0 && remaining != 0; --adjustmentShift)
          for (var symbol = 0; symbol < result.Length && remaining != 0; ++symbol) {
            if (result[symbol] <= 1)
              continue;
            var amount = (result[symbol] - 1) >> adjustmentShift;
            if (amount > -remaining)
              amount = -remaining;
            if (amount <= 0)
              continue;
            result[symbol] -= checked((ushort)amount);
            remaining += amount;
            changed = true;
          }

        if (!changed)
          throw new InvalidOperationException("LZFSE FSE normalization could not fit the state count.");
      }
    }

    var sum = 0;
    foreach (var frequency in result)
      sum += frequency;
    if (sum != stateCount)
      throw new InvalidOperationException($"LZFSE FSE normalized sum is {sum}, expected {stateCount}.");

    return result;
  }

  private static EncoderEntry[] BuildEncoderTable(ReadOnlySpan<ushort> frequencies, int stateCount) {
    var table = new EncoderEntry[frequencies.Length];
    var stateLeadingZeros = BitOperations.LeadingZeroCount((uint)stateCount);
    var offset = 0;

    for (var symbol = 0; symbol < frequencies.Length; ++symbol) {
      var frequency = frequencies[symbol];
      if (frequency == 0)
        continue;

      var k = BitOperations.LeadingZeroCount((uint)frequency) - stateLeadingZeros;
      table[symbol] = new EncoderEntry(
        checked((short)((frequency << k) - stateCount)),
        checked((byte)k),
        checked((short)(offset - frequency + (stateCount >> k))),
        checked((short)(k == 0 ? 0 : offset - frequency + (stateCount >> (k - 1)))));
      offset += frequency;
    }

    if (offset != stateCount)
      throw new InvalidOperationException($"LZFSE FSE encoder table spans {offset} states, expected {stateCount}.");

    return table;
  }

  private static void EncodeSymbol(ref ushort state, ReadOnlySpan<EncoderEntry> table, byte symbol,
      ref OutputBitStream output) {
    var entry = table[symbol];
    var current = state;
    var useFullWidth = current >= entry.Threshold;
    var bitCount = useFullWidth ? entry.BitCount : entry.BitCount - 1;
    var delta = useFullWidth ? entry.Delta0 : entry.Delta1;
    if (bitCount < 0)
      throw new InvalidOperationException("LZFSE FSE encoder selected a negative bit count.");

    var mask = bitCount == 0 ? 0u : (1u << bitCount) - 1;
    output.Push(bitCount, current & mask);
    state = checked((ushort)(delta + (current >> bitCount)));
  }

  private static byte[] EncodeFrequencyTables(ReadOnlySpan<ushort> lFrequency,
      ReadOnlySpan<ushort> mFrequency, ReadOnlySpan<ushort> dFrequency,
      ReadOnlySpan<ushort> literalFrequency) {
    var writer = new FrequencyBitWriter();
    EncodeFrequencyArray(ref writer, lFrequency);
    EncodeFrequencyArray(ref writer, mFrequency);
    EncodeFrequencyArray(ref writer, dFrequency);
    EncodeFrequencyArray(ref writer, literalFrequency);
    return writer.Finish();
  }

  private static void EncodeFrequencyArray(ref FrequencyBitWriter writer, ReadOnlySpan<ushort> values) {
    foreach (var value in values) {
      var (bits, code) = EncodeFrequency(value);
      writer.Write(bits, code);
    }
  }

  /// <summary>The bvx2 frequency prefix code, the exact inverse of the reader's lookup tables.</summary>
  private static (int Bits, ulong Code) EncodeFrequency(ushort value) => value switch {
    0 => (2, 0b00UL),
    1 => (2, 0b10UL),
    2 => (3, 0b001UL),
    3 => (3, 0b101UL),
    <= 7 => (5, ((ulong)(value - 4) << 3) | 0b011UL),
    <= 23 => (8, ((ulong)(value - 8) << 4) | 0b0111UL),
    <= 1047 => (14, ((ulong)(value - 24) << 4) | 0b1111UL),
    _ => throw new InvalidOperationException($"LZFSE frequency {value} is not encodable in a bvx2 header."),
  };

  /// <summary>Little-endian bit sink for the two backward-read entropy payloads.</summary>
  private struct OutputBitStream {
    private ulong _accumulator;
    private int _bitCount;
    private List<byte>? _bytes;

    internal void Push(int count, ulong value) {
      if (count <= 0)
        return;
      if (count > 56 || this._bitCount + count > 63)
        throw new InvalidOperationException("LZFSE FSE output accumulator overflow.");
      this._accumulator |= (value & ((1UL << count) - 1)) << this._bitCount;
      this._bitCount += count;
    }

    internal void Flush() {
      var bitsToFlush = this._bitCount & ~7;
      var bytesToFlush = bitsToFlush >> 3;
      if (bytesToFlush == 0)
        return;

      this._bytes ??= [];
      for (var i = 0; i < bytesToFlush; ++i) {
        this._bytes.Add((byte)this._accumulator);
        this._accumulator >>= 8;
      }
      this._bitCount -= bitsToFlush;
    }

    /// <summary>Writes the trailing partial byte and returns the decoder's initial bit count.</summary>
    internal int Finish() {
      if (this._bitCount == 0)
        return 0;
      this._bytes ??= [];
      this._bytes.Add((byte)this._accumulator);
      var result = this._bitCount - 8;
      this._accumulator = 0;
      this._bitCount = 0;
      return result;
    }

    internal readonly byte[] ToArray() => this._bytes?.ToArray() ?? [];
  }

  /// <summary>Little-endian bit sink for the forward-read frequency table.</summary>
  private struct FrequencyBitWriter {
    private ulong _accumulator;
    private int _bits;
    private List<byte>? _bytes;

    internal void Write(int count, ulong value) {
      this._accumulator |= value << this._bits;
      this._bits += count;
      this._bytes ??= [];
      while (this._bits >= 8) {
        this._bytes.Add((byte)this._accumulator);
        this._accumulator >>= 8;
        this._bits -= 8;
      }
    }

    internal byte[] Finish() {
      this._bytes ??= [];
      if (this._bits > 0)
        this._bytes.Add((byte)this._accumulator);
      return this._bytes.ToArray();
    }
  }
}
