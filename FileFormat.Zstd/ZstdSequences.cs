using System.Numerics;
using Compression.Core.Entropy.Fse;

namespace FileFormat.Zstd;

/// <summary>
/// Represents a single Zstandard sequence consisting of a literal run,
/// a match copy, and an offset for the match.
/// </summary>
/// <param name="LiteralLength">The number of literal bytes preceding this match.</param>
/// <param name="MatchLength">The match length in bytes (>= 3).</param>
/// <param name="Offset">The match offset (1-based distance back).</param>
internal readonly record struct ZstdSequence(int LiteralLength, int MatchLength, int Offset);

/// <summary>
/// Encodes and decodes the sequences section of Zstandard compressed blocks.
/// Sequences are encoded using three interleaved FSE streams for literal length,
/// offset, and match length codes, with extra bits appended.
/// </summary>
internal static class ZstdSequences {
  /// <summary>Sequence compression mode: use predefined default tables.</summary>
  private const int ModePredefined = 0;

  /// <summary>Sequence compression mode: single symbol repeated (RLE).</summary>
  private const int ModeRle = 1;

  /// <summary>Sequence compression mode: FSE-compressed table follows.</summary>
  private const int ModeFseCompressed = 2;

  /// <summary>Sequence compression mode: reuse previous block's table.</summary>
  private const int ModeRepeat = 3;

  /// <summary>
  /// Decodes sequences from a compressed block's sequence section.
  /// </summary>
  /// <param name="blockData">The compressed block data.</param>
  /// <param name="pos">The current position in the block data; updated on return.</param>
  /// <param name="remainingBlockSize">The number of bytes remaining in the block from pos.</param>
  /// <param name="repeatOffsets">The three repeat offsets, updated during decoding.</param>
  /// <param name="prevLlTable">Previous block's literal length FSE table; updated on return.</param>
  /// <param name="prevOfTable">Previous block's offset FSE table; updated on return.</param>
  /// <param name="prevMlTable">Previous block's match length FSE table; updated on return.</param>
  /// <returns>An array of decoded sequences.</returns>
  /// <exception cref="InvalidDataException">The sequence data is malformed.</exception>
  public static ZstdSequence[] DecodeSequences(ReadOnlySpan<byte> blockData, ref int pos,
    int remainingBlockSize, int[] repeatOffsets,
    ref FseTable? prevLlTable, ref FseTable? prevOfTable, ref FseTable? prevMlTable) {
    if (pos >= blockData.Length)
      return [];

    var numSequences = ReadSequenceCount(blockData, ref pos);
    if (numSequences == 0)
      return [];

    if (pos >= blockData.Length)
      throw new InvalidDataException("Truncated sequence compression modes.");

    int modesByte = blockData[pos++];
    var llMode = (modesByte >> 6) & 3;
    var ofMode = (modesByte >> 4) & 3;
    var mlMode = (modesByte >> 2) & 3;

    var llTable = BuildDecodingTable(blockData, ref pos, llMode, prevLlTable,
      ZstdConstants.DefaultLiteralLengthCounts, ZstdConstants.DefaultLiteralLengthCounts.Length - 1,
      ZstdConstants.LiteralLengthDefaultTableLog);
    var ofTable = BuildDecodingTable(blockData, ref pos, ofMode, prevOfTable,
      ZstdConstants.DefaultOffsetCounts, ZstdConstants.DefaultOffsetCounts.Length - 1,
      ZstdConstants.OffsetDefaultTableLog);
    var mlTable = BuildDecodingTable(blockData, ref pos, mlMode, prevMlTable,
      ZstdConstants.DefaultMatchLengthCounts, ZstdConstants.DefaultMatchLengthCounts.Length - 1,
      ZstdConstants.MatchLengthDefaultTableLog);

    prevLlTable = llTable;
    prevOfTable = ofTable;
    prevMlTable = mlTable;

    var bitstream = blockData.Slice(pos);
    pos = blockData.Length;

    return DecodeSequenceBitstream(bitstream, numSequences, llTable, ofTable, mlTable, repeatOffsets);
  }

  /// <summary>
  /// Encodes sequences using predefined FSE tables and a backward bitstream.
  /// </summary>
  /// <param name="sequences">The sequences to encode.</param>
  /// <param name="output">The output buffer.</param>
  /// <param name="outputPos">The current position in the output buffer.</param>
  /// <param name="repeatOffsets">The three repeat offsets, updated during encoding.</param>
  /// <returns>The number of bytes written.</returns>
  public static int EncodeSequences(ZstdSequence[] sequences, byte[] output, int outputPos,
    int[] repeatOffsets) {
    var startPos = outputPos;

    if (sequences.Length == 0) {
      output[outputPos++] = 0;
      return outputPos - startPos;
    }

    outputPos += WriteSequenceCount(sequences.Length, output, outputPos);

    // All Predefined mode
    output[outputPos++] = 0;

    var llTable = FseTable.Build(
      ZstdConstants.DefaultLiteralLengthCounts,
      ZstdConstants.DefaultLiteralLengthCounts.Length - 1,
      ZstdConstants.LiteralLengthDefaultTableLog);
    var ofTable = FseTable.Build(
      ZstdConstants.DefaultOffsetCounts,
      ZstdConstants.DefaultOffsetCounts.Length - 1,
      ZstdConstants.OffsetDefaultTableLog);
    var mlTable = FseTable.Build(
      ZstdConstants.DefaultMatchLengthCounts,
      ZstdConstants.DefaultMatchLengthCounts.Length - 1,
      ZstdConstants.MatchLengthDefaultTableLog);

    var bitstream = EncodeSequenceBitstream(sequences, llTable, ofTable, mlTable, repeatOffsets);
    bitstream.CopyTo(output.AsSpan(outputPos));
    outputPos += bitstream.Length;

    return outputPos - startPos;
  }

  /// <summary>
  /// Reads the number of sequences from the sequence header.
  /// </summary>
  private static int ReadSequenceCount(ReadOnlySpan<byte> data, ref int pos) {
    if (pos >= data.Length)
      throw new InvalidDataException("Truncated sequence count.");

    int b0 = data[pos++];
    if (b0 == 0) return 0;
    if (b0 < 128) return b0;

    if (pos >= data.Length)
      throw new InvalidDataException("Truncated sequence count (2-byte).");
    int b1 = data[pos++];
    if (b0 < 255) return ((b0 - 128) << 8) + b1;

    if (pos >= data.Length)
      throw new InvalidDataException("Truncated sequence count (3-byte).");
    int b2 = data[pos++];
    return b1 + (b2 << 8) + 0x7F00;
  }

  /// <summary>
  /// Writes the number of sequences to the output.
  /// </summary>
  private static int WriteSequenceCount(int count, byte[] output, int pos) {
    if (count < 128) {
      output[pos] = (byte)count;
      return 1;
    }

    if (count < 0x7F00) {
      output[pos] = (byte)((count >> 8) + 128);
      output[pos + 1] = (byte)(count & 0xFF);
      return 2;
    }

    output[pos] = 255;
    var adjusted = count - 0x7F00;
    output[pos + 1] = (byte)(adjusted & 0xFF);
    output[pos + 2] = (byte)((adjusted >> 8) & 0xFF);
    return 3;
  }

  /// <summary>
  /// Builds an FSE decoding table based on the compression mode.
  /// </summary>
  private static FseTable BuildDecodingTable(ReadOnlySpan<byte> data, ref int pos, int mode,
    FseTable? previousTable, short[] defaultCounts, int defaultMaxSymbol, int defaultTableLog) {
    switch (mode) {
      case ModePredefined:
        return FseTable.Build(defaultCounts, defaultMaxSymbol, defaultTableLog);
      case ModeRle:
        if (pos >= data.Length)
          throw new InvalidDataException("Truncated RLE sequence mode data.");
        var rleSymbol = data[pos++];
        var rleCounts = new short[rleSymbol + 1];
        rleCounts[rleSymbol] = (short)(1 << defaultTableLog);
        return FseTable.Build(rleCounts, rleSymbol, defaultTableLog);
      case ModeFseCompressed:
        var (normalizedCounts, maxSymbol, tableLog, bytesRead) =
          FseDecoder.ReadNormalizedCounts(data.Slice(pos));
        pos += bytesRead;
        return FseTable.Build(normalizedCounts, maxSymbol, tableLog);
      case ModeRepeat:
        if (previousTable is null)
          throw new InvalidDataException("Repeat sequence mode used without a previous table.");
        return previousTable;
      default:
        throw new InvalidDataException($"Unknown sequence compression mode: {mode}");
    }
  }

  /// <summary>
  /// Decodes the interleaved FSE bitstream to produce sequences.
  /// </summary>
  private static ZstdSequence[] DecodeSequenceBitstream(ReadOnlySpan<byte> bitstream,
    int numSequences, FseTable llTable, FseTable ofTable, FseTable mlTable,
    int[] repeatOffsets) {
    var sequences = new ZstdSequence[numSequences];
    var totalBits = FindTotalBits(bitstream);
    var reader = new BackwardBitReader(bitstream, totalBits);

    var llState = reader.ReadBits(llTable.TableLog);
    var ofState = reader.ReadBits(ofTable.TableLog);
    var mlState = reader.ReadBits(mlTable.TableLog);

    for (var i = 0; i < numSequences; ++i) {
      int ofCode = ofTable.Symbol[ofState];
      int llCode = llTable.Symbol[llState];
      int mlCode = mlTable.Symbol[mlState];

      // Offset extra bits
      int offset;
      if (ofCode > 0) {
        var ofExtra = reader.ReadBits(ofCode);
        offset = (1 << ofCode) | ofExtra;
      }
      else
        offset = 1;

      // Match length extra bits
      var mlExtraBits = ZstdConstants.MatchLengthExtraBits[mlCode];
      var matchLength = ZstdConstants.MatchLengthBase[mlCode];
      if (mlExtraBits > 0)
        matchLength += reader.ReadBits(mlExtraBits);

      // Literal length extra bits
      var llExtraBits = ZstdConstants.LitLengthExtraBits[llCode];
      var litLength = ZstdConstants.LitLengthBase[llCode];
      if (llExtraBits > 0)
        litLength += reader.ReadBits(llExtraBits);

      var resolvedOffset = ResolveOffset(offset, litLength, repeatOffsets);
      sequences[i] = new ZstdSequence(litLength, matchLength, resolvedOffset);

      // State updates (except for last sequence)
      if (i < numSequences - 1) {
        var llBits = llTable.NumBits[llState];
        llState = llTable.NewStateBase[llState] + (llBits > 0 ? reader.ReadBits(llBits) : 0);

        var mlBits = mlTable.NumBits[mlState];
        mlState = mlTable.NewStateBase[mlState] + (mlBits > 0 ? reader.ReadBits(mlBits) : 0);

        var ofBits = ofTable.NumBits[ofState];
        ofState = ofTable.NewStateBase[ofState] + (ofBits > 0 ? reader.ReadBits(ofBits) : 0);
      }
    }

    return sequences;
  }

  /// <summary>
  /// Encodes sequences into a backward bitstream.
  /// The encoding is the exact inverse of decoding: we simulate the decoder forward,
  /// record all the bit values that would need to be read, then write them in reverse.
  /// </summary>
  private static byte[] EncodeSequenceBitstream(ZstdSequence[] sequences,
    FseTable llTable, FseTable ofTable, FseTable mlTable,
    int[] repeatOffsets) {
    int[] encRepeatOffsets = [repeatOffsets[0], repeatOffsets[1], repeatOffsets[2]];

    // Pre-compute codes and extra bits for each sequence
    var llCodes = new byte[sequences.Length];
    var mlCodes = new byte[sequences.Length];
    var ofCodes = new byte[sequences.Length];
    var llExtraVals = new int[sequences.Length];
    var mlExtraVals = new int[sequences.Length];
    var ofExtraVals = new int[sequences.Length];
    var llExtraNBits = new int[sequences.Length];
    var mlExtraNBits = new int[sequences.Length];
    var ofExtraNBits = new int[sequences.Length];

    for (var i = 0; i < sequences.Length; ++i) {
      var seq = sequences[i];

      llCodes[i] = (byte)ZstdConstants.GetLitLengthCode(seq.LiteralLength);
      mlCodes[i] = (byte)ZstdConstants.GetMatchLengthCode(seq.MatchLength);

      llExtraNBits[i] = ZstdConstants.LitLengthExtraBits[llCodes[i]];
      llExtraVals[i] = seq.LiteralLength - ZstdConstants.LitLengthBase[llCodes[i]];

      mlExtraNBits[i] = ZstdConstants.MatchLengthExtraBits[mlCodes[i]];
      mlExtraVals[i] = seq.MatchLength - ZstdConstants.MatchLengthBase[mlCodes[i]];

      var encodedOffset = EncodeOffset(seq.Offset, seq.LiteralLength, encRepeatOffsets);
      ofCodes[i] = (byte)ZstdConstants.GetOffsetCode(encodedOffset);
      ofExtraNBits[i] = ofCodes[i];
      ofExtraVals[i] = encodedOffset - (1 << ofCodes[i]);
    }

    // Build encoding tables: for each symbol, collect all states that decode to it,
    // sorted by state value. During encoding, we pick a state whose symbol matches
    // and which is reachable from the current encoding state.
    var llTableSize = 1 << llTable.TableLog;
    var ofTableSize = 1 << ofTable.TableLog;
    var mlTableSize = 1 << mlTable.TableLog;

    var llStatesForSymbol = BuildSymbolStates(llTable, llTableSize);
    var ofStatesForSymbol = BuildSymbolStates(ofTable, ofTableSize);
    var mlStatesForSymbol = BuildSymbolStates(mlTable, mlTableSize);

    // The encoding algorithm works backward. We pick target states for each sequence
    // and compute the bits needed to transition between them.
    //
    // For the Nth (last) sequence, we pick any state for the symbol.
    // For sequence N-1, we need a state whose numBits and NewStateBase allow
    // transitioning to the state for sequence N.
    //
    // Working backward:
    // 1. Choose final states for the last sequence
    // 2. For each previous sequence, find a source state that can transition to the target

    var llTargetStates = new int[sequences.Length];
    var ofTargetStates = new int[sequences.Length];
    var mlTargetStates = new int[sequences.Length];

    // Start with the last sequence: pick any valid state
    llTargetStates[sequences.Length - 1] = llStatesForSymbol[llCodes[sequences.Length - 1]][0];
    ofTargetStates[sequences.Length - 1] = ofStatesForSymbol[ofCodes[sequences.Length - 1]][0];
    mlTargetStates[sequences.Length - 1] = mlStatesForSymbol[mlCodes[sequences.Length - 1]][0];

    // Work backward to find source states that can transition to each target
    for (var i = sequences.Length - 2; i >= 0; --i) {
      llTargetStates[i] = FindSourceState(llTable, llCodes[i], llTargetStates[i + 1],
        llStatesForSymbol, llTableSize);
      ofTargetStates[i] = FindSourceState(ofTable, ofCodes[i], ofTargetStates[i + 1],
        ofStatesForSymbol, ofTableSize);
      mlTargetStates[i] = FindSourceState(mlTable, mlCodes[i], mlTargetStates[i + 1],
        mlStatesForSymbol, mlTableSize);
    }

    // Build the bitstream. Bits are accumulated from LSB upward (no reversal).
    // The BackwardBitReader reads from the highest bit (sentinel) downward.
    //
    // Decoder reads from top (MSB) to bottom (LSB):
    //   1. LL initial state, OF initial state, ML initial state
    //   2. For each sequence 0..N-1:
    //      a. OF extra bits, ML extra bits, LL extra bits
    //      b. If not last: LL state bits, ML state bits, OF state bits
    //
    // In the accumulator (LSB = bottom, added first):
    //   Bottom: what the decoder reads LAST
    //   Top:    what the decoder reads FIRST (initial states) + sentinel
    //
    // So we add in reverse order of the decoder's read sequence:

    var acc = new BitAccumulator();

    // 1. Start with what the decoder reads LAST: extra bits of the last sequence
    //    and state transitions, working from last seq backward.
    for (var i = sequences.Length - 1; i >= 0; --i) {
      // For non-first sequences, the decoder reads state transition bits
      // AFTER the previous sequence's extra bits. In reverse (bottom-up) these
      // come BEFORE the previous sequence's extra bits.
      if (i < sequences.Length - 1) {
        // State transition bits for going from seq i to seq i+1
        // Decoder reads: LL bits, ML bits, OF bits (in that order, from top to bottom)
        // We add in reverse: OF bits, ML bits, LL bits (bottom to top)
        var sourceOf = ofTargetStates[i];
        var ofTransBits = ofTable.NumBits[sourceOf];
        if (ofTransBits > 0) {
          var ofVal = ofTargetStates[i + 1] - ofTable.NewStateBase[sourceOf];
          acc.AddBits(ofVal, ofTransBits);
        }

        var sourceMl = mlTargetStates[i];
        var mlTransBits = mlTable.NumBits[sourceMl];
        if (mlTransBits > 0) {
          var mlVal = mlTargetStates[i + 1] - mlTable.NewStateBase[sourceMl];
          acc.AddBits(mlVal, mlTransBits);
        }

        var sourceLL = llTargetStates[i];
        var llTransBits = llTable.NumBits[sourceLL];
        if (llTransBits > 0) {
          var llVal = llTargetStates[i + 1] - llTable.NewStateBase[sourceLL];
          acc.AddBits(llVal, llTransBits);
        }
      }

      // Extra bits for this sequence
      // Decoder reads: OF extra, ML extra, LL extra (top to bottom)
      // We add in reverse: LL extra, ML extra, OF extra (bottom to top)
      if (llExtraNBits[i] > 0)
        acc.AddBits(llExtraVals[i], llExtraNBits[i]);
      if (mlExtraNBits[i] > 0)
        acc.AddBits(mlExtraVals[i], mlExtraNBits[i]);
      if (ofExtraNBits[i] > 0)
        acc.AddBits(ofExtraVals[i], ofExtraNBits[i]);
    }

    // 2. Initial states (decoder reads first, so we add last)
    // Decoder reads: LL, OF, ML (top to bottom)
    // We add in reverse: ML, OF, LL (bottom to top)
    acc.AddBits(mlTargetStates[0], mlTable.TableLog);
    acc.AddBits(ofTargetStates[0], ofTable.TableLog);
    acc.AddBits(llTargetStates[0], llTable.TableLog);

    // 3. Sentinel bit at the very top
    acc.AddBits(1, 1);

    return acc.ToByteArray();
  }

  /// <summary>
  /// Builds a mapping from symbol value to sorted list of decoder states that output that symbol.
  /// </summary>
  private static List<int>[] BuildSymbolStates(FseTable table, int tableSize) {
    // Find max symbol
    var maxSymbol = 0;
    for (var s = 0; s < tableSize; ++s) {
      if (table.Symbol[s] > maxSymbol)
        maxSymbol = table.Symbol[s];
    }

    var result = new List<int>[maxSymbol + 1];
    for (var i = 0; i <= maxSymbol; ++i)
      result[i] = [];

    for (var s = 0; s < tableSize; ++s)
      result[table.Symbol[s]].Add(s);

    return result;
  }

  /// <summary>
  /// Finds a source state for the given symbol such that a valid transition exists
  /// from this state to the target state of the next sequence.
  /// </summary>
  private static int FindSourceState(FseTable table, byte symbol, int nextTargetState,
    List<int>[] statesForSymbol, int tableSize) {
    // We need a state S such that:
    // 1. table.Symbol[S] == symbol
    // 2. nextTargetState is reachable: nextTargetState = NewStateBase[S] + bits,
    //    where 0 <= bits < (1 << NumBits[S])
    foreach (var s in statesForSymbol[symbol]) {
      var nbBits = table.NumBits[s];
      var baseVal = table.NewStateBase[s];
      var range = 1 << nbBits;
      var bitsNeeded = nextTargetState - baseVal;
      if (bitsNeeded >= 0 && bitsNeeded < range)
        return s;
    }

    // Fallback: just use the first available state for this symbol
    return statesForSymbol[symbol][0];
  }

  /// <summary>
  /// Resolves a raw offset code to an actual offset using repeat offset logic.
  /// </summary>
  private static int ResolveOffset(int encodedOffset, int litLength, int[] repeatOffsets) {
    int offset;

    if (encodedOffset > 3) {
      offset = encodedOffset - 3;
      repeatOffsets[2] = repeatOffsets[1];
      repeatOffsets[1] = repeatOffsets[0];
      repeatOffsets[0] = offset;
    }
    else if (litLength > 0) {
      switch (encodedOffset) {
        case 1:
          offset = repeatOffsets[0];
          break;
        case 2:
          offset = repeatOffsets[1];
          repeatOffsets[1] = repeatOffsets[0];
          repeatOffsets[0] = offset;
          break;
        case 3:
          offset = repeatOffsets[2];
          repeatOffsets[2] = repeatOffsets[1];
          repeatOffsets[1] = repeatOffsets[0];
          repeatOffsets[0] = offset;
          break;
        default:
          offset = 1;
          break;
      }
    }
    else {
      switch (encodedOffset) {
        case 1:
          offset = repeatOffsets[1];
          repeatOffsets[1] = repeatOffsets[0];
          repeatOffsets[0] = offset;
          break;
        case 2:
          offset = repeatOffsets[2];
          repeatOffsets[2] = repeatOffsets[1];
          repeatOffsets[1] = repeatOffsets[0];
          repeatOffsets[0] = offset;
          break;
        case 3:
          offset = repeatOffsets[0] - 1;
          if (offset <= 0) offset = 1;
          repeatOffsets[2] = repeatOffsets[1];
          repeatOffsets[1] = repeatOffsets[0];
          repeatOffsets[0] = offset;
          break;
        default:
          offset = 1;
          break;
      }
    }

    return offset;
  }

  /// <summary>
  /// Encodes an actual offset into the repeat-offset-aware encoded form.
  /// </summary>
  private static int EncodeOffset(int actualOffset, int litLength, int[] repeatOffsets) {
    if (litLength > 0) {
      if (actualOffset == repeatOffsets[0])
        return 1;
      if (actualOffset == repeatOffsets[1]) {
        repeatOffsets[1] = repeatOffsets[0];
        repeatOffsets[0] = actualOffset;
        return 2;
      }
      if (actualOffset == repeatOffsets[2]) {
        repeatOffsets[2] = repeatOffsets[1];
        repeatOffsets[1] = repeatOffsets[0];
        repeatOffsets[0] = actualOffset;
        return 3;
      }
    }

    repeatOffsets[2] = repeatOffsets[1];
    repeatOffsets[1] = repeatOffsets[0];
    repeatOffsets[0] = actualOffset;
    return actualOffset + 3;
  }

  /// <summary>
  /// Finds the total number of valid data bits (position of sentinel bit).
  /// </summary>
  private static int FindTotalBits(ReadOnlySpan<byte> data) {
    var lastByteIndex = data.Length - 1;
    while (lastByteIndex > 0 && data[lastByteIndex] == 0)
      --lastByteIndex;

    if (data[lastByteIndex] == 0)
      throw new InvalidDataException("No sentinel bit found in sequence bitstream.");

    var highBit = BitOperations.Log2((uint)data[lastByteIndex]);
    return lastByteIndex * 8 + highBit;
  }

  /// <summary>
  /// Reads bits from the MSB end of a backward bitstream.
  /// </summary>
  private ref struct BackwardBitReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPos;

    /// <summary>
    /// Initializes the backward bit reader.
    /// </summary>
    public BackwardBitReader(ReadOnlySpan<byte> data, int totalBits) {
      this._data = data;
      this._bitPos = totalBits - 1;
    }

    /// <summary>
    /// Reads the specified number of bits from the top of the remaining bitstream.
    /// </summary>
    public int ReadBits(int nbBits) {
      if (nbBits == 0) return 0;

      var value = 0;
      for (var i = nbBits - 1; i >= 0; --i) {
        if (this._bitPos < 0) return value;
        var byteIdx = this._bitPos >> 3;
        var bitIdx = this._bitPos & 7;
        var bit = (this._data[byteIdx] >> bitIdx) & 1;
        value |= bit << i;
        --this._bitPos;
      }

      return value;
    }
  }

  /// <summary>
  /// Accumulates bits in LSB-first order for building the backward bitstream.
  /// </summary>
  private sealed class BitAccumulator {
    private readonly List<byte> _bytes = [];
    private ulong _buffer;
    private int _count;

    /// <summary>
    /// Adds bits to the accumulator.
    /// </summary>
    /// <param name="value">The value whose low bits are added.</param>
    /// <param name="nbBits">The number of bits to add.</param>
    public void AddBits(int value, int nbBits) {
      this._buffer |= ((ulong)(uint)value & ((1UL << nbBits) - 1)) << this._count;
      this._count += nbBits;

      while (this._count >= 8) {
        this._bytes.Add((byte)(this._buffer & 0xFF));
        this._buffer >>= 8;
        this._count -= 8;
      }
    }

    /// <summary>
    /// Returns the byte array (no reversal). The sentinel bit is at the highest position.
    /// </summary>
    public byte[] ToByteArray() {
      if (this._count > 0) {
        this._bytes.Add((byte)(this._buffer & 0xFF));
        this._buffer = 0;
        this._count = 0;
      }

      return this._bytes.ToArray();
    }
  }
}
