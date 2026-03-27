using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// RAR5 LZ+Huffman compression engine.
/// Encodes data using adaptive multi-table Huffman coding with LZ77 match references,
/// producing output compatible with <see cref="Rar5Decoder"/> and 7z.
/// </summary>
public sealed class Rar5Encoder {
  private readonly int _dictionarySize;
  private readonly byte[] _window;
  private readonly int _windowMask;
  private int _windowPos;
  private readonly int[] _repDist = [0, 0, 0, 0];
  private int _lastLength;

  /// <summary>
  /// Initializes a new <see cref="Rar5Encoder"/> with the specified dictionary size.
  /// </summary>
  /// <param name="dictionarySize">Dictionary (window) size in bytes. Must be a power of two, minimum 128 KB.</param>
  public Rar5Encoder(int dictionarySize = Rar5Constants.MinDictionarySize) {
    ArgumentOutOfRangeException.ThrowIfLessThan(dictionarySize, Rar5Constants.MinDictionarySize);
    var size = 1;
    while (size < dictionarySize && size > 0) size <<= 1;
    this._dictionarySize = size;
    this._window = new byte[size];
    this._windowMask = size - 1;
  }

  /// <summary>
  /// Compresses data using the RAR5 algorithm.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <returns>The compressed data.</returns>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    byte[] workBuffer;
    int dataStart;

    if (this._windowPos > 0) {
      var histLen = Math.Min(this._windowPos, this._dictionarySize);
      workBuffer = new byte[histLen + data.Length];
      var histStart = (this._windowPos - histLen + this._dictionarySize) & this._windowMask;
      for (var i = 0; i < histLen; ++i)
        workBuffer[i] = this._window[(histStart + i) & this._windowMask];
      data.CopyTo(workBuffer.AsSpan(histLen));
      dataStart = histLen;
    }
    else {
      workBuffer = data.ToArray();
      dataStart = 0;
    }

    var writer = new Rar5BitWriter();
    var matchFinder = new HashChainMatchFinder(this._dictionarySize);

    for (var i = 0; i < dataStart; ++i)
      matchFinder.InsertPosition(workBuffer, i);

    var tokens = CollectTokens(workBuffer, dataStart, workBuffer.Length, matchFinder,
      this._repDist, ref this._lastLength);

    // Build frequency tables
    var mainFreq = new int[Rar5Constants.MainTableSize];
    var offsetFreq = new int[Rar5Constants.OffsetTableSize];
    var lowOffsetFreq = new int[Rar5Constants.LowOffsetTableSize];
    var lengthFreq = new int[Rar5Constants.LengthTableSize];

    foreach (var token in tokens)
      CountFrequencies(token, mainFreq, offsetFreq, lowOffsetFreq, lengthFreq);

    EnsureAtLeastTwo(mainFreq);
    EnsureAtLeastTwo(offsetFreq);
    EnsureAtLeastTwo(lowOffsetFreq);
    EnsureAtLeastTwo(lengthFreq);

    var mainEnc = new Rar5HuffmanEncoder();
    var offsetEnc = new Rar5HuffmanEncoder();
    var lowOffsetEnc = new Rar5HuffmanEncoder();
    var lengthEnc = new Rar5HuffmanEncoder();

    mainEnc.Build(mainFreq, Rar5Constants.MainTableSize);
    offsetEnc.Build(offsetFreq, Rar5Constants.OffsetTableSize);
    lowOffsetEnc.Build(lowOffsetFreq, Rar5Constants.LowOffsetTableSize);
    lengthEnc.Build(lengthFreq, Rar5Constants.LengthTableSize);

    // Encode tables + tokens to a temporary buffer to measure total bit size
    var blockWriter = new Rar5BitWriter();
    WriteTables(blockWriter, mainEnc, offsetEnc, lowOffsetEnc, lengthEnc);
    foreach (var token in tokens)
      EncodeToken(blockWriter, token, mainEnc, offsetEnc, lowOffsetEnc, lengthEnc);
    var blockBitSize = blockWriter.BitCount;
    var blockBytes = blockWriter.ToArray();

    // Write RAR5 block header
    WriteBlockHeader(writer, blockBitSize, tablePresent: true, lastBlock: true);

    // Write the block data (tables + tokens)
    writer.WriteBytes(blockBytes, blockBitSize);

    // Update window
    for (var i = dataStart; i < workBuffer.Length; ++i) {
      this._window[this._windowPos] = workBuffer[i];
      this._windowPos = (this._windowPos + 1) & this._windowMask;
    }

    return writer.ToArray();
  }

  private List<Rar5Token> CollectTokens(ReadOnlySpan<byte> data, int start, int end,
      HashChainMatchFinder matchFinder, int[] repDist, ref int lastLength) {
    var tokens = new List<Rar5Token>();
    var pos = start;

    while (pos < end) {
      var match = matchFinder.FindMatch(data, pos, this._dictionarySize, 0x101 + 8, 2);

      var bonus = Rar5Constants.LengthBonus(match.Distance);
      if (match.Length >= 2 + bonus) {
        var useLen = match.Length;
        tokens.Add(new Rar5Token { Type = Rar5TokenType.Match, Length = useLen - bonus, Distance = match.Distance });
        repDist[3] = repDist[2];
        repDist[2] = repDist[1];
        repDist[1] = repDist[0];
        repDist[0] = match.Distance;
        lastLength = useLen;
        for (var i = 1; i < useLen && pos + i < data.Length; ++i)
          matchFinder.InsertPosition(data, pos + i);
        pos += useLen;
      }
      else {
        tokens.Add(new Rar5Token { Type = Rar5TokenType.Literal, Literal = data[pos] });
        pos++;
      }
    }

    return tokens;
  }

  private static void CountFrequencies(Rar5Token token,
      int[] mainFreq, int[] offsetFreq, int[] lowOffsetFreq, int[] lengthFreq) {
    switch (token.Type) {
      case Rar5TokenType.Literal:
        ++mainFreq[token.Literal];
        break;

      case Rar5TokenType.Match: {
        // Match length encoded in main table symbol via SlotToLength inverse
        var lengthSlot = GetLengthSlot(token.Length);
        ++mainFreq[Rar5Constants.MatchBase + lengthSlot];

        var distSlot = GetDistanceSlot(token.Distance - 1);
        ++offsetFreq[distSlot];
        if (Rar5Constants.DistanceExtraBits(distSlot) >= 4)
          ++lowOffsetFreq[((token.Distance - 1) - Rar5Constants.DistanceBase(distSlot)) & 0xF];
        break;
      }

      case Rar5TokenType.RepeatOffset: {
        ++mainFreq[Rar5Constants.RepeatOffset0 + token.RepeatIndex];
        if (token.RepeatIndex > 0)
          ++lengthFreq[GetLengthSlot(token.Length)];
        break;
      }
    }
  }

  private static void EncodeToken(Rar5BitWriter writer, Rar5Token token,
      Rar5HuffmanEncoder mainEnc, Rar5HuffmanEncoder offsetEnc,
      Rar5HuffmanEncoder lowOffsetEnc, Rar5HuffmanEncoder lengthEnc) {
    switch (token.Type) {
      case Rar5TokenType.Literal:
        mainEnc.EncodeSymbol(writer, token.Literal);
        break;

      case Rar5TokenType.Match: {
        // Match length: main table symbol = MatchBase + slot, extra bits from stream
        var lengthSlot = GetLengthSlot(token.Length);
        mainEnc.EncodeSymbol(writer, Rar5Constants.MatchBase + lengthSlot);
        WriteLengthExtra(writer, lengthSlot, token.Length);
        EncodeDistance(writer, token.Distance, offsetEnc, lowOffsetEnc);
        break;
      }

      case Rar5TokenType.RepeatOffset: {
        mainEnc.EncodeSymbol(writer, Rar5Constants.RepeatOffset0 + token.RepeatIndex);
        if (token.RepeatIndex > 0) {
          var lenSlot = GetLengthSlot(token.Length);
          lengthEnc.EncodeSymbol(writer, lenSlot);
          WriteLengthExtra(writer, lenSlot, token.Length);
        }
        break;
      }
    }
  }

  /// <summary>Writes the extra bits for a length slot (SlotToLength inverse).</summary>
  private static void WriteLengthExtra(Rar5BitWriter writer, int slot, int length) {
    if (slot < 8) return;
    var lBits = slot / 4 - 1;
    var baseLen = 2 + ((4 | (slot & 3)) << lBits);
    writer.WriteBits((uint)(length - baseLen), lBits);
  }

  private static void EncodeDistance(Rar5BitWriter writer, int distance, Rar5HuffmanEncoder offsetEnc, Rar5HuffmanEncoder lowOffsetEnc) {
    var dist0 = distance - 1;
    var slot = GetDistanceSlot(dist0);
    offsetEnc.EncodeSymbol(writer, slot);

    var extraBits = Rar5Constants.DistanceExtraBits(slot);
    if (extraBits > 0) {
      var extra = dist0 - Rar5Constants.DistanceBase(slot);
      if (extraBits >= 4) {
        if (extraBits > 4)
          writer.WriteBits((uint)(extra >> 4), extraBits - 4);

        lowOffsetEnc.EncodeSymbol(writer, extra & 0xF);
      }
      else {
        writer.WriteBits((uint)extra, extraBits);
      }
    }
  }

  private static void WriteTables(Rar5BitWriter writer,
      Rar5HuffmanEncoder mainEnc, Rar5HuffmanEncoder offsetEnc,
      Rar5HuffmanEncoder lowOffsetEnc, Rar5HuffmanEncoder lengthEnc) {
    var rleMain = ComputeRleSequence(mainEnc.CodeLengths, Rar5Constants.MainTableSize);
    var rleOffset = ComputeRleSequence(offsetEnc.CodeLengths, Rar5Constants.OffsetTableSize);
    var rleLowOffset = ComputeRleSequence(lowOffsetEnc.CodeLengths, Rar5Constants.LowOffsetTableSize);
    var rleLength = ComputeRleSequence(lengthEnc.CodeLengths, Rar5Constants.LengthTableSize);

    var clFreq = new int[Rar5Constants.CodeLengthTableSize];
    CountRleFrequencies(rleMain, clFreq);
    CountRleFrequencies(rleOffset, clFreq);
    CountRleFrequencies(rleLowOffset, clFreq);
    CountRleFrequencies(rleLength, clFreq);

    var clEncoder = new Rar5HuffmanEncoder();
    clEncoder.Build(clFreq, Rar5Constants.CodeLengthTableSize);

    // Write pre-code lengths with special value-15 escape:
    // If length == 15, write 15 (4 bits) + 0 (4 bits) to distinguish from zero-fill directive.
    for (var i = 0; i < Rar5Constants.CodeLengthTableSize; ++i) {
      writer.WriteBits((uint)clEncoder.CodeLengths[i], 4);
      if (clEncoder.CodeLengths[i] == 15)
        writer.WriteBits(0, 4); // escape: count=0 means "literal 15"
    }

    WriteRleSequence(writer, rleMain, clEncoder);
    WriteRleSequence(writer, rleOffset, clEncoder);
    WriteRleSequence(writer, rleLowOffset, clEncoder);
    WriteRleSequence(writer, rleLength, clEncoder);
  }

  /// <summary>
  /// RAR5 RLE symbols:
  /// 0-15 direct code lengths, 16 repeat prev (3 bits +3), 17 repeat prev (7 bits +11),
  /// 18 fill zeros (3 bits +3), 19 fill zeros (7 bits +11).
  /// </summary>
  private static List<(int sym, int extraBits, int extraValue)> ComputeRleSequence(
      int[] codeLengths, int numSymbols) {
    var rle = new List<(int sym, int extraBits, int extraValue)>();
    var i = 0;

    while (i < numSymbols) {
      if (codeLengths[i] == 0) {
        var run = 1;
        while (i + run < numSymbols && codeLengths[i + run] == 0) ++run;
        i += run;

        while (run > 0) {
          if (run >= 11) {
            var count = Math.Min(run, 138);
            rle.Add((19, 7, count - 11));
            run -= count;
          }
          else if (run >= 3) {
            var count = Math.Min(run, 10);
            rle.Add((18, 3, count - 3));
            run -= count;
          }
          else { rle.Add((0, 0, 0)); --run; }
        }
      }
      else {
        var val = codeLengths[i];
        rle.Add((val, 0, 0));
        ++i;

        var rep = 0;
        while (i < numSymbols && codeLengths[i] == val) {
          ++rep;
          ++i;
        }

        while (rep > 0) {
          if (rep >= 11) {
            var count = Math.Min(rep, 138);
            rle.Add((17, 7, count - 11));
            rep -= count;
          }
          else if (rep >= 3) {
            var count = Math.Min(rep, 10);
            rle.Add((16, 3, count - 3));
            rep -= count;
          }
          else { rle.Add((val, 0, 0)); --rep; }
        }
      }
    }

    return rle;
  }

  private static void CountRleFrequencies(List<(int sym, int extraBits, int extraValue)> rle, int[] freq) {
    foreach (var (sym, _, _) in rle)
      ++freq[sym];
  }

  private static void WriteRleSequence(Rar5BitWriter writer,
      List<(int sym, int extraBits, int extraValue)> rle, Rar5HuffmanEncoder clEncoder) {
    foreach (var (sym, extraBits, extraValue) in rle) {
      clEncoder.EncodeSymbol(writer, sym);
      if (extraBits > 0)
        writer.WriteBits((uint)extraValue, extraBits);
    }
  }

  private static int GetDistanceSlot(int distance) {
    if (distance < 4) return distance;
    var p = 31 - int.LeadingZeroCount(distance);
    return 2 * p + ((distance >> (p - 1)) & 1);
  }

  /// <summary>Inverse of SlotToLength: given a match length, returns slot 0-43.</summary>
  private static int GetLengthSlot(int length) {
    if (length <= 9) return length - 2;
    for (var slot = 43; slot >= 8; --slot) {
      var lBits = slot / 4 - 1;
      var baseLen = 2 + ((4 | (slot & 3)) << lBits);
      if (length >= baseLen && length <= baseLen + (1 << lBits) - 1)
        return slot;
    }
    return 43;
  }

  /// <summary>
  /// Writes the RAR5 block header.
  /// Format: byte-align, 1 byte flags, 1 byte checksum, 1-3 bytes BlockSize (LE).
  /// Bits 0-2 of flags = (7 - paddingBits), i.e., (validBitsInLastByte - 1) mod 8.
  /// Checksum = 0x5A ^ flags ^ XOR(all size bytes).
  /// </summary>
  private static void WriteBlockHeader(Rar5BitWriter writer, int blockBitSize, bool tablePresent, bool lastBlock) {
    var bitsToAlign = (8 - (writer.BitCount % 8)) % 8;
    if (bitsToAlign > 0) writer.WriteBits(0, bitsToAlign);

    var blockSize = (blockBitSize + 7) / 8;
    var paddingBits = blockSize * 8 - blockBitSize; // 0-7 unused bits in last byte

    int byteCount;
    if (blockSize <= 0xFF) byteCount = 1;
    else if (blockSize <= 0xFFFF) byteCount = 2;
    else byteCount = 3;

    var blockFlags = (byte)(
      ((7 - paddingBits) & 0x07) |
      (((byteCount - 1) & 0x03) << 3) |
      (lastBlock ? 0x40 : 0x00) |
      (tablePresent ? 0x80 : 0x00));

    // Compute checksum: 0x5A ^ flags ^ XOR(all size bytes)
    var checkSum = (byte)(0x5A ^ blockFlags);
    for (var i = 0; i < byteCount; ++i)
      checkSum ^= (byte)((blockSize >> (i * 8)) & 0xFF);

    writer.WriteBits(blockFlags, 8);
    writer.WriteBits(checkSum, 8);
    for (var i = 0; i < byteCount; ++i)
      writer.WriteBits((uint)((blockSize >> (i * 8)) & 0xFF), 8);
  }

  private static void EnsureAtLeastTwo(int[] freq) {
    var count = 0;
    for (var i = 0; i < freq.Length; ++i)
      if (freq[i] > 0) ++count;

    // Need at least 2 symbols to avoid single-symbol Huffman ambiguity.
    // RAR may consume 0 bits for single-symbol tables,
    // so we ensure 2+ symbols so the decoder always reads ≥1 bit.
    switch (count) {
      case 0:
        freq[0] = 1;
        freq[1] = 1;
        break;
      case 1:
        for (var i = 0; i < freq.Length; ++i)
          if (freq[i] == 0) { freq[i] = 1; break; }
        break;
    }
  }

  private enum Rar5TokenType { Literal, Match, RepeatOffset }

  #pragma warning disable CS0649 // RepeatIndex: repeat offsets currently disabled
  private struct Rar5Token {
    public Rar5TokenType Type;
    public byte Literal;
    public int Length;
    public int Distance;
    public int RepeatIndex;
  }
  #pragma warning restore CS0649
}
