using Compression.Core.Dictionary.MatchFinders;
using Compression.Core.Entropy.RangeCoding;
using Compression.Core.Simd;

namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// LZMA1 encoding of a stream of any length with bounded memory: the same greedy parse, hash-chain
/// match finder and range coder as <see cref="LzmaEncoder"/>, run over a sliding window that holds
/// one dictionary of history and a little look-ahead. For the same input and properties the output
/// is byte-identical to <see cref="LzmaEncoder.Encode(Stream, ReadOnlySpan{byte}, bool)"/>.
/// </summary>
/// <remarks>
/// Positions are absolute stream offsets; the window is addressed relative to its start. Every
/// decision the in-memory encoder makes looks at most one dictionary back (match candidates,
/// rep distances, the match byte) and at most <see cref="LzmaConstants.MatchMaxLen"/> bytes ahead,
/// so a window that keeps one dictionary behind the current position and
/// <see cref="LookAhead"/> bytes in front of it reproduces every decision.
/// </remarks>
public sealed class LzmaStreamEncoder {
  /// <summary>Bytes the window keeps in front of the position being encoded (longest match plus the hash and insert reach).</summary>
  private const int LookAhead = LzmaConstants.MatchMaxLen + 3;

  /// <summary>Bytes read from the input per refill.</summary>
  private const int ReadChunk = 1 << 20;

  private const int HashBits = 15;
  private const int HashSize = 1 << HashBits;
  private const int HashMask = HashSize - 1;

  private readonly int _lc;
  private readonly int _lp;
  private readonly int _pb;
  private readonly int _dictionarySize;
  private readonly int _posStateMask;
  private readonly LzmaCompressionLevel _level;

  /// <summary>Gets the 5-byte LZMA properties header (1 byte properties + 4 bytes dictionary size).</summary>
  public byte[] Properties { get; }

  /// <summary>Initializes a streaming LZMA encoder with the parameters <see cref="LzmaEncoder"/> takes.</summary>
  public LzmaStreamEncoder(int dictionarySize = 1 << 23, int lc = 3, int lp = 0, int pb = 2,
      LzmaCompressionLevel level = LzmaCompressionLevel.Normal) {
    var reference = new LzmaEncoder(dictionarySize, lc, lp, pb, level); // validates and builds the properties
    this._lc = lc;
    this._lp = lp;
    this._pb = pb;
    this._dictionarySize = dictionarySize;
    this._posStateMask = (1 << pb) - 1;
    this._level = level;
    this.Properties = reference.Properties;
  }

  /// <summary>
  /// Encodes <paramref name="length"/> bytes of <paramref name="input"/> into
  /// <paramref name="output"/> (no properties header, no size).
  /// </summary>
  /// <exception cref="EndOfStreamException">The input ends before <paramref name="length"/> bytes.</exception>
  public void Encode(Stream output, Stream input, long length, bool writeEndMarker = false) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(input);
    ArgumentOutOfRangeException.ThrowIfNegative(length);

    var encoder = new RangeEncoder(output);
    var literalEncoder = new LzmaLiteralEncoder(this._lc, this._lp);
    var matchLenEncoder = new LzmaLengthEncoder();
    var repLenEncoder = new LzmaLengthEncoder();

    var state = 0;
    long[] reps = [0, 0, 0, 0];
    var isMatch = Probabilities(LzmaConstants.NumStates << 4);
    var isRep = Probabilities(LzmaConstants.NumStates);
    var isRepG0 = Probabilities(LzmaConstants.NumStates);
    var isRepG1 = Probabilities(LzmaConstants.NumStates);
    var isRepG2 = Probabilities(LzmaConstants.NumStates);
    var isRep0Long = Probabilities(LzmaConstants.NumStates << 4);
    var posSlotEncoder = new BitTreeEncoder[LzmaConstants.NumLenToPosStates];
    for (var i = 0; i < LzmaConstants.NumLenToPosStates; ++i)
      posSlotEncoder[i] = new(6);
    var posEncoders = Probabilities(LzmaConstants.NumFullDistances - LzmaConstants.StartPosModelIndex);
    var alignEncoder = new BitTreeEncoder(LzmaConstants.NumAlignBits);

    // The match finder the in-memory encoder builds for this input.
    var windowSize = (int)Math.Min(this._dictionarySize, length > 0 ? length : 1);
    var chainDepth = this._level switch {
      LzmaCompressionLevel.Fast => 16,
      LzmaCompressionLevel.Best => 256,
      _ => 64,
    };
    var finder = new SlidingHashChain(Math.Max(windowSize, 4096), chainDepth);

    var window = new Window(input, length, this._dictionarySize);
    long pos = 0;

    while (pos < length) {
      window.EnsureAhead(pos, LookAhead);
      var data = window.Data;
      var rel = (int)(pos - window.Base);
      var posState = (int)(pos & this._posStateMask);
      var prevByte = pos > 0 ? data[rel - 1] : (byte)0;

      var bestRepLen = 0;
      var bestRepIndex = 0;
      for (var rep = 0; rep < LzmaConstants.NumRepDistances; ++rep) {
        if (reps[rep] >= pos)
          continue;

        var dist = (int)reps[rep] + 1;
        var len = 0;
        var maxLen = (int)Math.Min(LzmaConstants.MatchMaxLen, length - pos);
        while (len < maxLen && data[rel - dist + len] == data[rel + len])
          ++len;

        if (len < LzmaConstants.MatchMinLen || len <= bestRepLen)
          continue;

        bestRepLen = len;
        bestRepIndex = rep;
      }

      var match = finder.FindMatch(data, window.Base, pos,
        (int)Math.Min(this._dictionarySize, pos),
        (int)Math.Min(LzmaConstants.MatchMaxLen, length - pos),
        LzmaConstants.MatchMinLen);

      if (bestRepLen >= LzmaConstants.MatchMinLen && (bestRepLen >= match.Length || bestRepLen >= 3)) {
        encoder.EncodeBit(ref isMatch[(state << 4) + posState], 1);
        encoder.EncodeBit(ref isRep[state], 1);

        if (bestRepIndex == 0) {
          encoder.EncodeBit(ref isRepG0[state], 0);
          if (bestRepLen == 1) {
            encoder.EncodeBit(ref isRep0Long[(state << 4) + posState], 0);
            state = LzmaConstants.StateUpdateShortRep(state);
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

          var dist = reps[bestRepIndex];
          for (var i = bestRepIndex; i > 0; --i)
            reps[i] = reps[i - 1];

          reps[0] = dist;
        }

        if (bestRepLen > 1) {
          repLenEncoder.Encode(encoder, bestRepLen, posState);
          state = LzmaConstants.StateUpdateRep(state);
        }

        for (var i = 1; i < bestRepLen; ++i)
          finder.Insert(data, window.Base, pos + i);

        pos += bestRepLen;
      } else if (match.Length >= LzmaConstants.MatchMinLen) {
        encoder.EncodeBit(ref isMatch[(state << 4) + posState], 1);
        encoder.EncodeBit(ref isRep[state], 0);

        matchLenEncoder.Encode(encoder, match.Length, posState);

        var distance = match.Distance - 1;
        LzmaEncoder.EncodeDistance(encoder, posSlotEncoder, posEncoders, alignEncoder, distance, match.Length);

        for (var i = LzmaConstants.NumRepDistances - 1; i > 0; --i)
          reps[i] = reps[i - 1];

        reps[0] = distance;
        state = LzmaConstants.StateUpdateMatch(state);

        for (var i = 1; i < match.Length; ++i)
          finder.Insert(data, window.Base, pos + i);

        pos += match.Length;
      } else {
        encoder.EncodeBit(ref isMatch[(state << 4) + posState], 0);

        var matchByte = (pos > 0 && reps[0] < pos) ? data[(int)(rel - reps[0] - 1)] : (byte)0;
        literalEncoder.Encode(encoder, state, data[rel], matchByte, (int)pos, prevByte);
        state = LzmaConstants.StateUpdateLiteral(state);
        ++pos;
      }
    }

    if (writeEndMarker) {
      var posState = (int)(pos & this._posStateMask);
      encoder.EncodeBit(ref isMatch[(state << 4) + posState], 1);
      encoder.EncodeBit(ref isRep[state], 0);
      matchLenEncoder.Encode(encoder, LzmaConstants.MatchMinLen, posState);
      LzmaEncoder.EncodeDistance(encoder, posSlotEncoder, posEncoders, alignEncoder, 0xFFFFFFFF, LzmaConstants.MatchMinLen);
    }

    encoder.Finish();
  }

  private static int[] Probabilities(int count) {
    var probs = new int[count];
    probs.AsSpan().Fill(RangeEncoder.ProbInitValue);
    return probs;
  }

  /// <summary>
  /// The input as a window of absolute positions: at least one dictionary behind the current
  /// position stays in memory, and refills read ahead in large pieces.
  /// </summary>
  private sealed class Window(Stream input, long length, int history) {
    private readonly byte[] _buffer = new byte[(int)Math.Min((long)history + 2L * ReadChunk + LookAhead, Math.Max(length, 1))];
    private int _filled;

    /// <summary>The absolute position of <see cref="Data"/>[0].</summary>
    public long Base { get; private set; }

    /// <summary>The bytes in the window.</summary>
    public ReadOnlySpan<byte> Data => this._buffer.AsSpan(0, this._filled);

    /// <summary>Makes the window hold [<paramref name="pos"/>, <paramref name="pos"/> + <paramref name="ahead"/>), or up to the end.</summary>
    public void EnsureAhead(long pos, int ahead) {
      var wanted = Math.Min(pos + ahead, length);
      if (this.Base + this._filled >= wanted)
        return;

      // Slide: keep one dictionary of history behind pos, then read ahead into the room freed.
      var drop = (int)(Math.Max(this.Base, pos - history) - this.Base);
      if (drop > 0) {
        Buffer.BlockCopy(this._buffer, drop, this._buffer, 0, this._filled - drop);
        this._filled -= drop;
        this.Base += drop;
      }

      while (this.Base + this._filled < wanted) {
        var want = (int)Math.Min(this._buffer.Length - this._filled, length - (this.Base + this._filled));
        var read = input.Read(this._buffer, this._filled, want);
        if (read <= 0)
          throw new EndOfStreamException("LZMA input ended before its declared length.");
        this._filled += read;
      }
    }
  }

  /// <summary>
  /// <see cref="HashChainMatchFinder"/> over absolute positions: the same hash, chains and
  /// candidate order, so it finds the same matches the in-memory finder finds.
  /// </summary>
  private sealed class SlidingHashChain(int windowSize, int maxChainDepth) {
    private readonly long[] _head = CreateHead();
    private readonly long[] _prev = new long[windowSize];
    private readonly long _mask = windowSize - 1;

    private static long[] CreateHead() {
      var head = new long[HashSize];
      head.AsSpan().Fill(-1);
      return head;
    }

    private static int Hash(ReadOnlySpan<byte> data, int rel) => ((data[rel] << 10) ^ (data[rel + 1] << 5) ^ data[rel + 2]) & HashMask;

    public Match FindMatch(ReadOnlySpan<byte> data, long dataBase, long position, int maxDistance, int maxLength, int minLength) {
      var rel = (int)(position - dataBase);
      if (rel + 2 >= data.Length)
        return default;

      var bestDistance = 0;
      var bestLength = 0;
      var hash = Hash(data, rel);
      var candidate = this._head[hash];
      var chainCount = 0;
      var windowStart = Math.Max(0, position - maxDistance);

      while (candidate >= windowStart && chainCount < maxChainDepth) {
        if (candidate == position) {
          candidate = this._prev[candidate & this._mask];
          ++chainCount;
          continue;
        }
        var distance = (int)(position - candidate);
        var candRel = (int)(candidate - dataBase);
        var limit = Math.Min(maxLength, Math.Min(data.Length - rel, data.Length - candRel));
        if (bestLength == 0 || (bestLength < limit && data[candRel + bestLength] == data[rel + bestLength])) {
          var length = SimdMatchLength.GetMatchLength(data, candRel, rel, limit);
          if (length >= minLength && length > bestLength) {
            bestLength = length;
            bestDistance = distance;
            if (bestLength >= maxLength)
              break;
          }
        }

        candidate = this._prev[candidate & this._mask];
        if (candidate <= windowStart)
          break;
        ++chainCount;
      }

      this._prev[position & this._mask] = this._head[hash];
      this._head[hash] = position;
      return bestLength >= minLength ? new Match(bestDistance, bestLength) : default;
    }

    public void Insert(ReadOnlySpan<byte> data, long dataBase, long position) {
      var rel = (int)(position - dataBase);
      if (rel + 2 >= data.Length)
        return;
      var hash = Hash(data, rel);
      this._prev[position & this._mask] = this._head[hash];
      this._head[hash] = position;
    }
  }
}
