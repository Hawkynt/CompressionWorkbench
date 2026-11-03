namespace FileFormat.Zpaq;

/// <summary>
/// Linear-time suffix array construction by induced sorting (SA-IS).
/// </summary>
/// <remarks>
/// Implemented from G. Nong, S. Zhang and W. H. Chan, "Two Efficient Algorithms for Linear
/// Time Suffix Array Construction", IEEE Transactions on Computers 60(10), 2011. Suffixes are
/// ordered as plain byte strings (a proper prefix sorts first), which is the order a virtual
/// sentinel smaller than every byte produces and the order libzpaq's BWT and suffix-array
/// match finder expect.
/// </remarks>
internal static class ZpaqSuffixArray {
  /// <summary>Returns the suffix array of <paramref name="text"/>.</summary>
  public static int[] Build(ReadOnlySpan<byte> text) {
    var n = text.Length;
    var sa = new int[n];
    if (n == 0)
      return sa;
    if (n == 1)
      return sa;
    var s = new int[n];
    for (var i = 0; i < n; ++i)
      s[i] = text[i];
    Sais(s, sa, n, 256);
    return sa;
  }

  /// <summary>Sorts the suffixes of <paramref name="s"/>[0..n) over alphabet [0..k) into <paramref name="sa"/>.</summary>
  private static void Sais(int[] s, int[] sa, int n, int k) {
    if (n == 1) {
      sa[0] = 0;
      return;
    }

    // Suffix types: true = S-type, false = L-type. The virtual sentinel at n is S-type.
    var t = new bool[n];
    t[n - 1] = false;
    for (var i = n - 2; i >= 0; --i)
      t[i] = s[i] < s[i + 1] || (s[i] == s[i + 1] && t[i + 1]);

    bool IsLms(int i) => i > 0 && t[i] && !t[i - 1];

    var bucket = new int[k];
    void Buckets(bool end) {
      Array.Clear(bucket);
      for (var i = 0; i < n; ++i)
        ++bucket[s[i]];
      var sum = 0;
      for (var c = 0; c < k; ++c) {
        sum += bucket[c];
        bucket[c] = end ? sum : sum - bucket[c];
      }
    }

    void Induce() {
      Buckets(false);
      // The sentinel's predecessor n-1 is L-type and comes first in its bucket.
      sa[bucket[s[n - 1]]++] = n - 1;
      for (var i = 0; i < n; ++i) {
        var j = sa[i] - 1;
        if (sa[i] > 0 && !t[j])
          sa[bucket[s[j]]++] = j;
      }
      Buckets(true);
      for (var i = n - 1; i >= 0; --i) {
        var j = sa[i] - 1;
        if (sa[i] > 0 && t[j])
          sa[--bucket[s[j]]] = j;
      }
    }

    // Stage 1: place LMS suffixes at bucket ends and induce to sort the LMS substrings.
    Array.Fill(sa, -1);
    Buckets(true);
    for (var i = n - 1; i >= 1; --i)
      if (IsLms(i))
        sa[--bucket[s[i]]] = i;
    Induce();

    // Compact the sorted LMS positions to the front of sa.
    var m = 0;
    for (var i = 0; i < n; ++i)
      if (IsLms(sa[i]))
        sa[m++] = sa[i];
    for (var i = m; i < n; ++i)
      sa[i] = -1;

    // Name the LMS substrings; equal substrings share a name.
    var name = 0;
    var prev = -1;
    for (var i = 0; i < m; ++i) {
      var pos = sa[i];
      var diff = prev < 0;
      if (!diff) {
        for (var d = 0; ; ++d) {
          var a = pos + d;
          var b = prev + d;
          if (a == n || b == n || s[a] != s[b] || t[a] != t[b]) {
            diff = true;
            break;
          }
          if (d > 0 && (IsLms(a) || IsLms(b))) {
            diff = !(IsLms(a) && IsLms(b));
            break;
          }
        }
      }
      if (diff) {
        ++name;
        prev = pos;
      }
      sa[m + (pos >> 1)] = name - 1;
    }
    var j2 = n - 1;
    for (var i = n - 1; i >= m; --i)
      if (sa[i] >= 0)
        sa[j2--] = sa[i];

    // Stage 2: sort the reduced problem, recursively when names repeat.
    var s1 = new int[m];
    Array.Copy(sa, n - m, s1, 0, m);
    var sa1 = new int[m];
    if (name < m)
      Sais(s1, sa1, m, name);
    else
      for (var i = 0; i < m; ++i)
        sa1[s1[i]] = i;

    // Stage 3: map back to LMS positions in sorted order and induce the full array.
    var lms = new int[m];
    var c2 = 0;
    for (var i = 1; i < n; ++i)
      if (IsLms(i))
        lms[c2++] = i;
    for (var i = 0; i < m; ++i)
      sa1[i] = lms[sa1[i]];
    Array.Fill(sa, -1);
    Buckets(true);
    for (var i = m - 1; i >= 0; --i) {
      var p = sa1[i];
      sa[--bucket[s[p]]] = p;
    }
    Induce();
  }
}
