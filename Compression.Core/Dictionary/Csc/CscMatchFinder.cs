using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// libcsc's match finder over the circular LZ window: single-candidate 2- and 3-byte hash heads,
/// an optional multi-way 6-byte hash table, and an optional LZMA-style binary tree that covers only
/// the most recent part of the window (its hash head reaches the whole window).
/// </summary>
/// <remarks>
/// Positions are kept as a monotonically growing 32-bit counter; a table entry stores the counter
/// value when the position was inserted, so <c>pos - entry</c> is the distance and a zeroed entry is
/// automatically out of range. All tables live in one array so a counter overflow can rebase them in
/// one pass.
/// </remarks>
internal sealed class CscMatchFinder {
  private const int Hash3Size = 64 * 1024;
  private const int Hash2Size = 16 * 1024;
  private const int CandidateLimit = 32;

  /// <summary>Longest distance worth taking for a match of length 0-6; longer matches have no limit.</summary>
  private static ReadOnlySpan<uint> ShortMatchMaxDistance => [0, 0, 64, 1024, 16 * 1024, 256 * 1024, 4 * 1024 * 1024];

  /// <summary>Shift applied to a distance when trading it against 0-3 bytes of length.</summary>
  private static ReadOnlySpan<byte> LengthTradeShift => [0, 4, 8, 12];

  private readonly byte[] _window;
  private readonly uint _windowSize;
  private readonly uint _validRange;
  private readonly uint[] _tables;
  private readonly int _hash6Offset;
  private readonly int _treeHeadOffset;
  private readonly int _treeNodesOffset;
  private readonly int _hashBits;
  private readonly uint _hashWidth;
  private readonly int _treeBits;
  private readonly uint _treeSize;
  private readonly bool _hasTree;
  private readonly CscMatch[] _candidates = new CscMatch[CandidateLimit];

  private uint _pos;
  private uint _treePos;
  private uint _treeCycles;
  private uint _hashCycles;
  private bool _useShortHashes;
  private uint _goodLength;

  public CscMatchFinder(byte[] window, uint windowSize, uint treeSize, int treeBits, int hashWidth, int hashBits) {
    this._window = window;
    this._windowSize = windowSize;
    this._validRange = windowSize - CscConstants.MinBlockSize - 4;
    this._pos = this._validRange;

    if (treeBits == 0 || treeSize == 0)
      (treeBits, treeSize) = (0, 0);
    if (hashBits == 0 || hashWidth == 0)
      (hashBits, hashWidth) = (0, 0);

    this._hashBits = hashBits;
    this._hashWidth = (uint)hashWidth;
    this._treeBits = treeBits;
    this._treeSize = treeSize;
    this._hasTree = treeBits != 0;

    var size = (long)Hash2Size + Hash3Size + ((long)hashWidth << hashBits);
    this._hash6Offset = Hash2Size + Hash3Size;
    this._treeHeadOffset = (int)size;
    if (this._hasTree) {
      size += 1L << treeBits;
      this._treeNodesOffset = (int)size;
      size += (long)treeSize * 2;
    }

    // libcsc's buffer carries slack past the tree; the node slot one past the end is written (never
    // read) when a long match leaves the tree cursor exactly at the end, so the slack is kept.
    this._tables = new uint[size + 16];
  }

  /// <summary>Sets search effort: tree steps, hash-table ways, whether 2/3-byte heads are used, good length.</summary>
  public void SetArguments(uint treeCycles, uint hashCycles, bool useShortHashes, uint goodLength) {
    this._treeCycles = treeCycles;
    this._hashCycles = hashCycles;
    this._useShortHashes = useShortHashes;
    this._goodLength = goodLength;
  }

  /// <summary>
  /// Inserts the positions <c>windowPosition + 1 .. windowPosition + length - 1</c> (the first one was
  /// inserted by the search that found the match). Long runs only refresh the 2/3-byte heads every
  /// fourth byte.
  /// </summary>
  public void SlidePosition(uint windowPosition, uint length, uint limit = uint.MaxValue) {
    var tables = this._tables;
    var window = this._window;
    uint lastHash6 = 0;
    for (uint i = 1; i < length;) {
      var wpos = windowPosition + i;
      if (this._pos >= 0xFFFFFFF0)
        this.Normalize();

      tables[(int)this.Hash2(wpos)] = this._pos;
      tables[Hash2Size + (int)this.Hash3(wpos)] = this._pos;

      if (i + 128 < length) {
        i += 4;
        this._pos += 4;
        this._treePos += 4;
        continue;
      }

      if (this._hashWidth != 0) {
        var h6 = this.Hash6(wpos, this._hashBits);
        var row = this._hash6Offset + (int)(h6 * this._hashWidth);
        if (h6 != lastHash6) {
          var ways = Math.Min(this._hashWidth, this._hashCycles);
          for (var j = (int)ways - 1; j > 0; --j)
            tables[row + j] = tables[row + j - 1];
        }

        tables[row] = this._pos;
        lastHash6 = h6;
      }

      if (!this._hasTree) {
        ++this._pos;
        ++i;
        continue;
      }

      var hashTree = this.Hash6(wpos, this._treeBits);
      if (this._treePos >= this._treeSize)
        this._treePos -= this._treeSize;
      var distance = this._pos - tables[this._treeHeadOffset + (int)hashTree];
      var left = this._treeNodesOffset + (int)(this._treePos * 2);
      var right = left + 1;
      uint leftLength = 0, rightLength = 0;

      for (uint cycle = 0; ; ++cycle) {
        if (cycle >= this._treeCycles || distance >= this._treeSize || distance >= this._validRange) {
          tables[left] = tables[right] = 0;
          break;
        }

        var comparePos = wpos >= distance ? wpos - distance : wpos + this._windowSize - distance;
        var length2 = Math.Min(leftLength, rightLength);
        var compareLimit = Math.Min(limit - i, this._windowSize - comparePos);
        if (length2 >= compareLimit) {
          tables[left] = tables[right] = 0;
          break;
        }

        var nodePos = this._treePos >= distance ? this._treePos - distance : this._treePos + this._treeSize - distance;
        var node = this._treeNodesOffset + (int)(nodePos * 2);
        if (window[wpos + length2] == window[comparePos + length2]) {
          var compareLimit2 = Math.Min(this._goodLength, compareLimit);
          ++length2;
          while (length2 < compareLimit2 && window[wpos + length2] == window[comparePos + length2])
            ++length2;

          if (length2 >= this._goodLength) {
            tables[left] = tables[node];
            tables[right] = tables[node + 1];
            break;
          }

          if (length2 >= compareLimit2) {
            tables[left] = tables[right] = 0;
            break;
          }
        }

        if (window[comparePos + length2] < window[wpos + length2]) {
          tables[left] = this._pos - distance;
          left = node + 1;
          distance = this._pos - tables[left];
          leftLength = length2;
        } else {
          tables[right] = this._pos - distance;
          right = node;
          distance = this._pos - tables[right];
          rightLength = length2;
        }
      }

      tables[this._treeHeadOffset + (int)hashTree] = this._pos;
      ++this._treePos;
      ++this._pos;
      ++i;
    }
  }

  /// <summary>Inserts a sixteenth of the positions of an uncompressed block, cheaply, so duplicates can still be found.</summary>
  public void SlidePositionFast(uint windowPosition, uint length) {
    var tables = this._tables;
    for (uint i = 0; i < length; ++i, ++this._pos) {
      var wpos = windowPosition + i;
      if (this._pos >= 0xFFFFFFF0)
        this.Normalize();

      if (this.Hash2(wpos) % 16 != 0) {
        if (++this._treePos >= this._treeSize)
          this._treePos -= this._treeSize;
        continue;
      }

      if (this._hashWidth != 0) {
        var row = this._hash6Offset + (int)(this.Hash6(wpos, this._hashBits) * this._hashWidth);
        for (var j = (int)this._hashWidth - 1; j > 0; --j)
          tables[row + j] = tables[row + j - 1];
        tables[row] = this._pos;
      }

      if (this._hasTree) {
        var h = this.Hash6(wpos, this._treeBits);
        var node = this._treeNodesOffset + (int)(this._treePos * 2);
        tables[node] = tables[node + 1] = 0;
        tables[this._treeHeadOffset + (int)h] = this._pos;
        if (++this._treePos >= this._treeSize)
          this._treePos -= this._treeSize;
      }
    }
  }

  /// <summary>Picks the most worthwhile candidate at <paramref name="windowPosition"/> for the greedy/lazy parser.</summary>
  public CscMatch FindMatch(ReadOnlySpan<uint> repeatDistances, uint windowPosition, uint limit) {
    var candidates = this._candidates;
    candidates[0] = new(1, 0);
    var n = this.FindCandidates(repeatDistances, windowPosition, limit);
    var best = 0;
    for (var i = 1; i <= n; ++i) {
      if (best == 0 || IsSecondBetter(candidates[best], candidates[i]))
        best = i;
    }

    return candidates[best];
  }

  /// <summary>
  /// Fills <paramref name="result"/> for the optimal parser: slot 0 receives the longest candidate,
  /// slot <c>n</c> (n ≥ 1) the cheapest way to code a match of length <c>n</c> from <paramref name="state"/>.
  /// </summary>
  public void FindMatchWithPrice(CscModel model, uint state, CscMatch[] result, ReadOnlySpan<uint> repeatDistances, uint windowPosition, uint limit) {
    var candidates = this._candidates;
    candidates[0] = new(1, 0);
    var n = this.FindCandidates(repeatDistances, windowPosition, limit);
    result[0] = candidates[n];
    if (result[0].Length >= this._goodLength)
      return;

    var bound = ShortMatchMaxDistance;
    result[1].Distance = 0;
    uint lengthPos = 1;
    for (var i = 1; i <= n; ++i) {
      var candidate = candidates[i];
      uint distancePrice;
      uint realDistance;
      if (candidate is { Length: 1, Distance: 1 }) {
        result[1].Price = model.GetRep0Len1Price(state);
        result[1].Distance = 1;
        continue;
      }

      if (candidate.Distance <= 4) {
        distancePrice = model.GetRepeatDistancePrice(state, candidate.Distance - 1);
        realDistance = 0;
      } else {
        distancePrice = model.GetMatchDistancePrice(state, candidate.Distance - 5);
        realDistance = candidate.Distance - 4;
      }

      while (lengthPos < candidate.Length) {
        ++lengthPos;
        if (lengthPos <= 6 && realDistance >= bound[(int)lengthPos]) {
          result[lengthPos].Distance = 0;
          continue;
        }

        result[lengthPos].Distance = candidate.Distance;
        result[lengthPos].Price = distancePrice + model.GetMatchLengthPrice(lengthPos - 2);
      }
    }
  }

  /// <summary>Whether the block at <paramref name="source"/> repeats something already in the window (sampled, ≥ 19 bytes).</summary>
  public bool TestFind(uint windowPosition, ReadOnlySpan<byte> source, int offset, uint limit) {
    Span<uint> distances = stackalloc uint[9];
    distances[0] = distances[1] = this._windowSize;
    var depth = 0;
    if (Hash2Of(source, offset) % 16 != 0)
      return false;

    if (this._hashWidth != 0) {
      // libcsc reads the first way of the row for every slot here, not ways 0..n-1.
      var h = Hash6Of(source, offset, this._hashBits);
      for (uint i = 0; i < this._hashWidth && i < 8; ++i)
        distances[depth++] = this._pos - this._tables[this._hash6Offset + (int)(h * this._hashWidth)];
    }

    if (this._hasTree)
      distances[depth++] = this._pos - this._tables[this._treeHeadOffset + (int)Hash6Of(source, offset, this._treeBits)];

    var window = this._window;
    for (var i = 0; i < depth; ++i) {
      var distance = distances[i];
      if (distance >= this._validRange)
        continue;

      var comparePos = windowPosition >= distance ? windowPosition - distance : windowPosition + this._windowSize - distance;
      var compareLimit = Math.Min(limit, this._windowSize - comparePos);
      var length = 0;
      while (length < compareLimit && source[offset + length] == window[comparePos + length])
        ++length;

      if (length > 18)
        return true;
    }

    return false;
  }

  /// <summary>libcsc's heuristic for whether <paramref name="second"/> (one byte later, or later in the list) beats <paramref name="first"/>.</summary>
  public static bool IsSecondBetter(CscMatch first, CscMatch second) {
    var shift = LengthTradeShift;
    return second.Length > 1 && (
      second.Length > first.Length + 3
      || (second.Length > first.Length && second.Distance <= 4)
      || (second.Length + 2 > first.Length && second.Distance <= 4 && first.Distance > 4)
      || (second.Length >= first.Length && (second.Distance >> shift[(int)(second.Length - first.Length)]) <= first.Distance)
      || (second.Length < first.Length && second.Length + 2 >= first.Length && first.Distance > 4
          && (first.Distance >> shift[(int)(first.Length - second.Length)]) > second.Distance));
  }

  /// <summary>
  /// Collects candidates of strictly increasing length into <c>_candidates[1..]</c> — repeat distances
  /// first, then the 2/3-byte heads, the binary tree and the 6-byte hash ways — and inserts the position.
  /// </summary>
  /// <returns>The number of candidates found.</returns>
  private int FindCandidates(ReadOnlySpan<uint> repeatDistances, uint wpos, uint limit) {
    var tables = this._tables;
    var window = this._window;
    var result = this._candidates;
    var bound = ShortMatchMaxDistance;
    const int first = 1;

    var h2 = (int)this.Hash2(wpos);
    var h3 = Hash2Size + (int)this.Hash3(wpos);
    var h6 = this._hashWidth != 0 ? this.Hash6(wpos, this._hashBits) : 0;
    var hashTree = this._hasTree ? this.Hash6(wpos, this._treeBits) : 0;
    uint minLength = 1, distance = 0;
    var count = 0;

    for (var i = 0; i < 4; ++i) {
      var repeat = repeatDistances[i];
      if (repeat >= this._validRange)
        continue;

      var comparePos = wpos >= repeat ? wpos - repeat : wpos + this._windowSize - repeat;
      var compareLimit = Math.Min(limit, this._windowSize - comparePos);
      if (minLength >= compareLimit || window[comparePos + minLength] != window[wpos + minLength])
        continue;

      var matchLength = MatchLength(window, wpos, comparePos, compareLimit);
      // A one-byte rep0 copy from window offset 0 is skipped: libcsc's decoder resolves that one
      // case (position == rep0, only possible after the window wrapped) to the byte past the window
      // end, so its own encoder's choice would decode wrongly. Everything else matches libcsc.
      if (matchLength != 0 && i == 0 && wpos != repeat) {
        result[first + count] = new(1, 1);
        if (count + 2 < CandidateLimit)
          ++count;
      }

      if (matchLength > minLength) {
        minLength = matchLength;
        result[first + count] = new(matchLength, 1 + (uint)i);
        if (count + 2 < CandidateLimit)
          ++count;
        if (matchLength >= this._goodLength) {
          distance = uint.MaxValue;
          break;
        }
      }
    }

    if (this._useShortHashes) {
      if (this._pos - tables[h2] > distance) {
        distance = this._pos - tables[h2];
        if (distance < this._validRange) {
          // libcsc tests "wpos > dist" for this head only, so it never matches window offset 0 here.
          var comparePos = wpos > distance ? wpos - distance : wpos + this._windowSize - distance;
          var compareLimit = Math.Min(limit, this._windowSize - comparePos);
          if (minLength < compareLimit && window[comparePos + minLength] == window[wpos + minLength])
            this.TakeHeadCandidate(ref count, ref minLength, ref distance, MatchLength(window, wpos, comparePos, compareLimit), bound);
        }
      }

      if (this._pos - tables[h3] > distance) {
        distance = this._pos - tables[h3];
        if (distance < this._validRange) {
          var comparePos = wpos >= distance ? wpos - distance : wpos + this._windowSize - distance;
          var compareLimit = Math.Min(limit, this._windowSize - comparePos);
          if (minLength < compareLimit && window[comparePos + minLength] == window[wpos + minLength])
            this.TakeHeadCandidate(ref count, ref minLength, ref distance, MatchLength(window, wpos, comparePos, compareLimit), bound);
        }
      }

      tables[h2] = this._pos;
      tables[h3] = this._pos;
    }

    if (this._hasTree) {
      var headIndex = this._treeHeadOffset + (int)hashTree;
      distance = this._pos - tables[headIndex];
      var left = this._treeNodesOffset + (int)(this._treePos * 2);
      var right = left + 1;

      // The tree head may point beyond the tree's own range; such a candidate is checked on its own.
      if (distance >= this._treeSize && distance < this._validRange) {
        var comparePos = wpos >= distance ? wpos - distance : wpos + this._windowSize - distance;
        var compareLimit = Math.Min(limit, this._windowSize - comparePos);
        if (minLength < compareLimit && window[comparePos + minLength] == window[wpos + minLength])
          this.TakeHeadCandidate(ref count, ref minLength, ref distance, MatchLength(window, wpos, comparePos, compareLimit), bound);
      }

      uint leftLength = 0, rightLength = 0;
      for (uint cycle = 0; ; ++cycle) {
        if (cycle >= this._treeCycles || distance >= this._treeSize || distance >= this._validRange) {
          tables[left] = tables[right] = 0;
          break;
        }

        var comparePos = wpos >= distance ? wpos - distance : wpos + this._windowSize - distance;
        var length = Math.Min(leftLength, rightLength);
        var compareLimit = Math.Min(limit, this._windowSize - comparePos);
        if (length >= compareLimit) {
          tables[left] = tables[right] = 0;
          break;
        }

        var nodePos = this._treePos >= distance ? this._treePos - distance : this._treePos + this._treeSize - distance;
        var node = this._treeNodesOffset + (int)(nodePos * 2);
        if (window[wpos + length] == window[comparePos + length]) {
          ++length;
          while (length < compareLimit && window[wpos + length] == window[comparePos + length])
            ++length;

          if (length > minLength) {
            minLength = length;
            if (length > 6 || distance < bound[(int)length]) {
              result[first + count] = new(length, 4 + distance);
              if (count + 2 < CandidateLimit)
                ++count;
            }
          }

          if (length >= this._goodLength) {
            tables[left] = tables[node];
            tables[right] = tables[node + 1];
            distance = uint.MaxValue;
            break;
          }

          if (length >= compareLimit) {
            tables[left] = tables[right] = 0;
            break;
          }
        }

        if (window[comparePos + length] < window[wpos + length]) {
          tables[left] = this._pos - distance;
          left = node + 1;
          distance = this._pos - tables[left];
          leftLength = length;
        } else {
          tables[right] = this._pos - distance;
          right = node;
          distance = this._pos - tables[right];
          rightLength = length;
        }
      }

      tables[headIndex] = this._pos;
      if (++this._treePos >= this._treeSize)
        this._treePos -= this._treeSize;
    }

    var row = this._hash6Offset + (int)(h6 * this._hashWidth);
    var ways = Math.Min(this._hashWidth, this._hashCycles);
    for (var i = 0; i < ways; ++i) {
      if (this._pos - tables[row + i] <= distance)
        continue;

      distance = this._pos - tables[row + i];
      if (distance >= this._validRange)
        continue;

      var comparePos = wpos >= distance ? wpos - distance : wpos + this._windowSize - distance;
      var compareLimit = Math.Min(limit, this._windowSize - comparePos);
      if (minLength >= compareLimit || window[comparePos + minLength] != window[wpos + minLength])
        continue;

      var matchLength = MatchLength(window, wpos, comparePos, compareLimit);
      if (matchLength <= minLength)
        continue;

      minLength = matchLength;
      if (matchLength <= 6 && distance >= bound[(int)matchLength])
        continue;

      result[first + count] = new(matchLength, 4 + distance);
      if (count + 2 < CandidateLimit)
        ++count;
      if (matchLength >= this._goodLength)
        break;
    }

    if (this._hashWidth != 0) {
      for (var i = (int)ways - 1; i > 0; --i)
        tables[row + i] = tables[row + i - 1];
      tables[row] = this._pos;
    }

    if (++this._pos >= 0xFFFFFFF0)
      this.Normalize();
    return count;
  }

  /// <summary>Records a hash-head candidate under the short-match distance limits.</summary>
  private void TakeHeadCandidate(ref int count, ref uint minLength, ref uint distance, uint matchLength, ReadOnlySpan<uint> bound) {
    if (matchLength <= minLength)
      return;

    minLength = matchLength;
    if (matchLength <= 6 && distance >= bound[(int)matchLength])
      return;

    this._candidates[1 + count] = new(matchLength, 4 + distance);
    if (count + 2 < CandidateLimit)
      ++count;
    if (matchLength >= this._goodLength)
      distance = uint.MaxValue;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint MatchLength(byte[] window, uint current, uint match, uint limit) {
    var a = window.AsSpan((int)current, (int)limit);
    var b = window.AsSpan((int)match, (int)limit);
    return (uint)a.CommonPrefixLength(b);
  }

  private void Normalize() {
    var diff = this._pos - this._validRange + 1;
    var tables = this._tables;
    for (var i = 0; i < tables.Length; ++i)
      tables[i] = tables[i] > diff ? tables[i] - diff : 0;
    this._pos -= diff;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private uint Hash2(uint position) => Hash2Of(this._window, (int)position);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private uint Hash3(uint position) {
    var w = this._window;
    var p = (int)position;
    return ((uint)w[p] << 8) ^ ((uint)w[p + 1] << 5) ^ w[p + 2];
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private uint Hash6(uint position, int bits) => Hash6Of(this._window, (int)position, bits);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint Hash2Of(ReadOnlySpan<byte> data, int offset) => (BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) * 65521u) & 0x3FFF;

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static uint Hash6Of(ReadOnlySpan<byte> data, int offset, int bits) {
    var v = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    uint v2 = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
    return ((v ^ (v2 << 13)) * 2654435761u) >> (32 - bits);
  }
}
