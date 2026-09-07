using System.Buffers.Binary;

namespace FileFormat.Lzfse;

/// <summary>
/// Managed reader/writer for the entropy-coded LZFSE <c>bvx1</c>/<c>bvx2</c> blocks.
/// </summary>
/// <remarks>
/// Format fields, state counts and value buckets are defined by Apple's public
/// LZFSE reference implementation (BSD-3-Clause). The implementation here is
/// independent C# and deliberately keeps the match parser simpler than Apple's
/// tuned parser; the optimizer can trade parser depth against runtime.
/// </remarks>
internal static class LzfseCompressedBlock {
  internal const uint MagicV1 = 0x31787662;
  internal const uint MagicV2 = 0x32787662;
  internal const int V1HeaderSize = 772;
  internal const int V2MinimumHeaderSize = 32;
  internal const int MaximumDistance = 262139;
  internal const int MaximumRawBlockSize = 30_000;

  private const int LiteralSymbolCount = 256;
  private const int LiteralStateCount = 1024;
  private const int LSymbolCount = 20;
  private const int MSymbolCount = 20;
  private const int DSymbolCount = 64;
  private const int LStateCount = 64;
  private const int MStateCount = 64;
  private const int DStateCount = 256;
  private const int MaximumLiteralValue = 315;
  private const int MaximumMatchValue = 2359;
  private const int MaximumMatchesPerBlock = 10_000;
  private const int MaximumLiteralsPerBlock = 40_000;

  private static readonly byte[] LExtraBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 5, 8];
  private static readonly int[] LBaseValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 20, 28, 60];
  private static readonly byte[] MExtraBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 5, 8, 11];
  private static readonly int[] MBaseValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 24, 56, 312];
  private static readonly byte[] DExtraBits = [
    0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
    4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7,
    8, 8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11,
    12, 12, 12, 12, 13, 13, 13, 13, 14, 14, 14, 14, 15, 15, 15, 15,
  ];
  private static readonly int[] DBaseValues = [
    0, 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 28, 36, 44, 52,
    60, 76, 92, 108, 124, 156, 188, 220, 252, 316, 380, 444, 508, 636, 764, 892,
    1020, 1276, 1532, 1788, 2044, 2556, 3068, 3580, 4092, 5116, 6140, 7164,
    8188, 10236, 12284, 14332, 16380, 20476, 24572, 28668, 32764, 40956, 49148,
    57340, 65532, 81916, 98300, 114684, 131068, 163836, 196604, 229372,
  ];

  private static readonly byte[] FrequencyBitCounts = [
    2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14,
    2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14,
  ];
  private static readonly sbyte[] FrequencyValues = [
    0, 2, 1, 4, 0, 3, 1, -1, 0, 2, 1, 5, 0, 3, 1, -1,
    0, 2, 1, 6, 0, 3, 1, -1, 0, 2, 1, 7, 0, 3, 1, -1,
  ];

  internal enum ParserStrength {
    Fast = 1,
    Balanced = 4,
    Maximum = 8,
  }

  internal sealed class Header {
    internal uint RawBytes;
    internal uint LiteralCount;
    internal uint MatchCount;
    internal uint LiteralPayloadBytes;
    internal uint LmdPayloadBytes;
    internal int LiteralBits;
    internal ushort[] LiteralStates = new ushort[4];
    internal int LmdBits;
    internal ushort LState;
    internal ushort MState;
    internal ushort DState;
    internal ushort[] LFrequency = new ushort[LSymbolCount];
    internal ushort[] MFrequency = new ushort[MSymbolCount];
    internal ushort[] DFrequency = new ushort[DSymbolCount];
    internal ushort[] LiteralFrequency = new ushort[LiteralSymbolCount];
    internal int HeaderBytes;

    internal int PayloadBytes => checked((int)(this.LiteralPayloadBytes + this.LmdPayloadBytes));
  }

  internal sealed class History {
    private readonly byte[] _bytes = new byte[MaximumDistance];
    private int _next;
    private int _count;

    internal void Append(byte value) {
      this._bytes[this._next] = value;
      this._next = (this._next + 1) % this._bytes.Length;
      if (this._count < this._bytes.Length)
        ++this._count;
    }

    internal byte GetByDistance(int distance) {
      if (distance <= 0 || distance > this._count)
        throw new InvalidDataException($"LZFSE match distance {distance} exceeds the available {this._count}-byte history.");
      var index = this._next - distance;
      if (index < 0)
        index += this._bytes.Length;
      return this._bytes[index];
    }
  }

  private readonly record struct Match(int Position, int Length, int Distance);

  internal static Header ReadV1Header(ReadOnlySpan<byte> bytes) {
    if (bytes.Length < V1HeaderSize)
      throw new InvalidDataException("LZFSE bvx1 header is truncated.");
    if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != MagicV1)
      throw new InvalidDataException("LZFSE bvx1 header has the wrong magic.");

    var header = new Header {
      RawBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
      LiteralCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
      MatchCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
      LiteralPayloadBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]),
      LmdPayloadBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]),
      LiteralBits = BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]),
      LmdBits = BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]),
      LState = BinaryPrimitives.ReadUInt16LittleEndian(bytes[44..]),
      MState = BinaryPrimitives.ReadUInt16LittleEndian(bytes[46..]),
      DState = BinaryPrimitives.ReadUInt16LittleEndian(bytes[48..]),
      HeaderBytes = V1HeaderSize,
    };
    for (var i = 0; i < 4; ++i)
      header.LiteralStates[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(32 + i * 2)..]);

    var offset = 52;
    ReadFrequencyArray(bytes, ref offset, header.LFrequency);
    ReadFrequencyArray(bytes, ref offset, header.MFrequency);
    ReadFrequencyArray(bytes, ref offset, header.DFrequency);
    ReadFrequencyArray(bytes, ref offset, header.LiteralFrequency);
    ValidateHeader(header);
    return header;
  }

  internal static Header ReadV2Header(ReadOnlySpan<byte> bytes) {
    if (bytes.Length < V2MinimumHeaderSize)
      throw new InvalidDataException("LZFSE bvx2 header is truncated.");
    if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != MagicV2)
      throw new InvalidDataException("LZFSE bvx2 header has the wrong magic.");

    var packed0 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    var packed1 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
    var packed2 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
    var headerBytes = checked((int)(uint)packed2);
    if (headerBytes < V2MinimumHeaderSize || headerBytes > bytes.Length)
      throw new InvalidDataException($"LZFSE bvx2 header size {headerBytes} is invalid.");

    var header = new Header {
      RawBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
      LiteralCount = (uint)(packed0 & 0xFFFFF),
      LiteralPayloadBytes = (uint)((packed0 >> 20) & 0xFFFFF),
      MatchCount = (uint)((packed0 >> 40) & 0xFFFFF),
      LiteralBits = (int)((packed0 >> 60) & 7) - 7,
      LmdPayloadBytes = (uint)((packed1 >> 40) & 0xFFFFF),
      LmdBits = (int)((packed1 >> 60) & 7) - 7,
      LState = (ushort)((packed2 >> 32) & 0x3FF),
      MState = (ushort)((packed2 >> 42) & 0x3FF),
      DState = (ushort)((packed2 >> 52) & 0x3FF),
      HeaderBytes = headerBytes,
    };
    for (var i = 0; i < 4; ++i)
      header.LiteralStates[i] = (ushort)((packed1 >> (i * 10)) & 0x3FF);

    DecodeFrequencyTables(
      bytes[V2MinimumHeaderSize..headerBytes],
      header.LFrequency,
      header.MFrequency,
      header.DFrequency,
      header.LiteralFrequency);
    ValidateHeader(header);
    return header;
  }

  internal static int ReadV2HeaderSize(ReadOnlySpan<byte> fixedHeader) {
    if (fixedHeader.Length < V2MinimumHeaderSize)
      throw new InvalidDataException("LZFSE bvx2 fixed header is truncated.");
    return checked((int)(uint)BinaryPrimitives.ReadUInt64LittleEndian(fixedHeader[24..]));
  }

  internal static void Decode(Header header, ReadOnlySpan<byte> payload, Stream output, History history) {
    ArgumentNullException.ThrowIfNull(header);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(history);
    if (payload.Length < header.PayloadBytes)
      throw new InvalidDataException("LZFSE compressed block payload is truncated.");

    var literalPayloadLength = checked((int)header.LiteralPayloadBytes);
    var literalPayload = payload[..literalPayloadLength];
    var lmdPayload = payload.Slice(literalPayloadLength, checked((int)header.LmdPayloadBytes));

    var literalTable = LzfseFse.BuildDecoderTable(header.LiteralFrequency, LiteralStateCount);
    var lTable = LzfseFse.BuildValueDecoderTable(header.LFrequency, LStateCount, LExtraBits, LBaseValues);
    var mTable = LzfseFse.BuildValueDecoderTable(header.MFrequency, MStateCount, MExtraBits, MBaseValues);
    var dTable = LzfseFse.BuildValueDecoderTable(header.DFrequency, DStateCount, DExtraBits, DBaseValues);

    var literalCount = checked((int)header.LiteralCount);
    var literals = new byte[literalCount];
    var literalInput = new LzfseFse.InputBitStream(literalPayload, header.LiteralBits);
    var literalStates = (ushort[])header.LiteralStates.Clone();
    for (var i = 0; i < literals.Length; i += 4) {
      literalInput.Flush();
      literals[i] = LzfseFse.Decode(ref literalStates[0], literalTable, ref literalInput);
      literals[i + 1] = LzfseFse.Decode(ref literalStates[1], literalTable, ref literalInput);
      literals[i + 2] = LzfseFse.Decode(ref literalStates[2], literalTable, ref literalInput);
      literals[i + 3] = LzfseFse.Decode(ref literalStates[3], literalTable, ref literalInput);
    }

    var lmdInput = new LzfseFse.InputBitStream(lmdPayload, header.LmdBits);
    var lState = header.LState;
    var mState = header.MState;
    var dState = header.DState;
    var literalIndex = 0;
    var distance = 0;
    var produced = 0;

    for (var i = 0; i < header.MatchCount; ++i) {
      lmdInput.Flush();
      var literalLength = LzfseFse.DecodeValue(ref lState, lTable, ref lmdInput);
      var matchLength = LzfseFse.DecodeValue(ref mState, mTable, ref lmdInput);
      lmdInput.Flush();
      var newDistance = LzfseFse.DecodeValue(ref dState, dTable, ref lmdInput);
      if (newDistance != 0)
        distance = newDistance;

      if (literalLength < 0 || matchLength < 0 || literalIndex + literalLength > literals.Length)
        throw new InvalidDataException("LZFSE L/M value exceeds the literal or output bounds.");

      for (var j = 0; j < literalLength; ++j) {
        var value = literals[literalIndex++];
        output.WriteByte(value);
        history.Append(value);
      }
      produced = checked(produced + literalLength);

      for (var j = 0; j < matchLength; ++j) {
        var value = history.GetByDistance(distance);
        output.WriteByte(value);
        history.Append(value);
      }
      produced = checked(produced + matchLength);
    }

    if (produced != header.RawBytes)
      throw new InvalidDataException($"LZFSE block produced {produced} bytes, header declares {header.RawBytes}.");
  }

  internal static byte[]? EncodeV2(ReadOnlySpan<byte> source, ParserStrength strength) {
    if (source.Length == 0)
      return null;
    if (source.Length > MaximumRawBlockSize)
      throw new ArgumentOutOfRangeException(nameof(source), $"LZFSE compressed blocks are capped at {MaximumRawBlockSize} raw bytes.");

    var data = source.ToArray();
    var matches = FindMatches(data, (int)strength);
    if (matches.Count == 0)
      return null;

    var literals = new List<byte>(source.Length);
    var lValues = new List<int>();
    var mValues = new List<int>();
    var dRaw = new List<int>();
    var literalPosition = 0;

    foreach (var match in matches) {
      if (match.Position < literalPosition)
        continue;
      literals.AddRange(data.AsSpan(literalPosition, match.Position - literalPosition).ToArray());
      AppendRecord(lValues, mValues, dRaw, match.Position - literalPosition, match.Length, match.Distance);
      literalPosition = match.Position + match.Length;
    }

    if (literalPosition < data.Length) {
      literals.AddRange(data.AsSpan(literalPosition).ToArray());
      AppendRecord(lValues, mValues, dRaw, data.Length - literalPosition, 0, 1);
    }

    while ((literals.Count & 3) != 0)
      literals.Add(0);

    if (literals.Count == 0 || literals.Count > MaximumLiteralsPerBlock || lValues.Count == 0 || lValues.Count > MaximumMatchesPerBlock)
      return null;

    var dValues = new int[dRaw.Count];
    var previousDistance = 0;
    for (var i = 0; i < dRaw.Count; ++i) {
      var current = dRaw[i];
      if (current == previousDistance)
        dValues[i] = 0;
      else {
        dValues[i] = current;
        previousDistance = current;
      }
    }

    var lSymbols = new byte[lValues.Count];
    var mSymbols = new byte[mValues.Count];
    var dSymbols = new byte[dValues.Length];
    var lCounts = new int[LSymbolCount];
    var mCounts = new int[MSymbolCount];
    var dCounts = new int[DSymbolCount];
    var literalCounts = new int[LiteralSymbolCount];
    foreach (var literal in literals)
      ++literalCounts[literal];

    for (var i = 0; i < lValues.Count; ++i) {
      lSymbols[i] = checked((byte)FindValueSymbol(lValues[i], LBaseValues));
      mSymbols[i] = checked((byte)FindValueSymbol(mValues[i], MBaseValues));
      dSymbols[i] = checked((byte)FindValueSymbol(dValues[i], DBaseValues));
      ++lCounts[lSymbols[i]];
      ++mCounts[mSymbols[i]];
      ++dCounts[dSymbols[i]];
    }

    EnsureNonZero(lCounts);
    EnsureNonZero(mCounts);
    EnsureNonZero(dCounts);
    EnsureNonZero(literalCounts);

    var lFrequency = LzfseFse.Normalize(lCounts, LStateCount);
    var mFrequency = LzfseFse.Normalize(mCounts, MStateCount);
    var dFrequency = LzfseFse.Normalize(dCounts, DStateCount);
    var literalFrequency = LzfseFse.Normalize(literalCounts, LiteralStateCount);
    var lEncoder = LzfseFse.BuildEncoderTable(lFrequency, LStateCount);
    var mEncoder = LzfseFse.BuildEncoderTable(mFrequency, MStateCount);
    var dEncoder = LzfseFse.BuildEncoderTable(dFrequency, DStateCount);
    var literalEncoder = LzfseFse.BuildEncoderTable(literalFrequency, LiteralStateCount);

    var lmdOutput = new LzfseFse.OutputBitStream();
    ushort lState = 0, mState = 0, dState = 0;
    for (var i = lValues.Count - 1; i >= 0; --i) {
      EncodeValue(ref dState, dEncoder, dSymbols[i], dValues[i], DBaseValues, DExtraBits, ref lmdOutput);
      lmdOutput.Flush();
      EncodeValue(ref mState, mEncoder, mSymbols[i], mValues[i], MBaseValues, MExtraBits, ref lmdOutput);
      lmdOutput.Flush();
      EncodeValue(ref lState, lEncoder, lSymbols[i], lValues[i], LBaseValues, LExtraBits, ref lmdOutput);
      lmdOutput.Flush();
    }
    var lmdBits = lmdOutput.Finish();
    var encodedLmd = lmdOutput.ToArray();
    var lmdPayload = new byte[8 + encodedLmd.Length];
    encodedLmd.CopyTo(lmdPayload, 8);

    var literalOutput = new LzfseFse.OutputBitStream();
    var literalStates = new ushort[4];
    for (var i = literals.Count - 4; i >= 0; i -= 4)
      for (var channel = 3; channel >= 0; --channel) {
        LzfseFse.Encode(ref literalStates[channel], literalEncoder, literals[i + channel], ref literalOutput);
        literalOutput.Flush();
      }
    var literalBits = literalOutput.Finish();
    var literalPayload = literalOutput.ToArray();

    var header = new Header {
      RawBytes = checked((uint)data.Length),
      LiteralCount = checked((uint)literals.Count),
      MatchCount = checked((uint)lValues.Count),
      LiteralPayloadBytes = checked((uint)literalPayload.Length),
      LmdPayloadBytes = checked((uint)lmdPayload.Length),
      LiteralBits = literalBits,
      LiteralStates = literalStates,
      LmdBits = lmdBits,
      LState = lState,
      MState = mState,
      DState = dState,
      LFrequency = lFrequency,
      MFrequency = mFrequency,
      DFrequency = dFrequency,
      LiteralFrequency = literalFrequency,
    };

    var frequencyBytes = EncodeFrequencyTables(header);
    var headerBytes = checked(V2MinimumHeaderSize + frequencyBytes.Length);
    header.HeaderBytes = headerBytes;
    var result = new byte[checked(headerBytes + literalPayload.Length + lmdPayload.Length)];
    BinaryPrimitives.WriteUInt32LittleEndian(result, MagicV2);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), header.RawBytes);

    var packed0 = (ulong)header.LiteralCount
      | (ulong)header.LiteralPayloadBytes << 20
      | (ulong)header.MatchCount << 40
      | (ulong)(header.LiteralBits + 7) << 60;
    var packed1 = (ulong)header.LiteralStates[0]
      | (ulong)header.LiteralStates[1] << 10
      | (ulong)header.LiteralStates[2] << 20
      | (ulong)header.LiteralStates[3] << 30
      | (ulong)header.LmdPayloadBytes << 40
      | (ulong)(header.LmdBits + 7) << 60;
    var packed2 = (ulong)(uint)headerBytes
      | (ulong)header.LState << 32
      | (ulong)header.MState << 42
      | (ulong)header.DState << 52;
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), packed0);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), packed1);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24), packed2);
    frequencyBytes.CopyTo(result, V2MinimumHeaderSize);
    literalPayload.CopyTo(result, headerBytes);
    lmdPayload.CopyTo(result, headerBytes + literalPayload.Length);

    return result;
  }

  private static void AppendRecord(List<int> l, List<int> m, List<int> d, int literalLength, int matchLength, int distance) {
    while (literalLength > MaximumLiteralValue) {
      l.Add(MaximumLiteralValue);
      m.Add(0);
      d.Add(distance);
      literalLength -= MaximumLiteralValue;
    }
    while (matchLength > MaximumMatchValue) {
      l.Add(literalLength);
      m.Add(MaximumMatchValue);
      d.Add(distance);
      literalLength = 0;
      matchLength -= MaximumMatchValue;
    }
    l.Add(literalLength);
    m.Add(matchLength);
    d.Add(distance);
  }

  private static List<Match> FindMatches(byte[] data, int depth) {
    const int hashBits = 15;
    const int hashSize = 1 << hashBits;
    var heads = new int[hashSize];
    var previous = new int[data.Length];
    Array.Fill(heads, -1);
    Array.Fill(previous, -1);
    var result = new List<Match>(data.Length / 8);

    var position = 0;
    while (position + 4 <= data.Length) {
      var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
      var hash = (int)(unchecked(value * 2654435761u) >> (32 - hashBits));
      var candidate = heads[hash];
      previous[position] = candidate;
      heads[hash] = position;

      var bestLength = 0;
      var bestDistance = 0;
      for (var probe = 0; candidate >= 0 && probe < depth; ++probe) {
        var distance = position - candidate;
        if (distance > MaximumDistance)
          break;
        if (distance > 0 && data[candidate] == data[position]) {
          var length = 0;
          var maximum = Math.Min(MaximumMatchValue, data.Length - position);
          while (length < maximum && data[candidate + length] == data[position + length])
            ++length;
          if (length >= 3 && (length > bestLength || length == bestLength && distance < bestDistance)) {
            bestLength = length;
            bestDistance = distance;
          }
        }
        candidate = previous[candidate];
      }

      if (bestLength < 3) {
        ++position;
        continue;
      }

      result.Add(new(position, bestLength, bestDistance));
      var end = Math.Min(position + bestLength, data.Length - 3);
      for (var p = position + 1; p < end; ++p) {
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
        hash = (int)(unchecked(value * 2654435761u) >> (32 - hashBits));
        previous[p] = heads[hash];
        heads[hash] = p;
      }
      position += bestLength;
    }

    return result;
  }

  private static void EncodeValue(
      ref ushort state,
      ReadOnlySpan<LzfseFse.EncoderEntry> table,
      byte symbol,
      int value,
      ReadOnlySpan<int> baseValues,
      ReadOnlySpan<byte> extraBits,
      ref LzfseFse.OutputBitStream output) {
    var extra = extraBits[symbol];
    var residual = checked(value - baseValues[symbol]);
    output.Push(extra, checked((ulong)residual));
    LzfseFse.Encode(ref state, table, symbol, ref output);
  }

  private static int FindValueSymbol(int value, ReadOnlySpan<int> baseValues) {
    for (var symbol = baseValues.Length - 1; symbol >= 0; --symbol)
      if (value >= baseValues[symbol])
        return symbol;
    return 0;
  }

  private static void EnsureNonZero(Span<int> counts) {
    foreach (var count in counts)
      if (count != 0)
        return;
    counts[0] = 1;
  }

  private static void ReadFrequencyArray(ReadOnlySpan<byte> source, ref int offset, Span<ushort> destination) {
    foreach (ref var value in destination) {
      value = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
      offset += 2;
    }
  }

  private static void ValidateHeader(Header header) {
    if (header.LiteralCount > MaximumLiteralsPerBlock || (header.LiteralCount & 3) != 0)
      throw new InvalidDataException($"LZFSE literal count {header.LiteralCount} is invalid.");
    if (header.MatchCount > MaximumMatchesPerBlock)
      throw new InvalidDataException($"LZFSE match count {header.MatchCount} exceeds the format limit.");
    if (header.LiteralBits is < -7 or > 0 || header.LmdBits is < -7 or > 0)
      throw new InvalidDataException("LZFSE FSE initial bit counts are invalid.");
    foreach (var state in header.LiteralStates)
      if (state >= LiteralStateCount)
        throw new InvalidDataException("LZFSE literal FSE state is out of range.");
    if (header.LState >= LStateCount || header.MState >= MStateCount || header.DState >= DStateCount)
      throw new InvalidDataException("LZFSE L/M/D FSE state is out of range.");
    ValidateFrequencySum(header.LFrequency, LStateCount, "L");
    ValidateFrequencySum(header.MFrequency, MStateCount, "M");
    ValidateFrequencySum(header.DFrequency, DStateCount, "D");
    ValidateFrequencySum(header.LiteralFrequency, LiteralStateCount, "literal");
  }

  private static void ValidateFrequencySum(ReadOnlySpan<ushort> frequencies, int expected, string name) {
    var sum = 0;
    foreach (var frequency in frequencies)
      sum += frequency;
    if (sum != expected)
      throw new InvalidDataException($"LZFSE {name} FSE frequencies sum to {sum}, expected {expected}.");
  }

  private static void DecodeFrequencyTables(
      ReadOnlySpan<byte> source,
      Span<ushort> l,
      Span<ushort> m,
      Span<ushort> d,
      Span<ushort> literal) {
    var reader = new FrequencyBitReader(source);
    DecodeFrequencyArray(ref reader, l);
    DecodeFrequencyArray(ref reader, m);
    DecodeFrequencyArray(ref reader, d);
    DecodeFrequencyArray(ref reader, literal);
    if (!reader.IsAtEnd)
      throw new InvalidDataException("LZFSE bvx2 frequency table does not end at the header boundary.");
  }

  private static void DecodeFrequencyArray(ref FrequencyBitReader reader, Span<ushort> destination) {
    foreach (ref var value in destination) {
      var lowFive = (int)reader.Peek(5);
      var bitCount = FrequencyBitCounts[lowFive];
      var code = reader.Read(bitCount);
      value = bitCount switch {
        8 => checked((ushort)(8 + ((code >> 4) & 0xF))),
        14 => checked((ushort)(24 + ((code >> 4) & 0x3FF))),
        _ => checked((ushort)FrequencyValues[lowFive]),
      };
    }
  }

  private static byte[] EncodeFrequencyTables(Header header) {
    var writer = new FrequencyBitWriter();
    EncodeFrequencyArray(ref writer, header.LFrequency);
    EncodeFrequencyArray(ref writer, header.MFrequency);
    EncodeFrequencyArray(ref writer, header.DFrequency);
    EncodeFrequencyArray(ref writer, header.LiteralFrequency);
    return writer.Finish();
  }

  private static void EncodeFrequencyArray(ref FrequencyBitWriter writer, ReadOnlySpan<ushort> values) {
    foreach (var value in values) {
      var (bits, code) = EncodeFrequency(value);
      writer.Write(bits, code);
    }
  }

  private static (int Bits, ulong Code) EncodeFrequency(ushort value) => value switch {
    0 => (2, 0b00),
    1 => (2, 0b10),
    2 => (3, 0b001),
    3 => (3, 0b101),
    <= 7 => (5, (ulong)(value - 4) << 3 | 0b011UL),
    <= 23 => (8, (ulong)(value - 8) << 4 | 0b0111UL),
    <= 1047 => (14, (ulong)(value - 24) << 4 | 0b1111UL),
    _ => throw new InvalidOperationException($"LZFSE frequency {value} is not encodable in a bvx2 header."),
  };

  private ref struct FrequencyBitReader(ReadOnlySpan<byte> source) {
    private readonly ReadOnlySpan<byte> _source = source;
    private int _byteIndex;
    private ulong _accumulator;
    private int _bits;

    internal bool IsAtEnd {
      get {
        this.Refill(8);
        return this._byteIndex == this._source.Length && this._bits < 8 && this._accumulator == 0;
      }
    }

    internal ulong Peek(int count) {
      this.Refill(count);
      if (this._bits < count)
        throw new InvalidDataException("LZFSE bvx2 frequency table is truncated.");
      return this._accumulator & ((1UL << count) - 1);
    }

    internal ulong Read(int count) {
      var value = this.Peek(count);
      this._accumulator >>= count;
      this._bits -= count;
      return value;
    }

    private void Refill(int minimumBits) {
      while (this._bits < minimumBits && this._byteIndex < this._source.Length) {
        this._accumulator |= (ulong)this._source[this._byteIndex++] << this._bits;
        this._bits += 8;
      }
    }
  }

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
