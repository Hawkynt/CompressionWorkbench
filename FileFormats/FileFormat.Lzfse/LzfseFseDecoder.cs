using System.Buffers.Binary;
using System.Numerics;

namespace FileFormat.Lzfse;

/*
 * The bvx1/bvx2 decoder below is a managed C# adaptation of the decoder data
 * model and FSE state transitions from Apple's reference LZFSE implementation:
 * https://github.com/lzfse/lzfse
 *
 * Copyright (c) 2015-2016, Apple Inc. All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *
 * 1. Redistributions of source code must retain the above copyright notice,
 *    this list of conditions and the following disclaimer.
 * 2. Redistributions in binary form must reproduce the above copyright notice,
 *    this list of conditions and the following disclaimer in the documentation
 *    and/or other materials provided with the distribution.
 * 3. Neither the name of the copyright holder(s) nor the names of any
 *    contributors may be used to endorse or promote products derived from this
 *    software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 */

/// <summary>
/// Decoder for the entropy-coded <c>bvx1</c>/<c>bvx2</c> blocks in Apple's
/// LZFSE stream format. The public stream framing remains in
/// <see cref="LzfseStream"/>; this type owns only the FSE block grammar.
/// </summary>
internal static class LzfseFseDecoder {
  private const uint MagicV1 = 0x31787662;
  private const uint MagicV2 = 0x32787662;

  private const int LSymbols = 20;
  private const int MSymbols = 20;
  private const int DSymbols = 64;
  private const int LiteralSymbols = 256;
  private const int LStates = 64;
  private const int MStates = 64;
  private const int DStates = 256;
  private const int LiteralStates = 1024;
  private const int MatchesPerBlock = 10_000;
  private const int LiteralsPerBlock = 40_000;
  private const int V1HeaderSize = 772;
  private const int V2FixedHeaderSize = 32;
  private const int V2MaxHeaderSize = V2FixedHeaderSize + 2 * (LSymbols + MSymbols + DSymbols + LiteralSymbols);
  private const int MaxRawBytes = LiteralsPerBlock + MatchesPerBlock * 2359;

  /// <summary>Largest distance representable by Apple's 64-symbol D alphabet.</summary>
  internal const int MaxMatchDistance = 262_139;

  private static ReadOnlySpan<byte> LExtraBits => [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 5, 8,
  ];

  private static ReadOnlySpan<int> LBaseValue => [
    0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 20, 28, 60,
  ];

  private static ReadOnlySpan<byte> MExtraBits => [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 5, 8, 11,
  ];

  private static ReadOnlySpan<int> MBaseValue => [
    0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 24, 56, 312,
  ];

  private static ReadOnlySpan<byte> DExtraBits => [
    0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3,
    4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7,
    8, 8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11,
    12, 12, 12, 12, 13, 13, 13, 13, 14, 14, 14, 14, 15, 15, 15, 15,
  ];

  private static ReadOnlySpan<int> DBaseValue => [
    0, 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 28, 36, 44, 52,
    60, 76, 92, 108, 124, 156, 188, 220, 252, 316, 380, 444, 508, 636, 764, 892,
    1020, 1276, 1532, 1788, 2044, 2556, 3068, 3580, 4092, 5116, 6140, 7164,
    8188, 10236, 12284, 14332, 16380, 20476, 24572, 28668, 32764, 40956, 49148,
    57340, 65532, 81916, 98300, 114684, 131068, 163836, 196604, 229372,
  ];

  private static ReadOnlySpan<byte> FrequencyBitCounts => [
    2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14,
    2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14,
  ];

  private static ReadOnlySpan<sbyte> FrequencyValues => [
    0, 2, 1, 4, 0, 3, 1, -1, 0, 2, 1, 5, 0, 3, 1, -1,
    0, 2, 1, 6, 0, 3, 1, -1, 0, 2, 1, 7, 0, 3, 1, -1,
  ];

  /// <summary>Decodes one entropy-coded block after its four-byte magic was consumed.</summary>
  public static byte[] DecodeBlock(Stream input, uint magic, ReadOnlySpan<byte> history) {
    ArgumentNullException.ThrowIfNull(input);
    if (history.Length > MaxMatchDistance)
      throw new ArgumentException("LZFSE history exceeds the maximum representable match distance.", nameof(history));

    var (header, serializedHeader) = magic switch {
      MagicV1 => ReadV1Header(input),
      MagicV2 => ReadV2Header(input),
      _ => throw new ArgumentOutOfRangeException(nameof(magic)),
    };

    ValidateHeader(header);

    var literalPayload = new byte[header.LiteralPayloadBytes];
    input.ReadExactly(literalPayload);
    var lmdPayload = new byte[header.LmdPayloadBytes];
    input.ReadExactly(lmdPayload);

    return Decode(header, serializedHeader, literalPayload, lmdPayload, history);
  }

  private static (Header Header, byte[] Serialized) ReadV1Header(Stream input) {
    var bytes = new byte[V1HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, MagicV1);
    input.ReadExactly(bytes.AsSpan(4));

    var span = bytes.AsSpan();
    var rawBytes = ReadBoundedUInt32(span, 4, MaxRawBytes, "raw byte count");
    var payloadBytes = ReadBoundedUInt32(span, 8, int.MaxValue, "payload byte count");
    var literals = ReadBoundedUInt32(span, 12, LiteralsPerBlock, "literal count");
    var matches = ReadBoundedUInt32(span, 16, MatchesPerBlock, "match count");
    var literalPayloadBytes = ReadBoundedUInt32(span, 20, int.MaxValue, "literal payload length");
    var lmdPayloadBytes = ReadBoundedUInt32(span, 24, int.MaxValue, "LMD payload length");
    var literalBits = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);
    ushort[] literalState = [
      BinaryPrimitives.ReadUInt16LittleEndian(span[32..]),
      BinaryPrimitives.ReadUInt16LittleEndian(span[34..]),
      BinaryPrimitives.ReadUInt16LittleEndian(span[36..]),
      BinaryPrimitives.ReadUInt16LittleEndian(span[38..]),
    ];
    var lmdBits = BinaryPrimitives.ReadInt32LittleEndian(span[40..]);
    var lState = BinaryPrimitives.ReadUInt16LittleEndian(span[44..]);
    var mState = BinaryPrimitives.ReadUInt16LittleEndian(span[46..]);
    var dState = BinaryPrimitives.ReadUInt16LittleEndian(span[48..]);

    // Native C layout places l_freq at byte 50 and rounds sizeof(header) up
    // from 770 to 772; the two alignment bytes are therefore at the tail.
    var offset = 50;
    var lFreq = ReadFrequencyArray(span, ref offset, LSymbols);
    var mFreq = ReadFrequencyArray(span, ref offset, MSymbols);
    var dFreq = ReadFrequencyArray(span, ref offset, DSymbols);
    var literalFreq = ReadFrequencyArray(span, ref offset, LiteralSymbols);
    if (offset != 770)
      throw new InvalidDataException("LZFSE V1 header layout is inconsistent.");

    if (payloadBytes != checked(literalPayloadBytes + lmdPayloadBytes))
      throw new InvalidDataException("LZFSE V1 payload length does not equal literal + LMD payload lengths.");

    return (new Header(rawBytes, literals, matches, literalPayloadBytes, lmdPayloadBytes,
      literalBits, literalState, lmdBits, lState, mState, dState,
      lFreq, mFreq, dFreq, literalFreq), bytes);
  }

  private static (Header Header, byte[] Serialized) ReadV2Header(Stream input) {
    var fixedHeader = new byte[V2FixedHeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(fixedHeader, MagicV2);
    input.ReadExactly(fixedHeader.AsSpan(4));

    var rawBytes = ReadBoundedUInt32(fixedHeader, 4, MaxRawBytes, "raw byte count");
    var v0 = BinaryPrimitives.ReadUInt64LittleEndian(fixedHeader.AsSpan(8));
    var v1 = BinaryPrimitives.ReadUInt64LittleEndian(fixedHeader.AsSpan(16));
    var v2 = BinaryPrimitives.ReadUInt64LittleEndian(fixedHeader.AsSpan(24));

    // Masked rather than cast: a checked `(uint)` of the whole word throws on the state bits above it.
    var headerSize = checked((int)(v2 & 0xFFFFFFFF));
    if (headerSize is < V2FixedHeaderSize or > V2MaxHeaderSize)
      throw new InvalidDataException($"LZFSE V2 header size {headerSize} is outside the valid range.");

    var serialized = new byte[headerSize];
    fixedHeader.CopyTo(serialized, 0);
    if (headerSize > V2FixedHeaderSize)
      input.ReadExactly(serialized.AsSpan(V2FixedHeaderSize));

    var literals = checked((int)(v0 & 0xFFFFF));
    var literalPayloadBytes = checked((int)((v0 >> 20) & 0xFFFFF));
    var matches = checked((int)((v0 >> 40) & 0xFFFFF));
    var literalBits = checked((int)((v0 >> 60) & 0x7)) - 7;

    ushort[] literalState = [
      checked((ushort)(v1 & 0x3FF)),
      checked((ushort)((v1 >> 10) & 0x3FF)),
      checked((ushort)((v1 >> 20) & 0x3FF)),
      checked((ushort)((v1 >> 30) & 0x3FF)),
    ];
    var lmdPayloadBytes = checked((int)((v1 >> 40) & 0xFFFFF));
    var lmdBits = checked((int)((v1 >> 60) & 0x7)) - 7;
    var lState = checked((ushort)((v2 >> 32) & 0x3FF));
    var mState = checked((ushort)((v2 >> 42) & 0x3FF));
    var dState = checked((ushort)((v2 >> 52) & 0x3FF));

    var lFreq = new ushort[LSymbols];
    var mFreq = new ushort[MSymbols];
    var dFreq = new ushort[DSymbols];
    var literalFreq = new ushort[LiteralSymbols];
    var frequencyBytes = serialized.AsSpan(V2FixedHeaderSize);
    if (!frequencyBytes.IsEmpty)
      DecodeV2Frequencies(frequencyBytes, lFreq, mFreq, dFreq, literalFreq);

    return (new Header(rawBytes, literals, matches, literalPayloadBytes, lmdPayloadBytes,
      literalBits, literalState, lmdBits, lState, mState, dState,
      lFreq, mFreq, dFreq, literalFreq), serialized);
  }

  private static void DecodeV2Frequencies(ReadOnlySpan<byte> source,
      Span<ushort> lFreq, Span<ushort> mFreq, Span<ushort> dFreq, Span<ushort> literalFreq) {
    const int totalSymbols = LSymbols + MSymbols + DSymbols + LiteralSymbols;
    Span<ushort> all = stackalloc ushort[totalSymbols];
    uint accumulator = 0;
    var accumulatorBits = 0;
    var position = 0;

    for (var i = 0; i < all.Length; ++i) {
      while (position < source.Length && accumulatorBits + 8 <= 32) {
        accumulator |= (uint)source[position++] << accumulatorBits;
        accumulatorBits += 8;
      }

      var tableIndex = checked((int)(accumulator & 31));
      var bitCount = FrequencyBitCounts[tableIndex];
      if (bitCount > accumulatorBits)
        throw new InvalidDataException("LZFSE V2 frequency table is truncated.");

      var value = bitCount switch {
        8 => 8 + checked((int)((accumulator >> 4) & 0xF)),
        14 => 24 + checked((int)((accumulator >> 4) & 0x3FF)),
        _ => FrequencyValues[tableIndex],
      };
      if (value < 0)
        throw new InvalidDataException("LZFSE V2 frequency code is invalid.");
      all[i] = checked((ushort)value);

      accumulator >>= bitCount;
      accumulatorBits -= bitCount;
    }

    if (accumulatorBits >= 8 || position != source.Length)
      throw new InvalidDataException("LZFSE V2 frequency table does not end at the header boundary.");

    all[..LSymbols].CopyTo(lFreq);
    all.Slice(LSymbols, MSymbols).CopyTo(mFreq);
    all.Slice(LSymbols + MSymbols, DSymbols).CopyTo(dFreq);
    all[(LSymbols + MSymbols + DSymbols)..].CopyTo(literalFreq);
  }

  private static byte[] Decode(Header header, ReadOnlySpan<byte> serializedHeader,
      ReadOnlySpan<byte> literalPayload, ReadOnlySpan<byte> lmdPayload, ReadOnlySpan<byte> history) {
    var literalTable = BuildSymbolTable(LiteralStates, header.LiteralFreq);
    var lTable = BuildValueTable(LStates, header.LFreq, LExtraBits, LBaseValue);
    var mTable = BuildValueTable(MStates, header.MFreq, MExtraBits, MBaseValue);
    var dTable = BuildValueTable(DStates, header.DFreq, DExtraBits, DBaseValue);

    // Apple's decoder permits the literal bit reader to refill backwards into
    // the immediately preceding block header. Preserve that wire behavior in
    // managed code without out-of-bounds reads by making the prefix explicit.
    var literalBacking = new byte[checked(serializedHeader.Length + literalPayload.Length)];
    serializedHeader.CopyTo(literalBacking);
    literalPayload.CopyTo(literalBacking.AsSpan(serializedHeader.Length));
    var literalReader = new BackwardBitReader(literalBacking, header.LiteralBits);

    var literals = new byte[header.Literals];
    var literalStates = (ushort[])header.LiteralState.Clone();
    for (var i = 0; i < literals.Length; i += 4) {
      literalReader.Flush();
      literals[i] = DecodeSymbol(ref literalReader, literalTable, ref literalStates[0]);
      literals[i + 1] = DecodeSymbol(ref literalReader, literalTable, ref literalStates[1]);
      literals[i + 2] = DecodeSymbol(ref literalReader, literalTable, ref literalStates[2]);
      literals[i + 3] = DecodeSymbol(ref literalReader, literalTable, ref literalStates[3]);
    }

    var lmdReader = new BackwardBitReader(lmdPayload, header.LmdBits);
    var lState = header.LState;
    var mState = header.MState;
    var dState = header.DState;
    var previousDistance = -1;
    var literalPosition = 0;
    var outputPosition = 0;
    var output = new byte[header.RawBytes];

    for (var i = 0; i < header.Matches; ++i) {
      lmdReader.Flush();
      var literalLength = DecodeValue(ref lmdReader, lTable, ref lState);
      var matchLength = DecodeValue(ref lmdReader, mTable, ref mState);
      var encodedDistance = DecodeValue(ref lmdReader, dTable, ref dState);
      if (encodedDistance != 0)
        previousDistance = encodedDistance;

      if (literalLength < 0 || matchLength < 0 ||
          literalPosition > literals.Length - literalLength ||
          outputPosition > output.Length - literalLength)
        throw new InvalidDataException("LZFSE L/M/D tuple exceeds its literal or output range.");

      literals.AsSpan(literalPosition, literalLength)
        .CopyTo(output.AsSpan(outputPosition, literalLength));
      literalPosition += literalLength;
      outputPosition += literalLength;

      if (previousDistance <= 0 || previousDistance > history.Length + outputPosition ||
          outputPosition > output.Length - matchLength)
        throw new InvalidDataException("LZFSE match references an invalid distance or output range.");

      for (var j = 0; j < matchLength; ++j) {
        var sourcePosition = history.Length + outputPosition - previousDistance;
        output[outputPosition] = sourcePosition < history.Length
          ? history[sourcePosition]
          : output[sourcePosition - history.Length];
        ++outputPosition;
      }
    }

    if (outputPosition != output.Length)
      throw new InvalidDataException($"LZFSE block decoded {outputPosition} bytes, expected {output.Length}.");

    // n_literals is rounded up to a multiple of four for the interleaved FSE
    // streams. The decoder ignores at most three entropy-coded padding literals;
    // their values are not part of the reconstructed data and need not be zero.
    if (literals.Length - literalPosition is < 0 or > 3)
      throw new InvalidDataException("LZFSE block leaves more than three padding literals unused.");

    return output;
  }

  private static void ValidateHeader(Header header) {
    if (header.RawBytes is < 0 or > MaxRawBytes)
      throw new InvalidDataException("LZFSE raw block length is out of range.");
    if (header.Literals is < 0 or > LiteralsPerBlock || (header.Literals & 3) != 0)
      throw new InvalidDataException("LZFSE literal count is out of range or not a multiple of four.");
    if (header.Matches is < 0 or > MatchesPerBlock)
      throw new InvalidDataException("LZFSE match count is out of range.");
    if (header.LiteralPayloadBytes < 0 || header.LmdPayloadBytes < 0)
      throw new InvalidDataException("LZFSE payload length is negative.");
    if (header.LiteralBits is < -7 or > 0 || header.LmdBits is < -7 or > 0)
      throw new InvalidDataException("LZFSE FSE bit-count state is invalid.");
    if (header.LiteralState.Any(static state => state >= LiteralStates) ||
        header.LState >= LStates || header.MState >= MStates || header.DState >= DStates)
      throw new InvalidDataException("LZFSE FSE state is outside its table.");

    CheckFrequencySum(header.LFreq, LStates, "L");
    CheckFrequencySum(header.MFreq, MStates, "M");
    CheckFrequencySum(header.DFreq, DStates, "D");
    CheckFrequencySum(header.LiteralFreq, LiteralStates, "literal");
  }

  private static void CheckFrequencySum(ReadOnlySpan<ushort> frequencies, int states, string name) {
    var sum = 0;
    foreach (var frequency in frequencies) {
      sum += frequency;
      if (sum > states)
        throw new InvalidDataException($"LZFSE {name} frequencies exceed the {states}-state table.");
    }
  }

  private static SymbolEntry[] BuildSymbolTable(int states, ReadOnlySpan<ushort> frequencies) {
    var table = new SymbolEntry[states];
    var output = 0;
    var stateLeadingZeros = BitOperations.LeadingZeroCount((uint)states);

    for (var symbol = 0; symbol < frequencies.Length; ++symbol) {
      var frequency = frequencies[symbol];
      if (frequency == 0)
        continue;
      if (output > states - frequency)
        throw new InvalidDataException("LZFSE frequency table overflows its state table.");

      var k = BitOperations.LeadingZeroCount((uint)frequency) - stateLeadingZeros;
      var j0 = ((2 * states) >> k) - frequency;
      for (var j = 0; j < frequency; ++j) {
        var bits = j < j0 ? k : k - 1;
        var delta = j < j0
          ? ((frequency + j) << k) - states
          : (j - j0) << (k - 1);
        table[output++] = new SymbolEntry(checked((byte)bits), checked((byte)symbol), checked((short)delta), true);
      }
    }

    return table;
  }

  private static ValueEntry[] BuildValueTable(int states, ReadOnlySpan<ushort> frequencies,
      ReadOnlySpan<byte> extraBits, ReadOnlySpan<int> baseValues) {
    var table = new ValueEntry[states];
    var output = 0;
    var stateLeadingZeros = BitOperations.LeadingZeroCount((uint)states);

    for (var symbol = 0; symbol < frequencies.Length; ++symbol) {
      var frequency = frequencies[symbol];
      if (frequency == 0)
        continue;
      if (output > states - frequency)
        throw new InvalidDataException("LZFSE frequency table overflows its state table.");

      var k = BitOperations.LeadingZeroCount((uint)frequency) - stateLeadingZeros;
      var j0 = ((2 * states) >> k) - frequency;
      var valueBits = extraBits[symbol];
      for (var j = 0; j < frequency; ++j) {
        var stateBits = j < j0 ? k : k - 1;
        var delta = j < j0
          ? ((frequency + j) << k) - states
          : (j - j0) << (k - 1);
        table[output++] = new ValueEntry(checked((byte)(stateBits + valueBits)), valueBits,
          checked((short)delta), baseValues[symbol], true);
      }
    }

    return table;
  }

  private static byte DecodeSymbol(ref BackwardBitReader reader, ReadOnlySpan<SymbolEntry> table, ref ushort state) {
    if (state >= table.Length || !table[state].Valid)
      throw new InvalidDataException("LZFSE literal decoder reached an uninitialized FSE state.");
    var entry = table[state];
    var bits = reader.Pull(entry.Bits);
    var next = checked(entry.Delta + (int)bits);
    if ((uint)next >= table.Length)
      throw new InvalidDataException("LZFSE literal decoder produced an invalid FSE state.");
    state = checked((ushort)next);
    return entry.Symbol;
  }

  private static int DecodeValue(ref BackwardBitReader reader, ReadOnlySpan<ValueEntry> table, ref ushort state) {
    if (state >= table.Length || !table[state].Valid)
      throw new InvalidDataException("LZFSE value decoder reached an uninitialized FSE state.");
    var entry = table[state];
    var bits = reader.Pull(entry.TotalBits);
    var stateBits = bits >> entry.ValueBits;
    var next = checked(entry.Delta + (int)stateBits);
    if ((uint)next >= table.Length)
      throw new InvalidDataException("LZFSE value decoder produced an invalid FSE state.");
    state = checked((ushort)next);
    return checked(entry.BaseValue + (int)(bits & Mask(entry.ValueBits)));
  }

  private static ushort[] ReadFrequencyArray(ReadOnlySpan<byte> bytes, ref int offset, int count) {
    var result = new ushort[count];
    for (var i = 0; i < count; ++i) {
      result[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
      offset += 2;
    }
    return result;
  }

  private static int ReadBoundedUInt32(ReadOnlySpan<byte> data, int offset, int maximum, string field) {
    var value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    if (value > maximum)
      throw new InvalidDataException($"LZFSE {field} {value} exceeds the supported format limit {maximum}.");
    return checked((int)value);
  }

  private static ulong Mask(int bits) => bits switch {
    0 => 0,
    64 => ulong.MaxValue,
    _ => (1UL << bits) - 1,
  };

  private readonly record struct Header(
    int RawBytes,
    int Literals,
    int Matches,
    int LiteralPayloadBytes,
    int LmdPayloadBytes,
    int LiteralBits,
    ushort[] LiteralState,
    int LmdBits,
    ushort LState,
    ushort MState,
    ushort DState,
    ushort[] LFreq,
    ushort[] MFreq,
    ushort[] DFreq,
    ushort[] LiteralFreq);

  private readonly record struct SymbolEntry(byte Bits, byte Symbol, short Delta, bool Valid);
  private readonly record struct ValueEntry(byte TotalBits, byte ValueBits, short Delta, int BaseValue, bool Valid);

  /// <summary>
  /// Apple's FSE payloads are written forwards but decoded backwards. This is
  /// the checked, managed equivalent of the 64-bit <c>fse_in_stream</c> path.
  /// </summary>
  private ref struct BackwardBitReader {
    private readonly ReadOnlySpan<byte> _source;
    private int _position;
    private ulong _accumulator;
    private int _bits;

    public BackwardBitReader(ReadOnlySpan<byte> source, int initialBits) {
      if (initialBits is < -7 or > 0)
        throw new InvalidDataException("LZFSE FSE stream carries an invalid initial bit count.");

      this._source = source;
      this._position = source.Length;
      this._accumulator = 0;
      this._bits = 0;

      if (initialBits != 0) {
        if (this._position < 8)
          throw new InvalidDataException("LZFSE FSE stream is too short for its initial accumulator.");
        this._position -= 8;
        this._accumulator = BinaryPrimitives.ReadUInt64LittleEndian(source[this._position..]);
        this._bits = initialBits + 64;
      } else {
        if (this._position < 7)
          throw new InvalidDataException("LZFSE FSE stream is too short for its initial accumulator.");
        this._position -= 7;
        for (var i = 0; i < 7; ++i)
          this._accumulator |= (ulong)source[this._position + i] << (8 * i);
        this._bits = 56;
      }

      if (this._bits is < 56 or >= 64 || (this._accumulator >> this._bits) != 0)
        throw new InvalidDataException("LZFSE FSE stream has non-zero padding bits.");
    }

    public void Flush() {
      var bitsToAdd = (63 - this._bits) & ~7;
      if (bitsToAdd == 0)
        return;

      var bytesToAdd = bitsToAdd >> 3;
      var newPosition = this._position - bytesToAdd;
      if (newPosition < 0)
        throw new InvalidDataException("LZFSE FSE stream is truncated while refilling backwards.");

      ulong incoming = 0;
      for (var i = 0; i < bytesToAdd; ++i)
        incoming |= (ulong)this._source[newPosition + i] << (8 * i);

      this._position = newPosition;
      this._accumulator = unchecked((this._accumulator << bitsToAdd) | incoming);
      this._bits += bitsToAdd;
    }

    public ulong Pull(int count) {
      if (count < 0 || count > this._bits)
        throw new InvalidDataException("LZFSE FSE decoder attempted to consume unavailable bits.");
      this._bits -= count;
      var result = this._accumulator >> this._bits;
      this._accumulator &= Mask(this._bits);
      return result;
    }
  }
}
