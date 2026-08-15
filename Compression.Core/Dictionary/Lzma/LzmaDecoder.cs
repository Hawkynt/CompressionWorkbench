using System.Diagnostics.CodeAnalysis;
using Compression.Core.DataStructures;
using Compression.Core.Entropy.RangeCoding;

namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// LZMA decoder implementing the full LZMA1 decompression algorithm.
/// </summary>
/// <remarks>
/// The complete coder state — probability model, 12-state machine, rep distances,
/// dictionary window and the uncompressed position counter — is held in fields so that
/// LZMA2 can carry it from chunk to chunk and reset exactly the parts a chunk asks for.
/// A single LZMA2 chunk is one self-contained range-coded unit, so the range decoder is
/// re-initialised for every chunk; everything else survives unless the chunk's control
/// byte says otherwise.
/// </remarks>
public sealed class LzmaDecoder {
  private int _lc;
  private int _lp;
  private int _pb;
  private int _posStateMask;
  private readonly int _dictionarySize;

  private readonly Stream? _input;
  private readonly long _uncompressedSize;

  // Probability model — survives across LZMA2 chunks unless a chunk requests a state reset.
  private readonly int[] _isMatch = new int[LzmaConstants.NumStates << 4];
  private readonly int[] _isRep = new int[LzmaConstants.NumStates];
  private readonly int[] _isRepG0 = new int[LzmaConstants.NumStates];
  private readonly int[] _isRepG1 = new int[LzmaConstants.NumStates];
  private readonly int[] _isRepG2 = new int[LzmaConstants.NumStates];
  private readonly int[] _isRep0Long = new int[LzmaConstants.NumStates << 4];
  private readonly int[] _posDecoders = new int[LzmaConstants.NumFullDistances - LzmaConstants.StartPosModelIndex];
  private readonly BitTreeDecoder[] _posSlotDecoder = new BitTreeDecoder[LzmaConstants.NumLenToPosStates];
  private readonly BitTreeDecoder _alignDecoder = new(LzmaConstants.NumAlignBits);
  private readonly LzmaLengthDecoder _matchLenDecoder = new();
  private readonly LzmaLengthDecoder _repLenDecoder = new();
  private LzmaLiteralDecoder _literalDecoder = new(0, 0);

  // Match state — likewise survives unless a chunk requests a state reset.
  private int _state;
  private readonly int[] _reps = new int[LzmaConstants.NumRepDistances];

  // Dictionary state — survives every chunk except an explicit dictionary reset.
  private SlidingWindow? _window;
  private long _processedPos;

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

    var dictionarySize = properties[1] | (properties[2] << 8) | (properties[3] << 16) | (properties[4] << 24);
    this._dictionarySize = dictionarySize < 0 ? int.MaxValue : dictionarySize;

    this.Initialize(properties[0]);
  }

  /// <summary>
  /// Initializes a new LZMA decoder for a raw stream whose coding parameters are known
  /// from the outside instead of from a properties header.
  /// </summary>
  /// <remarks>
  /// Embedders such as executable packers strip the 13-byte LZMA container and keep
  /// lc/lp/pb and the uncompressed size in a private header of their own, so the
  /// range-coded data starts right at the first byte of <paramref name="input"/>.
  /// </remarks>
  /// <param name="input">The stream positioned at the first range-coder byte.</param>
  /// <param name="literalContextBits">The number of literal context bits (0-8).</param>
  /// <param name="literalPositionBits">The number of literal position bits (0-4).</param>
  /// <param name="positionBits">The number of position bits (0-4).</param>
  /// <param name="dictionarySize">The dictionary size in bytes.</param>
  /// <param name="uncompressedSize">The expected uncompressed size, or -1 for end-marker termination.</param>
  public LzmaDecoder(Stream input, int literalContextBits, int literalPositionBits, int positionBits, int dictionarySize, long uncompressedSize = -1) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentOutOfRangeException.ThrowIfNegative(literalContextBits);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(literalContextBits, 8);
    ArgumentOutOfRangeException.ThrowIfNegative(literalPositionBits);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(literalPositionBits, 4);
    ArgumentOutOfRangeException.ThrowIfNegative(positionBits);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(positionBits, 4);
    ArgumentOutOfRangeException.ThrowIfLessThan(dictionarySize, 1);

    this._input = input;
    this._uncompressedSize = uncompressedSize;
    this._dictionarySize = dictionarySize;

    for (var i = 0; i < LzmaConstants.NumLenToPosStates; ++i)
      this._posSlotDecoder[i] = new(6);

    this.SetProperties(literalContextBits, literalPositionBits, positionBits);
    this.ResetState();
  }

  /// <summary>
  /// Initializes a new LZMA decoder for chunk-wise use by <see cref="Lzma2Decoder"/>.
  /// The properties arrive later with the first chunk that carries them.
  /// </summary>
  /// <param name="dictionarySize">The dictionary size in bytes.</param>
  internal LzmaDecoder(int dictionarySize) {
    this._dictionarySize = dictionarySize;
    this._uncompressedSize = -1;
    this.Initialize(0);
  }

  private void Initialize(byte propertiesByte) {
    for (var i = 0; i < LzmaConstants.NumLenToPosStates; ++i)
      this._posSlotDecoder[i] = new(6);

    this.SetProperties(propertiesByte);
    this.ResetState();
  }

  /// <summary>
  /// Decodes the entire compressed stream and returns the decompressed data.
  /// </summary>
  /// <returns>The decompressed data.</returns>
  public byte[] Decode() {
    using var output = new MemoryStream();
    this.Decode(output);
    return output.ToArray();
  }

  /// <summary>
  /// Decodes the compressed stream writing to the specified output stream.
  /// </summary>
  /// <param name="output">The output stream.</param>
  public void Decode(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (this._input == null)
      throw new InvalidOperationException("This decoder was created without an input stream.");

    this.EnsureWindow();
    this.DecodeCore(this._input, output, this._uncompressedSize);
  }

  /// <summary>
  /// Applies a new LZMA properties byte (lc/lp/pb), as carried by LZMA2 chunks with
  /// reset level 2 or 3.
  /// </summary>
  /// <param name="propertiesByte">The encoded (pb * 5 + lp) * 9 + lc value.</param>
  internal void ApplyProperties(byte propertiesByte) => this.SetProperties(propertiesByte);

  /// <summary>
  /// Resets the probability model, the 12-state machine and the rep distances,
  /// as requested by LZMA2 chunks with reset level 1 or higher. The dictionary and the
  /// uncompressed position counter are left untouched.
  /// </summary>
  internal void ResetState() {
    this._isMatch.AsSpan().Fill(RangeEncoder.ProbInitValue);
    this._isRep.AsSpan().Fill(RangeEncoder.ProbInitValue);
    this._isRepG0.AsSpan().Fill(RangeEncoder.ProbInitValue);
    this._isRepG1.AsSpan().Fill(RangeEncoder.ProbInitValue);
    this._isRepG2.AsSpan().Fill(RangeEncoder.ProbInitValue);
    this._isRep0Long.AsSpan().Fill(RangeEncoder.ProbInitValue);
    this._posDecoders.AsSpan().Fill(RangeEncoder.ProbInitValue);

    foreach (var posSlot in this._posSlotDecoder)
      posSlot.Reset();

    this._alignDecoder.Reset();
    this._matchLenDecoder.Reset();
    this._repLenDecoder.Reset();
    this._literalDecoder.Reset();

    this._state = 0;
    this._reps.AsSpan().Clear();
  }

  /// <summary>
  /// Discards the dictionary contents and restarts the uncompressed position counter,
  /// as requested by LZMA2 chunks with reset level 3 and by uncompressed chunks with
  /// control byte 0x01.
  /// </summary>
  internal void ResetDictionary() {
    this._window = null;
    this._processedPos = 0;
  }

  /// <summary>
  /// Decodes one LZMA2 chunk of range-coded data into the output stream, continuing the
  /// dictionary and whatever coder state the chunk's control byte did not reset.
  /// </summary>
  /// <param name="input">The chunk's packed bytes.</param>
  /// <param name="output">The output stream.</param>
  /// <param name="unpackedSize">The number of bytes this chunk produces.</param>
  internal void DecodeChunk(Stream input, Stream output, int unpackedSize) {
    this.EnsureWindow();
    this.DecodeCore(input, output, this._processedPos + unpackedSize);
  }

  /// <summary>
  /// Feeds the payload of an LZMA2 uncompressed chunk through the dictionary so that later
  /// chunks can reference it, and advances the uncompressed position counter.
  /// </summary>
  /// <param name="output">The output stream.</param>
  /// <param name="data">The literal chunk payload.</param>
  internal void WriteUncompressed(Stream output, ReadOnlySpan<byte> data) {
    this.EnsureWindow();
    output.Write(data);
    this._window.WriteBytes(data);
    this._processedPos += data.Length;
  }

  [MemberNotNull(nameof(LzmaDecoder._window))]
  private void EnsureWindow() => this._window ??= new(Math.Max(this._dictionarySize, 4096));

  private void SetProperties(byte propertiesByte) {
    int value = propertiesByte;
    if (value >= 9 * 5 * 5)
      throw new InvalidDataException("Invalid LZMA properties byte.");

    var lc = value % 9;
    value /= 9;
    var lp = value % 5;
    var pb = value / 5;

    this.SetProperties(lc, lp, pb);
  }

  private void SetProperties(int lc, int lp, int pb) {
    // lc/lp shape the literal sub-coder table, so only a change reshapes it. Its
    // probabilities need no clearing here: new properties always come with a state reset.
    if (lc != this._lc || lp != this._lp)
      this._literalDecoder = new(lc, lp);

    this._lc = lc;
    this._lp = lp;
    this._pb = pb;
    this._posStateMask = (1 << pb) - 1;
  }

  /// <summary>
  /// Runs the LZMA symbol loop until <paramref name="limit"/> uncompressed bytes have been
  /// produced since the last dictionary reset, or until the end marker when negative.
  /// </summary>
  private void DecodeCore(Stream input, Stream output, long limit) {
    var window = this._window!;
    var decoder = new RangeDecoder(input);

    var isMatch = this._isMatch.AsSpan();
    var isRep = this._isRep.AsSpan();
    var isRepG0 = this._isRepG0.AsSpan();
    var isRepG1 = this._isRepG1.AsSpan();
    var isRepG2 = this._isRepG2.AsSpan();
    var isRep0Long = this._isRep0Long.AsSpan();
    var posDecoders = this._posDecoders.AsSpan();
    var posSlotDecoder = this._posSlotDecoder;
    var alignDecoder = this._alignDecoder;
    var literalDecoder = this._literalDecoder;
    var reps = this._reps;

    var state = this._state;
    var outPos = this._processedPos;

    // The literal context byte is the byte before the current position; right after a
    // dictionary reset there is none and zero is used instead.
    var prevByte = outPos == 0 ? (byte)0 : window.GetByte(1);

    // Reusable copy buffer (max match length = 273 bytes)
    Span<byte> copyBuf = stackalloc byte[LzmaConstants.MatchMaxLen];

    while (limit < 0 || outPos < limit) {
      var posState = (int)(outPos & this._posStateMask);

      if (decoder.DecodeBit(ref isMatch[(state << 4) + posState]) == 0) {
        // Literal
        var matchByte = window.Count > 0 && reps[0] < window.Count
          ? window.GetByte(reps[0] + 1)
          : (byte)0;
        var lit = literalDecoder.Decode(decoder, state, matchByte, (int)outPos, prevByte);
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
          len = this._matchLenDecoder.Decode(decoder, posState);
          state = LzmaConstants.StateUpdateMatch(state);

          distance = DecodeDistance(decoder, posSlotDecoder, posDecoders,
            alignDecoder, len);

          if (distance == unchecked((int)0xFFFFFFFF))
            // End marker
            break;

          // Update rep distances
          for (var i = LzmaConstants.NumRepDistances - 1; i > 0; --i)
            reps[i] = reps[i - 1];

          reps[0] = distance;
        } else {
          // Rep match
          if (decoder.DecodeBit(ref isRepG0[state]) == 0) {
            // Rep0
            if (decoder.DecodeBit(ref isRep0Long[(state << 4) + posState]) == 0) {
              // Short rep (1 byte)
              state = LzmaConstants.StateUpdateShortRep(state);
              var previousByte = window.GetByte(reps[0] + 1);
              output.WriteByte(previousByte);
              window.WriteByte(previousByte);
              prevByte = previousByte;
              ++outPos;
              continue;
            }
            // else: long rep0 — distance stays reps[0]
          } else {
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

          len = this._repLenDecoder.Decode(decoder, posState);
          state = LzmaConstants.StateUpdateRep(state);
          distance = reps[0];
        }

        // Copy from dictionary
        var actualDist = distance + 1;
        var copySlice = copyBuf[..len];
        window.CopyFromWindow(actualDist, len, copySlice);
        output.Write(copySlice);
        prevByte = copySlice[len - 1];
        outPos += len;
      }
    }

    this._state = state;
    this._processedPos = outPos;
  }

  private static int DecodeDistance(RangeDecoder decoder,
    BitTreeDecoder[] posSlotDecoder, Span<int> posDecoders,
    BitTreeDecoder alignDecoder, int length) {
    var lenToPosState = LzmaConstants.GetLenToPosState(length);
    var posSlot = posSlotDecoder[lenToPosState].Decode(decoder);

    if (posSlot < LzmaConstants.StartPosModelIndex)
      return posSlot;

    var numDirectBits = (posSlot >> 1) - 1;
    var result = (2 | (posSlot & 1)) << numDirectBits;

    if (posSlot < LzmaConstants.EndPosModelIndex)
      result += BitTreeDecoder.ReverseDecode(decoder, posDecoders, result - posSlot - 1, numDirectBits);
    else {
      var directBits = numDirectBits - LzmaConstants.NumAlignBits;
      result += decoder.DecodeDirectBits(directBits) << LzmaConstants.NumAlignBits;
      result += alignDecoder.ReverseDecode(decoder);
    }

    return result;
  }
}
