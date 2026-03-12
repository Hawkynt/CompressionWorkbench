using Compression.Core.Dictionary.MatchFinders;
using Compression.Core.Entropy.RangeCoding;

namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// LZMA encoder implementing the full LZMA1 compression algorithm.
/// </summary>
public sealed class LzmaEncoder {
  private readonly int _lc;
  private readonly int _lp;
  private readonly int _pb;
  private readonly int _dictionarySize;
  private readonly int _posStateMask;

  /// <summary>
  /// Gets the 5-byte LZMA properties header (1 byte properties + 4 bytes dictionary size).
  /// </summary>
  public byte[] Properties { get; }

  /// <summary>
  /// Initializes a new LZMA encoder.
  /// </summary>
  /// <param name="dictionarySize">The dictionary size in bytes.</param>
  /// <param name="lc">Literal context bits (0-8). Default 3.</param>
  /// <param name="lp">Literal position bits (0-4). Default 0.</param>
  /// <param name="pb">Position bits (0-4). Default 2.</param>
  public LzmaEncoder(int dictionarySize = 1 << 23, int lc = 3, int lp = 0, int pb = 2) {
    this._lc = lc;
    this._lp = lp;
    this._pb = pb;
    this._dictionarySize = dictionarySize;
    this._posStateMask = (1 << pb) - 1;

    // Build 5-byte properties header
    this.Properties = new byte[5];
    this.Properties[0] = (byte)((pb * 5 + lp) * 9 + lc);
    // TODO: write 64 bits at once
    this.Properties[1] = (byte)dictionarySize;
    this.Properties[2] = (byte)(dictionarySize >> 8);
    this.Properties[3] = (byte)(dictionarySize >> 16);
    this.Properties[4] = (byte)(dictionarySize >> 24);
  }

  /// <summary>
  /// Encodes the given data to the output stream.
  /// Writes LZMA-compressed data (without the properties header or uncompressed size).
  /// </summary>
  /// <param name="output">The output stream.</param>
  /// <param name="data">The data to compress.</param>
  /// <param name="writeEndMarker">Whether to write the end-of-stream marker.</param>
  public void Encode(Stream output, ReadOnlySpan<byte> data, bool writeEndMarker = true) => this.Encode(output, data, 0, writeEndMarker);

  /// <summary>
  /// Encodes data starting at <paramref name="startOffset"/> within <paramref name="data"/>.
  /// Bytes before <paramref name="startOffset"/> serve as historical context for the match finder,
  /// enabling cross-chunk back-references in LZMA2.
  /// </summary>
  /// <param name="output">The output stream.</param>
  /// <param name="data">The data including optional historical prefix.</param>
  /// <param name="startOffset">The position in <paramref name="data"/> where actual encoding begins.</param>
  /// <param name="writeEndMarker">Whether to write the end-of-stream marker.</param>
  internal void Encode(Stream output, ReadOnlySpan<byte> data, int startOffset, bool writeEndMarker = true) {
    var encoder = new RangeEncoder(output);
    var literalEncoder = new LzmaLiteralEncoder(this._lc, this._lp);
    var matchLenEncoder = new LzmaLengthEncoder();
    var repLenEncoder = new LzmaLengthEncoder();

    // State variables
    var state = 0;
    int[] reps = [0, 0, 0, 0];

    // Probability arrays (stackalloc: 432 ints = 1,728 bytes total)
    Span<int> isMatch = stackalloc int[LzmaConstants.NumStates << 4];
    Span<int> isRep = stackalloc int[LzmaConstants.NumStates];
    Span<int> isRepG0 = stackalloc int[LzmaConstants.NumStates];
    Span<int> isRepG1 = stackalloc int[LzmaConstants.NumStates];
    Span<int> isRepG2 = stackalloc int[LzmaConstants.NumStates];
    Span<int> isRep0Long = stackalloc int[LzmaConstants.NumStates << 4];
    isMatch.Fill(RangeEncoder.ProbInitValue);
    isRep.Fill(RangeEncoder.ProbInitValue);
    isRepG0.Fill(RangeEncoder.ProbInitValue);
    isRepG1.Fill(RangeEncoder.ProbInitValue);
    isRepG2.Fill(RangeEncoder.ProbInitValue);
    isRep0Long.Fill(RangeEncoder.ProbInitValue);

    // Distance encoding
    var posSlotEncoder = new BitTreeEncoder[LzmaConstants.NumLenToPosStates];
    for (var i = 0; i < LzmaConstants.NumLenToPosStates; ++i)
      posSlotEncoder[i] = new(6);

    Span<int> posEncoders = stackalloc int[LzmaConstants.NumFullDistances - LzmaConstants.StartPosModelIndex];
    posEncoders.Fill(RangeEncoder.ProbInitValue);

    var alignEncoder = new BitTreeEncoder(LzmaConstants.NumAlignBits);

    // Match finder
    var windowSize = Math.Min(this._dictionarySize, data.Length > 0 ? data.Length : 1);
    var matchFinder = new HashChainMatchFinder(Math.Max(windowSize, 4096), 64);

    // Pre-seed the match finder with historical context positions
    for (var h = 0; h < startOffset; ++h)
      matchFinder.InsertPosition(data, h);

    var pos = startOffset;

    while (pos < data.Length) {
      var posState = pos & this._posStateMask;
      var prevByte = pos > 0 ? data[pos - 1] : (byte)0;

      // Try rep matches first
      var bestRepLen = 0;
      var bestRepIndex = 0;
      for (var rep = 0; rep < LzmaConstants.NumRepDistances; ++rep) {
        if (reps[rep] >= pos)
          continue;

        var dist = reps[rep] + 1;
        var len = 0;
        var maxLen = Math.Min(LzmaConstants.MatchMaxLen, data.Length - pos);
        while (len < maxLen && data[pos - dist + len] == data[pos + len])
          ++len;

        if (len < LzmaConstants.MatchMinLen || len <= bestRepLen)
          continue;

        bestRepLen = len;
        bestRepIndex = rep;
      }

      // Try normal match
      var match = matchFinder.FindMatch(data, pos,
        Math.Min(this._dictionarySize, pos),
        Math.Min(LzmaConstants.MatchMaxLen, data.Length - pos),
        LzmaConstants.MatchMinLen);

      // Decision: rep match, normal match, or literal
      if (bestRepLen >= LzmaConstants.MatchMinLen &&
        (bestRepLen >= match.Length || bestRepLen >= 3)) {
        // Encode rep match
        encoder.EncodeBit(ref isMatch[(state << 4) + posState], 1);
        encoder.EncodeBit(ref isRep[state], 1);

        if (bestRepIndex == 0) {
          encoder.EncodeBit(ref isRepG0[state], 0);
          if (bestRepLen == 1) {
            encoder.EncodeBit(ref isRep0Long[(state << 4) + posState], 0);
            state = LzmaConstants.StateUpdateShortRep(state);

            // Insert positions for match finder
            for (var i = 1; i < 1; ++i)
              matchFinder.InsertPosition(data, pos + i);

            pos += 1;
            continue;
          }

          encoder.EncodeBit(ref isRep0Long[(state << 4) + posState], 1);
        } else {
          encoder.EncodeBit(ref isRepG0[state], 1);
          if (bestRepIndex == 1)
            encoder.EncodeBit(ref isRepG1[state], 0);
          else {
            encoder.EncodeBit(ref isRepG1[state], 1);
            encoder.EncodeBit(ref isRepG2[state], bestRepIndex - 2);
          }

          // Shift rep distances
          var dist = reps[bestRepIndex];
          for (var i = bestRepIndex; i > 0; --i)
            reps[i] = reps[i - 1];

          reps[0] = dist;
        }

        if (bestRepLen > 1) {
          repLenEncoder.Encode(encoder, bestRepLen, posState);
          state = LzmaConstants.StateUpdateRep(state);
        }

        // Insert skipped positions
        for (var i = 1; i < bestRepLen; ++i)
          matchFinder.InsertPosition(data, pos + i);

        pos += bestRepLen;
      }
      else if (match.Length >= LzmaConstants.MatchMinLen) {
        // Encode normal match
        encoder.EncodeBit(ref isMatch[(state << 4) + posState], 1);
        encoder.EncodeBit(ref isRep[state], 0);

        matchLenEncoder.Encode(encoder, match.Length, posState);

        var distance = match.Distance - 1; // 0-based
        EncodeDistance(encoder, posSlotEncoder, posEncoders, alignEncoder, distance, match.Length);

        // Update rep distances
        for (var i = LzmaConstants.NumRepDistances - 1; i > 0; --i)
          reps[i] = reps[i - 1];

        reps[0] = distance;

        state = LzmaConstants.StateUpdateMatch(state);

        for (var i = 1; i < match.Length; ++i)
          matchFinder.InsertPosition(data, pos + i);

        pos += match.Length;
      } else {
        // Encode literal
        encoder.EncodeBit(ref isMatch[(state << 4) + posState], 0);

        var matchByte = (pos > 0 && reps[0] < pos) ? data[pos - reps[0] - 1] : (byte)0;
        literalEncoder.Encode(encoder, state, data[pos], matchByte, pos, prevByte);
        state = LzmaConstants.StateUpdateLiteral(state);
        ++pos;
      }
    }

    if (writeEndMarker) {
      // Write end marker: match with distance = 0xFFFFFFFF
      var posState = pos & this._posStateMask;
      encoder.EncodeBit(ref isMatch[(state << 4) + posState], 1);
      encoder.EncodeBit(ref isRep[state], 0);
      matchLenEncoder.Encode(encoder, LzmaConstants.MatchMinLen, posState);
      EncodeDistance(encoder, posSlotEncoder, posEncoders, alignEncoder, 0xFFFFFFFF, LzmaConstants.MatchMinLen);
    }

    encoder.Finish();
  }

  private static void EncodeDistance(RangeEncoder encoder,
    BitTreeEncoder[] posSlotEncoder, Span<int> posEncoders,
    BitTreeEncoder alignEncoder, long distance, int length) {
    var lenToPosState = LzmaConstants.GetLenToPosState(length);

    if (distance < LzmaConstants.NumFullDistances) {
      var posSlot = GetPosSlot((int)distance);
      posSlotEncoder[lenToPosState].Encode(encoder, posSlot);

      if (posSlot < LzmaConstants.StartPosModelIndex)
        return;

      var footerBits = (posSlot >> 1) - 1;
      var baseVal = (2 | (posSlot & 1)) << footerBits;
      var posReduced = (int)distance - baseVal;
      BitTreeEncoder.ReverseEncode(encoder, posEncoders, baseVal - posSlot - 1, footerBits, posReduced);
    } else {
      var posSlot = distance >= 0xFFFFFFFF ? 63 : GetPosSlot((int)distance);
      posSlotEncoder[lenToPosState].Encode(encoder, posSlot);

      var footerBits = (posSlot >> 1) - 1;
      var baseVal = (2 | (posSlot & 1)) << footerBits;
      var posReduced = (int)((uint)distance - (uint)baseVal);

      if (posSlot >= LzmaConstants.EndPosModelIndex) {
        var directBits = footerBits - LzmaConstants.NumAlignBits;
        encoder.EncodeDirectBits(posReduced >> LzmaConstants.NumAlignBits, directBits);
        alignEncoder.ReverseEncode(encoder, posReduced & (LzmaConstants.AlignTableSize - 1));
      } else
        BitTreeEncoder.ReverseEncode(encoder, posEncoders, baseVal - posSlot - 1, footerBits, posReduced);
    }
  }

  private static int GetPosSlot(int distance) {
    if (distance < 4)
      return distance;

    var bitCount = 31 - int.LeadingZeroCount(distance);
    return (bitCount << 1) + ((distance >> (bitCount - 1)) & 1);
  }
}
