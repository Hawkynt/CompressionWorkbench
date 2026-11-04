namespace Compression.Core.Transforms;

/// <summary>
/// The Burrows-Wheeler Transform in its suffix-array form: the rows are the suffixes of the
/// input followed by an implicit end sentinel that sorts below every byte, and the sentinel's
/// own output cell is dropped. This is the transform libsais, divsufsort and sais-lite compute
/// (<c>libsais_bwt</c>, <c>divbwt</c>, <c>sais_bwt</c>) and that libbsc and BCM store.
/// </summary>
/// <remarks>
/// <para>For an input <c>T[0..n)</c> with suffix array <c>SA</c> the output is
/// <c>T[n-1]</c> (the row of the lone sentinel suffix) followed by <c>T[SA[i]-1]</c> for every
/// row except the one with <c>SA[i] = 0</c>. The primary index is that row's position plus one,
/// so it runs from 1 to n.</para>
/// <para>It differs from <see cref="BurrowsWheelerTransform"/>, which sorts the cyclic
/// rotations of the input as bzip2 does: the two agree only when no suffix is a prefix of a
/// rotation that sorts differently, so neither can stand in for the other.</para>
/// </remarks>
public static class SuffixBurrowsWheelerTransform {

  /// <summary>Transforms <paramref name="data"/>.</summary>
  /// <param name="data">The input.</param>
  /// <returns>The transformed bytes (as many as the input) and the one-based primary index (0 for empty input).</returns>
  public static (byte[] Transformed, int PrimaryIndex) Forward(ReadOnlySpan<byte> data) {
    var (transformed, primary, _) = Forward(data, 0);
    return (transformed, primary);
  }

  /// <summary>
  /// Transforms <paramref name="data"/> and samples the auxiliary indexes libsais' <c>libsais_bwt_aux</c>
  /// reports: entry <c>t</c> is the one-based output row of the suffix starting at <c>t * rate</c>,
  /// so entry 0 is the primary index.
  /// </summary>
  /// <param name="data">The input.</param>
  /// <param name="rate">The sampling rate, a power of two; 0 to sample nothing.</param>
  /// <returns>The transformed bytes, the primary index and the samples (empty for a rate of 0).</returns>
  public static (byte[] Transformed, int PrimaryIndex, int[] Samples) Forward(ReadOnlySpan<byte> data, int rate) {
    ArgumentOutOfRangeException.ThrowIfNegative(rate);
    var n = data.Length;
    if (n == 0)
      return ([], 0, []);

    var transformed = new byte[n];
    var samples = rate == 0 ? [] : new int[(n - 1) / rate + 1];
    if (n == 1) {
      transformed[0] = data[0];
      if (samples.Length > 0)
        samples[0] = 1;
      return (transformed, 1, samples);
    }

    var sa = SuffixArraySais.Build(data);
    transformed[0] = data[n - 1];
    var primary = 0;
    var output = 1;
    for (var row = 0; row < n; ++row) {
      var suffix = sa[row];
      if (rate != 0 && (suffix & (rate - 1)) == 0)
        samples[suffix / rate] = row + 1;

      if (suffix == 0)
        primary = row + 1;
      else
        transformed[output++] = data[suffix - 1];
    }

    return (transformed, primary, samples);
  }

  /// <summary>Recovers the input from its transform.</summary>
  /// <param name="transformed">The transformed bytes.</param>
  /// <param name="primaryIndex">The one-based primary index the forward transform returned.</param>
  /// <returns>The original input.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The primary index is outside 1..n.</exception>
  public static byte[] Inverse(ReadOnlySpan<byte> transformed, int primaryIndex) {
    var n = transformed.Length;
    if (n == 0)
      return [];

    ArgumentOutOfRangeException.ThrowIfLessThan(primaryIndex, 1);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(primaryIndex, n);

    // The full last column has n + 1 cells: transformed[0..p) , the sentinel, transformed[p..n).
    // Rows sort the sentinel first, so the first column is the sentinel then every byte in order.
    Span<int> start = stackalloc int[256];
    start.Clear();
    foreach (var value in transformed)
      ++start[value];

    var sum = 1;
    for (var c = 0; c < 256; ++c) {
      var count = start[c];
      start[c] = sum;
      sum += count;
    }

    // next[row] = the row whose suffix is one longer (LF mapping over the full column).
    var next = new int[n + 1];
    for (var row = 0; row <= n; ++row) {
      if (row == primaryIndex) {
        next[row] = 0;
        continue;
      }

      var value = transformed[row < primaryIndex ? row : row - 1];
      next[row] = start[value]++;
    }

    // Row 0 is the sentinel suffix; its last-column byte is T[n-1].
    var result = new byte[n];
    var current = 0;
    for (var i = n - 1; i >= 0; --i) {
      result[i] = transformed[current < primaryIndex ? current : current - 1];
      current = next[current];
    }

    return result;
  }
}
