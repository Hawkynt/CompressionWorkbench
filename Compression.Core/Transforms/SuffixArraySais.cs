namespace Compression.Core.Transforms;

/// <summary>
/// Suffix array construction by induced sorting (SA-IS, Nong, Zhang &amp; Chan 2009).
/// </summary>
/// <remarks>
/// Converted from Yuta Mori's sais-lite 2.4.1 (<c>sais.c</c>, MIT; see
/// <c>THIRD-PARTY-NOTICE.sais-lite.txt</c>). The conversion keeps sais-lite's LMS-substring
/// sort, naming and induction steps, but always takes the plain <c>LMSsort1</c> path and gives
/// the bucket arrays and the reduced problem their own allocations instead of carving them out
/// of free space in the suffix array; both are memory placement choices that do not change the
/// result. A suffix array is unique, so the result is exactly the one libsais, divsufsort or
/// any other correct sorter produces.
/// </remarks>
public static class SuffixArraySais {

  private interface IText {
    int this[int index] { get; }
  }

  private readonly struct ByteText(byte[] data) : IText {
    public int this[int index] => data[index];
  }

  private readonly struct IntText(int[] data) : IText {
    public int this[int index] => data[index];
  }

  /// <summary>Builds the suffix array of <paramref name="data"/>: the start of every suffix, in ascending order.</summary>
  /// <param name="data">The text.</param>
  /// <returns>The suffix array, one entry per byte.</returns>
  public static int[] Build(ReadOnlySpan<byte> data) {
    var n = data.Length;
    var sa = new int[n];
    if (n <= 1)
      return sa;

    Sort(new ByteText(data.ToArray()), sa, n, 256);
    return sa;
  }

  private static void GetCounts<T>(T text, int[] counts, int n, int k) where T : struct, IText {
    Array.Clear(counts, 0, k);
    for (var i = 0; i < n; ++i)
      ++counts[text[i]];
  }

  private static void GetBuckets(int[] counts, int[] buckets, int k, bool end) {
    var sum = 0;
    if (end)
      for (var i = 0; i < k; ++i) {
        sum += counts[i];
        buckets[i] = sum;
      }
    else
      for (var i = 0; i < k; ++i) {
        sum += counts[i];
        buckets[i] = sum - counts[i];
      }
  }

  // Sorts all LMS substrings by inducing an L pass and an S pass from the LMS positions.
  private static void LmsSort<T>(T text, int[] sa, int[] counts, int[] buckets, int n, int k) where T : struct, IText {
    GetBuckets(counts, buckets, k, end: false);
    var j = n - 1;
    var c1 = text[j];
    var b = buckets[c1];
    --j;
    sa[b++] = text[j] < c1 ? ~j : j;
    for (var i = 0; i < n; ++i) {
      if (0 < (j = sa[i])) {
        var c0 = text[j];
        if (c0 != c1) {
          buckets[c1] = b;
          b = buckets[c1 = c0];
        }

        --j;
        sa[b++] = text[j] < c1 ? ~j : j;
        sa[i] = 0;
      } else if (j < 0)
        sa[i] = ~j;
    }

    GetBuckets(counts, buckets, k, end: true);
    c1 = 0;
    b = buckets[0];
    for (var i = n - 1; 0 <= i; --i) {
      if (0 < (j = sa[i])) {
        var c0 = text[j];
        if (c0 != c1) {
          buckets[c1] = b;
          b = buckets[c1 = c0];
        }

        --j;
        sa[--b] = text[j] > c1 ? ~(j + 1) : j;
        sa[i] = 0;
      }
    }
  }

  // Compacts the sorted LMS substrings into sa[0..m) and names them; returns the name count.
  private static int LmsPostProcess<T>(T text, int[] sa, int n, int m) where T : struct, IText {
    int i, j, p;
    for (i = 0; (p = sa[i]) < 0; ++i)
      sa[i] = ~p;

    if (i < m)
      for (j = i, ++i; ; ++i)
        if ((p = sa[i]) < 0) {
          sa[j++] = ~p;
          sa[i] = 0;
          if (j == m)
            break;
        }

    // Store the length of every LMS substring.
    i = n - 1;
    j = n - 1;
    var c0 = text[n - 1];
    int c1;
    do {
      c1 = c0;
    } while (0 <= --i && (c0 = text[i]) >= c1);

    while (0 <= i) {
      do {
        c1 = c0;
      } while (0 <= --i && (c0 = text[i]) <= c1);

      if (0 <= i) {
        sa[m + ((i + 1) >> 1)] = j - i;
        j = i + 1;
        do {
          c1 = c0;
        } while (0 <= --i && (c0 = text[i]) >= c1);
      }
    }

    // Name the substrings: equal neighbours share a name.
    var name = 0;
    var q = n;
    var qLength = 0;
    for (i = 0; i < m; ++i) {
      p = sa[i];
      var pLength = sa[m + (p >> 1)];
      var different = true;
      if (pLength == qLength && q + pLength < n) {
        for (j = 0; j < pLength && text[p + j] == text[q + j]; ++j) {
        }

        if (j == pLength)
          different = false;
      }

      if (different) {
        ++name;
        q = p;
        qLength = pLength;
      }

      sa[m + (p >> 1)] = name;
    }

    return name;
  }

  // Induces the full suffix array from the sorted LMS suffixes placed at their bucket ends.
  private static void InduceSuffixArray<T>(T text, int[] sa, int[] counts, int[] buckets, int n, int k) where T : struct, IText {
    GetBuckets(counts, buckets, k, end: false);
    var j = n - 1;
    var c1 = text[j];
    var b = buckets[c1];
    sa[b++] = 0 < j && text[j - 1] < c1 ? ~j : j;
    for (var i = 0; i < n; ++i) {
      j = sa[i];
      sa[i] = ~j;
      if (0 < j) {
        --j;
        var c0 = text[j];
        if (c0 != c1) {
          buckets[c1] = b;
          b = buckets[c1 = c0];
        }

        sa[b++] = 0 < j && text[j - 1] < c1 ? ~j : j;
      }
    }

    GetBuckets(counts, buckets, k, end: true);
    c1 = 0;
    b = buckets[0];
    for (var i = n - 1; 0 <= i; --i) {
      if (0 < (j = sa[i])) {
        --j;
        var c0 = text[j];
        if (c0 != c1) {
          buckets[c1] = b;
          b = buckets[c1 = c0];
        }

        sa[--b] = j == 0 || text[j - 1] > c1 ? ~j : j;
      } else
        sa[i] = ~j;
    }
  }

  private static void Sort<T>(T text, int[] sa, int n, int k) where T : struct, IText {
    var counts = new int[k];
    var buckets = new int[k];

    // Stage 1: place the LMS suffixes and sort the LMS substrings.
    GetCounts(text, counts, n, k);
    GetBuckets(counts, buckets, k, end: true);
    Array.Clear(sa, 0, n);
    var pending = -1; // sais-lite's write cursor starts on a dummy cell
    var i = n - 1;
    var j = n;
    var m = 0;
    var c0 = text[n - 1];
    int c1;
    do {
      c1 = c0;
    } while (0 <= --i && (c0 = text[i]) >= c1);

    while (0 <= i) {
      do {
        c1 = c0;
      } while (0 <= --i && (c0 = text[i]) <= c1);

      if (0 <= i) {
        if (pending >= 0)
          sa[pending] = j;
        pending = --buckets[c1];
        j = i;
        ++m;
        do {
          c1 = c0;
        } while (0 <= --i && (c0 = text[i]) >= c1);
      }
    }

    int name;
    if (1 < m) {
      LmsSort(text, sa, counts, buckets, n, k);
      name = LmsPostProcess(text, sa, n, m);
    } else if (m == 1) {
      sa[pending] = j + 1;
      name = 1;
    } else
      name = 0;

    // Stage 2: when names repeat, sort the reduced string of names recursively.
    if (name < m) {
      var reduced = new int[m];
      for (i = m + (n >> 1) - 1, j = m - 1; m <= i; --i)
        if (sa[i] != 0)
          reduced[j--] = sa[i] - 1;

      var reducedSa = new int[m];
      Sort(new IntText(reduced), reducedSa, m, name);

      i = n - 1;
      j = m - 1;
      c0 = text[n - 1];
      do {
        c1 = c0;
      } while (0 <= --i && (c0 = text[i]) >= c1);

      while (0 <= i) {
        do {
          c1 = c0;
        } while (0 <= --i && (c0 = text[i]) <= c1);

        if (0 <= i) {
          reduced[j--] = i + 1;
          do {
            c1 = c0;
          } while (0 <= --i && (c0 = text[i]) >= c1);
        }
      }

      for (i = 0; i < m; ++i)
        sa[i] = reduced[reducedSa[i]];
    }

    // Stage 3: put the sorted LMS suffixes at their bucket ends and induce the rest.
    if (1 < m) {
      GetBuckets(counts, buckets, k, end: true);
      i = m - 1;
      j = n;
      var p = sa[m - 1];
      c1 = text[p];
      do {
        var q = buckets[c0 = c1];
        while (q < j)
          sa[--j] = 0;
        do {
          sa[--j] = p;
          if (--i < 0)
            break;
          p = sa[i];
        } while ((c1 = text[p]) == c0);
      } while (0 <= i);

      while (0 < j)
        sa[--j] = 0;
    }

    InduceSuffixArray(text, sa, counts, buckets, n, k);
  }
}
