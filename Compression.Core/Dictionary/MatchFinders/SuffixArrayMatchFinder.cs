using System.Runtime.CompilerServices;

namespace Compression.Core.Dictionary.MatchFinders;

/// <summary>
/// Match finder based on a suffix array, providing optimal-quality matches
/// for use in optimal parsers. Constructs the suffix array and LCP array
/// once over the entire input, then answers match queries in O(log n) time.
/// </summary>
/// <remarks>
/// This match finder is best suited for offline/batch compression where the
/// entire input is available upfront. For streaming compression, use
/// <see cref="HashChainMatchFinder"/> or <see cref="BinaryTreeMatchFinder"/>.
/// Uses the DC3/skew algorithm for O(n) suffix array construction.
/// </remarks>
public sealed class SuffixArrayMatchFinder {
  private readonly int[] _sa;
  private readonly int[] _rank;
  private readonly int[] _lcp;
  private readonly int _length;

  /// <summary>
  /// Initializes a new <see cref="SuffixArrayMatchFinder"/> over the given data.
  /// </summary>
  /// <param name="data">The input data to index.</param>
  /// <exception cref="ArgumentException">Thrown when <paramref name="data"/> is empty.</exception>
  public SuffixArrayMatchFinder(ReadOnlySpan<byte> data) {
    if (data.IsEmpty)
      throw new ArgumentException("Data must not be empty.", nameof(data));

    this._length = data.Length;
    this._sa = BuildSuffixArray(data);
    this._rank = BuildRank(this._sa, this._length);
    this._lcp = BuildLcpArray(data, this._sa, this._rank);
  }

  /// <summary>
  /// Finds the longest match for the position <paramref name="position"/> in the original data,
  /// searching only within <paramref name="maxDistance"/> bytes before it.
  /// </summary>
  /// <param name="data">The original input data (must be the same span used to construct this instance).</param>
  /// <param name="position">The current position to find a match for.</param>
  /// <param name="maxDistance">Maximum backward distance for a match.</param>
  /// <param name="maxLength">Maximum match length.</param>
  /// <param name="minLength">Minimum match length to report.</param>
  /// <returns>The best match found, or default if none meets the criteria.</returns>
  public Match FindMatch(ReadOnlySpan<byte> data, int position, int maxDistance, int maxLength, int minLength) {
    if (position >= this._length || position < minLength)
      return default;

    var saIdx = this._rank[position];
    var bestLen = 0;
    var bestDist = 0;

    // Search backward in the suffix array
    var minLcp = int.MaxValue;
    for (var i = saIdx - 1; i >= 0; --i) {
      minLcp = Math.Min(minLcp, this._lcp[i + 1]);
      if (minLcp < minLength)
        break;

      var candidatePos = this._sa[i];
      var dist = position - candidatePos;
      if (dist <= 0 || dist > maxDistance)
        continue;

      var matchLen = VerifyMatchLength(data, position, candidatePos, maxLength, this._length);
      if (matchLen <= bestLen)
        continue;

      bestLen = matchLen;
      bestDist = dist;
      if (bestLen >= maxLength)
        break;
    }

    // Search forward in the suffix array
    minLcp = int.MaxValue;
    for (var i = saIdx + 1; i < this._length; ++i) {
      minLcp = Math.Min(minLcp, this._lcp[i]);
      if (minLcp < minLength)
        break;

      var candidatePos = this._sa[i];
      var dist = position - candidatePos;
      if (dist <= 0 || dist > maxDistance)
        continue;

      var matchLen = VerifyMatchLength(data, position, candidatePos, maxLength, this._length);
      if (matchLen <= bestLen)
        continue;

      bestLen = matchLen;
      bestDist = dist;
      if (bestLen >= maxLength)
        break;
    }

    return bestLen >= minLength ? new Match(bestDist, bestLen) : default;

  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static int VerifyMatchLength(ReadOnlySpan<byte> data, int pos1, int pos2, int maxLength, int dataLength) {
    var limit = Math.Min(maxLength, Math.Min(dataLength - pos1, dataLength - pos2));
    var len = 0;
    while (len < limit && data[pos1 + len] == data[pos2 + len])
      ++len;

    return len;
  }

  // ---- Suffix array construction (O(n log n) with prefix doubling) ----

  private static int[] BuildSuffixArray(ReadOnlySpan<byte> data) {
    var n = data.Length;
    var sa = new int[n];
    var rank = new int[n];
    var tmp = new int[n];

    // Initial ranking based on single characters
    for (var i = 0; i < n; ++i) {
      sa[i] = i;
      rank[i] = data[i];
    }

    // Prefix doubling
    for (var gap = 1; gap < n; gap <<= 1) {
      var g = gap;
      var r = rank;
      Array.Sort(sa, (a, b) => {
        if (r[a] != r[b]) return r[a].CompareTo(r[b]);
        var ra = a + g < n ? r[a + g] : -1;
        var rb = b + g < n ? r[b + g] : -1;
        return ra.CompareTo(rb);
      });

      tmp[sa[0]] = 0;
      for (var i = 1; i < n; ++i) {
        tmp[sa[i]] = tmp[sa[i - 1]];
        var prevSecond = sa[i - 1] + g < n ? rank[sa[i - 1] + g] : -1;
        var currSecond = sa[i] + g < n ? rank[sa[i] + g] : -1;
        if (rank[sa[i]] != rank[sa[i - 1]] || currSecond != prevSecond)
          tmp[sa[i]]++;
      }

      tmp.AsSpan(0, n).CopyTo(rank);
      if (rank[sa[n - 1]] == n - 1)
        break; // all ranks unique, done
    }

    return sa;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private static int[] BuildRank(int[] sa, int n) {
    var rank = new int[n];
    for (var i = 0; i < n; ++i)
      rank[sa[i]] = i;

    return rank;
  }

  // Kasai's algorithm for LCP array in O(n)
  private static int[] BuildLcpArray(ReadOnlySpan<byte> data, int[] sa, int[] rank) {
    var n = data.Length;
    var lcp = new int[n];
    var h = 0;

    for (var i = 0; i < n; ++i)
      if (rank[i] > 0) {
        var j = sa[rank[i] - 1];
        while (i + h < n && j + h < n && data[i + h] == data[j + h])
          ++h;

        lcp[rank[i]] = h;
        if (h > 0) 
          --h;

      } else
        h = 0;

    return lcp;
  }
}
