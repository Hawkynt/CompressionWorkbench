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
    if (properties.Length < 5)
      throw new ArgumentException("Properties must be at least 5 bytes.", nameof(properties));

    this._input = input;
    this._uncompressedSize = uncompressedSize;

    // Parse properties byte
    int d = properties[0];
    if (d >= 9 * 5 * 5)
      throw new InvalidDataException("Invalid LZMA properties byte.");

    this._lc = d % 9;
    d /= 9;
    this._lp = d % 5;
    this._pb = d / 5;

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
    var decoder = new RangeDecoder(this._input);
    var literalDecoder = new LzmaLiteralDecoder(this._lc, this._lp);
    var matchLenDecoder = new LzmaLengthDecoder();
    var repLenDecoder = new LzmaLengthDecoder();

    // State variables
    int state = 0;
    int[] reps = [0, 0, 0, 0];

    // Probability arrays
    int[] isMatch = new int[LzmaConstants.NumStates << 4];
    int[] isRep = new int[LzmaConstants.NumStates];
    int[] isRepG0 = new int[LzmaConstants.NumStates];
    int[] isRepG1 = new int[LzmaConstants.NumStates];
    int[] isRepG2 = new int[LzmaConstants.NumStates];
    int[] isRep0Long = new int[LzmaConstants.NumStates << 4];
    Array.Fill(isMatch, RangeEncoder.ProbInitValue);
    Array.Fill(isRep, RangeEncoder.ProbInitValue);
    Array.Fill(isRepG0, RangeEncoder.ProbInitValue);
    Array.Fill(isRepG1, RangeEncoder.ProbInitValue);
    Array.Fill(isRepG2, RangeEncoder.ProbInitValue);
    Array.Fill(isRep0Long, RangeEncoder.ProbInitValue);

    // Distance decoding
    var posSlotDecoder = new BitTreeDecoder[LzmaConstants.NumLenToPosStates];
    for (int i = 0; i < LzmaConstants.NumLenToPosStates; ++i)
      posSlotDecoder[i] = new BitTreeDecoder(6);

    int[] posDecoders = new int[LzmaConstants.NumFullDistances - LzmaConstants.StartPosModelIndex];
    Array.Fill(posDecoders, RangeEncoder.ProbInitValue);

    var alignDecoder = new BitTreeDecoder(LzmaConstants.NumAlignBits);

    // Dictionary window
    int winSize = Math.Max(this._dictionarySize, 1);
    if (winSize < 4096)
      winSize = 4096;

    var window = new SlidingWindow(winSize);

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
              byte b = window.GetByte(reps[0] + 1);
              output.WriteByte(b);
              window.WriteByte(b);
              prevByte = b;
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
        byte[] copyBuf = new byte[len];
        window.CopyFromWindow(actualDist, len, copyBuf);
        output.Write(copyBuf, 0, len);
        prevByte = copyBuf[len - 1];
        outPos += len;
      }
    }
  }

  private static int DecodeDistance(RangeDecoder decoder,
    BitTreeDecoder[] posSlotDecoder, int[] posDecoders,
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
