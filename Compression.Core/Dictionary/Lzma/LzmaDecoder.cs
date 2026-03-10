using Compression.Core.DataStructures;
using Compression.Core.Entropy.RangeCoding;

namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// LZMA decoder implementing the full LZMA1 decompression algorithm.
/// </summary>
public sealed class LzmaDecoder {
  private readonly int _lc;
  private readonly int _lp;
  private readonly int _pb;
  private readonly int _dictionarySize;
  private readonly int _posStateMask;

  private readonly Stream _input;
  private readonly long _uncompressedSize;

  /// <summary>
  /// Initializes a new LZMA decoder.
  /// </summary>
  /// <param name="input">The input stream containing LZMA-compressed data.</param>
  /// <param name="properties">The 5-byte LZMA properties header.</param>
  /// <param name="uncompressedSize">The expected uncompressed size, or -1 for end-marker termination.</param>
  public LzmaDecoder(Stream input, byte[] properties, long uncompressedSize = -1) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(properties);
    ArgumentOutOfRangeException.ThrowIfLessThan(properties.Length, 5, nameof(properties));

    this._input = input;
    this._uncompressedSize = uncompressedSize;

    // Parse properties byte
    int propByte = properties[0];
    if (propByte >= 9 * 5 * 5)
      throw new InvalidDataException("Invalid LZMA properties byte.");

    this._lc = propByte % 9;
    propByte /= 9;
    this._lp = propByte % 5;
    this._pb = propByte / 5;

    this._dictionarySize = properties[1] | (properties[2] << 8) | (properties[3] << 16) | (properties[4] << 24);
    if (this._dictionarySize < 0)
      this._dictionarySize = int.MaxValue;

    this._posStateMask = (1 << this._pb) - 1;
  }

  /// <summary>
  /// Decodes the entire compressed stream and returns the decompressed data.
  /// </summary>
  /// <returns>The decompressed data.</returns>
  public byte[] Decode() {
    using var output = new MemoryStream();
    Decode(output);
    return output.ToArray();
  }

  /// <summary>
  /// Decodes the compressed stream writing to the specified output stream.
  /// </summary>
  /// <param name="output">The output stream.</param>
  public void Decode(Stream output) {
    int winSize = Math.Max(this._dictionarySize, 1);
    if (winSize < 4096)
      winSize = 4096;

    var window = new SlidingWindow(winSize);
    Decode(output, window, null);
  }

  /// <summary>
  /// Decodes the compressed stream using a shared sliding window and rep distances.
  /// Used by LZMA2 for cross-chunk dictionary persistence.
  /// </summary>
  /// <param name="output">The output stream.</param>
  /// <param name="window">The shared sliding window.</param>
  /// <param name="reps">Rep distances to carry across chunks (4 elements), or null for fresh state.</param>
  internal void Decode(Stream output, SlidingWindow window, int[]? reps) {
    var decoder = new RangeDecoder(this._input);
    var literalDecoder = new LzmaLiteralDecoder(this._lc, this._lp);
    var matchLenDecoder = new LzmaLengthDecoder();
    var repLenDecoder = new LzmaLengthDecoder();

    // State variables
    int state = 0;
    reps ??= [0, 0, 0, 0];

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

    // Distance decoding
    var posSlotDecoder = new BitTreeDecoder[LzmaConstants.NumLenToPosStates];
    for (int i = 0; i < LzmaConstants.NumLenToPosStates; ++i)
      posSlotDecoder[i] = new BitTreeDecoder(6);

    Span<int> posDecoders = stackalloc int[LzmaConstants.NumFullDistances - LzmaConstants.StartPosModelIndex];
    posDecoders.Fill(RangeEncoder.ProbInitValue);

    // Reusable copy buffer (max match length = 273 bytes)
    Span<byte> copyBuf = stackalloc byte[LzmaConstants.MatchMaxLen];

    var alignDecoder = new BitTreeDecoder(LzmaConstants.NumAlignBits);

    long outPos = 0;
    byte prevByte = 0;

    while (this._uncompressedSize < 0 || outPos < this._uncompressedSize) {
      int posState = (int)(outPos & this._posStateMask);

      if (decoder.DecodeBit(ref isMatch[(state << 4) + posState]) == 0) {
        // Literal
        byte matchByte = window.Count > 0 && reps[0] < window.Count
          ? window.GetByte(reps[0] + 1)
          : (byte)0;
        byte lit = literalDecoder.Decode(decoder, state, matchByte, (int)outPos, prevByte);
        output.WriteByte(lit);
        window.WriteByte(lit);
        prevByte = lit;
        state = LzmaConstants.StateUpdateLiteral(state);
        ++outPos;
      } else {
        int len;
        int distance;

        if (decoder.DecodeBit(ref isRep[state]) == 0) {
          // Normal match
          len = matchLenDecoder.Decode(decoder, posState);
          state = LzmaConstants.StateUpdateMatch(state);

          distance = DecodeDistance(decoder, posSlotDecoder, posDecoders,
            alignDecoder, len);

          if (distance == unchecked((int)0xFFFFFFFF)) {
            // End marker
            break;
          }

          // Update rep distances
          for (int i = LzmaConstants.NumRepDistances - 1; i > 0; --i)
            reps[i] = reps[i - 1];

          reps[0] = distance;
        }
        else {
          // Rep match
          if (decoder.DecodeBit(ref isRepG0[state]) == 0) {
            // Rep0
            if (decoder.DecodeBit(ref isRep0Long[(state << 4) + posState]) == 0) {
              // Short rep (1 byte)
              state = LzmaConstants.StateUpdateShortRep(state);
              byte previousByte = window.GetByte(reps[0] + 1);
              output.WriteByte(previousByte);
              window.WriteByte(previousByte);
              prevByte = previousByte;
              outPos++;
              continue;
            }
            // else: long rep0 — distance stays reps[0]
          }
          else {
            int dist;
            if (decoder.DecodeBit(ref isRepG1[state]) == 0)
              dist = reps[1];
            else {
              if (decoder.DecodeBit(ref isRepG2[state]) == 0)
                dist = reps[2];
              else {
                dist = reps[3];
                reps[3] = reps[2];
              }
              reps[2] = reps[1];
            }
            reps[1] = reps[0];
            reps[0] = dist;
          }

          len = repLenDecoder.Decode(decoder, posState);
          state = LzmaConstants.StateUpdateRep(state);
          distance = reps[0];
        }

        // Copy from dictionary
        int actualDist = distance + 1;
        var copySlice = copyBuf.Slice(0, len);
        window.CopyFromWindow(actualDist, len, copySlice);
        output.Write(copySlice);
        prevByte = copySlice[len - 1];
        outPos += len;
      }
    }
  }

  private static int DecodeDistance(RangeDecoder decoder,
    BitTreeDecoder[] posSlotDecoder, Span<int> posDecoders,
    BitTreeDecoder alignDecoder, int length) {
    int lenToPosState = LzmaConstants.GetLenToPosState(length);
    int posSlot = posSlotDecoder[lenToPosState].Decode(decoder);

    if (posSlot < LzmaConstants.StartPosModelIndex)
      return posSlot;

    int numDirectBits = (posSlot >> 1) - 1;
    int result = (2 | (posSlot & 1)) << numDirectBits;

    if (posSlot < LzmaConstants.EndPosModelIndex) {
      result += BitTreeDecoder.ReverseDecode(decoder, posDecoders,
        result - posSlot - 1, numDirectBits);
    }
    else {
      int directBits = numDirectBits - LzmaConstants.NumAlignBits;
      result += decoder.DecodeDirectBits(directBits) << LzmaConstants.NumAlignBits;
      result += alignDecoder.ReverseDecode(decoder);
    }

    return result;
  }
}
