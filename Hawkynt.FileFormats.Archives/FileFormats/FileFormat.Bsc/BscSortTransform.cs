// Converted from libbsc's st/st.cpp (Apache-2.0), the single-threaded inverse transform.
// Copyright (c) 2009-2025 Ilya Grebnov <ilya.grebnov@gmail.com>
// See THIRD-PARTY-NOTICE.libbsc.txt in this directory.
namespace FileFormat.Bsc;

/// <summary>
/// The inverse of libbsc's Sort Transform of order 3 to 8 (<c>bsc -m3</c> .. <c>-m8</c>):
/// <c>bsc_st_decode</c> with its serial helpers <c>bsc_unst_sort_serial</c> and
/// <c>bsc_unst_reconstruct_case1/2/3_serial</c>.
/// </summary>
internal static class BscSortTransform {
  private const int AlphabetSize = 256;

  /// <summary>Restores <paramref name="data"/> in place from its order-<paramref name="order"/> sort transform.</summary>
  public static void Decode(byte[] data, int order, int index) {
    var n = data.Length;
    if (index < 0 || index >= n)
      throw new InvalidDataException($"BSC: Sort Transform index {index} outside the block");
    if (order is < 3 or > 8)
      throw new InvalidDataException($"BSC: Sort Transform order {order} is not 3..8");
    if (n <= 1)
      return;

    var p = new uint[n];
    var bucket = new uint[AlphabetSize * AlphabetSize];
    var count = new uint[AlphabetSize];
    var failBack = Sort(data, p, count, bucket, n, order);
    if (n < 0x800000)
      ReconstructCase1(data, p, count, n, index);
    else if (!failBack)
      ReconstructCase2(data, p, count, n, index);
    else
      ReconstructCase3(data, p, count, n, index);
  }

  // bsc_unst_sort_serial: marks in P where the order-k context groups begin.
  private static bool Sort(byte[] t, uint[] p, uint[] count, uint[] bucket, int n, int k) {
    Span<uint> index = stackalloc uint[AlphabetSize];
    Span<int> group = stackalloc int[AlphabetSize];

    var failBack = false;
    for (var i = 0; i < n; ++i)
      ++count[t[i]];

    for (int sum = 0, c = 0; c < AlphabetSize; ++c) {
      if (count[c] >= 0x800000)
        failBack = true;

      var start = sum;
      sum += (int)count[c];
      count[c] = (uint)start;
      if (start != sum)
        for (var i = start; i < sum; ++i)
          ++bucket[c << 8 | t[i]];
    }

    for (var c = 0; c < AlphabetSize; ++c)
      for (var d = 0; d < c; ++d)
        (bucket[d << 8 | c], bucket[c << 8 | d]) = (bucket[c << 8 | d], bucket[d << 8 | c]);

    if (k == 3) {
      for (int sum = 0, w = 0; w < AlphabetSize * AlphabetSize; ++w)
        if (bucket[w] > 0) {
          p[sum] = 1;
          sum += (int)bucket[w];
        }

      return failBack;
    }

    count.CopyTo(index);
    group.Fill(-1);
    for (int sum = 0, w = 0; w < AlphabetSize * AlphabetSize; ++w) {
      var start = sum;
      sum += (int)bucket[w];
      bucket[w] = (uint)start;
      for (var i = start; i < sum; ++i) {
        var c = t[i];
        if (group[c] != w) {
          group[c] = w;
          p[index[c]] = 0x80000000;
        }

        ++index[c];
      }
    }

    uint mask0 = 0x80000000, mask1 = 0x40000000;
    for (var round = 4; round < k; ++round, mask0 >>= 1, mask1 >>= 1) {
      count.CopyTo(index);
      group.Fill(-1);
      for (int g = 0, i = 0; i < n; ++i) {
        if ((p[i] & mask0) != 0)
          g = i;

        var c = t[i];
        if (group[c] != g) {
          group[c] = g;
          p[index[c]] += mask1;
        }

        ++index[c];
      }
    }

    return failBack;
  }

  // Blocks below 8 MiB: byte, then a 23-bit link per cell.
  private static void ReconstructCase1(byte[] t, uint[] p, uint[] count, int n, int start) {
    Span<uint> index = stackalloc uint[AlphabetSize];
    Span<int> group = stackalloc int[AlphabetSize];
    count.CopyTo(index);
    group.Fill(-1);

    for (int g = 0, i = 0; i < n; ++i) {
      if (p[i] > 0)
        g = i;

      var c = t[i];
      if (group[c] < g) {
        group[c] = i;
        p[i] = (uint)c << 24 | index[c];
      } else {
        p[i] = (uint)c << 24 | 0x800000 | (uint)group[c];
        ++p[group[c]];
      }

      ++index[c];
    }

    for (int position = start, i = n - 1; i >= 0; --i) {
      var u = p[position];
      if ((u & 0x800000) != 0) {
        position = (int)(u & 0x7fffff);
        u = p[position];
      }

      t[i] = (byte)(u >> 24);
      --p[position];
      position = (int)(u & 0x7fffff);
    }
  }

  // Larger blocks whose symbols all occur fewer than 8 Mi times: links relative to the group.
  private static void ReconstructCase2(byte[] t, uint[] p, uint[] count, int n, int start) {
    Span<uint> index = stackalloc uint[AlphabetSize];
    Span<int> group = stackalloc int[AlphabetSize];
    index.Clear();
    group.Fill(-1);

    for (int g = 0, i = 0; i < n; ++i) {
      if (p[i] > 0)
        g = i;

      var c = t[i];
      if (group[c] < g) {
        group[c] = i;
        p[i] = (uint)c << 24 | index[c];
      } else {
        p[i] = (uint)c << 24 | 0x800000 | (uint)(i - group[c]);
        ++p[group[c]];
      }

      ++index[c];
    }

    for (int position = start, i = n - 1; i >= 0; --i) {
      var u = p[position];
      if ((u & 0x800000) != 0) {
        position -= (int)(u & 0x7fffff);
        u = p[position];
      }

      var c = (byte)(u >> 24);
      t[i] = c;
      --p[position];
      position = (int)((u & 0x7fffff) + count[c]);
    }
  }

  private const int FastBits = 10;

  // The general case: 31-bit links, the symbol recovered from the cumulative counts.
  private static void ReconstructCase3(byte[] t, uint[] p, uint[] count, int n, int start) {
    Span<byte> fastBits = stackalloc byte[1 << FastBits];
    var index = new uint[AlphabetSize];
    Span<int> group = stackalloc int[AlphabetSize];
    count.CopyTo(index, 0);
    group.Fill(-1);

    for (int g = 0, i = 0; i < n; ++i) {
      if (p[i] > 0)
        g = i;

      var c = t[i];
      if (group[c] < g) {
        group[c] = i;
        p[i] = index[c];
      } else {
        p[i] = 0x80000000 | (uint)group[c];
        ++p[group[c]];
      }

      ++index[c];
    }

    var shift = 0;
    while ((n - 1) >> shift >= 1 << FastBits)
      ++shift;

    for (int v = 0, c = 0; c < AlphabetSize; ++c) {
      index[c] = c + 1 < AlphabetSize ? count[c + 1] : (uint)n;
      if (count[c] != index[c])
        for (; v <= (int)((index[c] - 1) >> shift); ++v)
          fastBits[v] = (byte)c;
    }

    if ((p[start] & 0x80000000) != 0)
      start = (int)(p[start] & 0x7fffffff);

    t[0] = (byte)Search(fastBits[start >> shift], index, (uint)start);
    --p[start];
    start = (int)(p[start] + 1);

    for (int position = start, i = n - 1; i >= 1; --i) {
      var u = p[position];
      if ((u & 0x80000000) != 0) {
        position = (int)(u & 0x7fffffff);
        u = p[position];
      }

      t[i] = (byte)Search(fastBits[position >> shift], index, (uint)position);
      --p[position];
      position = (int)u;
    }
  }

  private static int Search(int index, uint[] p, uint value) {
    while (p[index] <= value)
      ++index;
    return index;
  }
}
