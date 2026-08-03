namespace Compression.Core.Dictionary.Lzfse;

/// <summary>
/// Converts raw frequency counts into FSE-table-ready normalized counts (summing to
/// exactly the table size) using the largest-remainder ("Hamilton apportionment")
/// method: every present symbol first gets its floor share (minimum one slot), then
/// the leftover slots are handed out one at a time to the symbols with the largest
/// fractional remainder, or taken back from those with the smallest remainder if
/// flooring (plus the one-slot minimum) overshot the table size.
/// </summary>
/// <remarks>
/// This exists because <see cref="Compression.Core.Entropy.Fse.FseEncoder.NormalizeCounts"/>
/// hands its entire rounding remainder to a single symbol; with many distinct
/// symbols relative to the table size (e.g. a near-uniform byte-value literal
/// stream) that single correction can overshoot far enough to drive an unrelated
/// symbol's count to zero or negative, which then makes that symbol unencodable.
/// Rather than modify the shared entropy module, this LZFSE-local normalizer
/// produces its own normalized counts using the classic largest-remainder
/// apportionment method and hands them to the existing, unmodified
/// <see cref="Compression.Core.Entropy.Fse.FseEncoder"/> /
/// <see cref="Compression.Core.Entropy.Fse.FseDecoder"/> / <see cref="Compression.Core.Entropy.Fse.FseTable"/>
/// machinery, which is agnostic to how its normalized-count input was produced.
/// Every present symbol is emitted as a positive slot count (never the -1
/// "sub-probability" marker); <see cref="Compression.Core.Entropy.Fse.FseTable.Build"/>
/// treats a positive count of 1 identically to -1 (both place the symbol in
/// exactly one table slot), so the marker is not needed for correctness.
/// </remarks>
internal static class FseNormalizer {
  /// <summary>
  /// Normalizes raw frequency counts to sum to exactly <c>1 &lt;&lt; tableLog</c>.
  /// </summary>
  /// <param name="counts">Raw frequency counts indexed by symbol.</param>
  /// <param name="maxSymbol">The maximum symbol value to consider.</param>
  /// <param name="tableLog">The log2 of the target table size. Must satisfy <c>(1 &lt;&lt; tableLog) &gt;= (number of distinct symbols)</c>.</param>
  /// <returns>Normalized counts where 0 means absent and positive values are slot counts.</returns>
  /// <exception cref="ArgumentException">No symbols with non-zero frequency, or the table is too small to hold every distinct symbol.</exception>
  public static short[] Normalize(int[] counts, int maxSymbol, int tableLog) {
    var tableSize = 1 << tableLog;

    long total = 0;
    var nonZeroCount = 0;
    var onlySymbol = -1;
    for (var s = 0; s <= maxSymbol; ++s) {
      if (counts[s] <= 0)
        continue;
      total += counts[s];
      ++nonZeroCount;
      onlySymbol = s;
    }

    if (total == 0)
      throw new ArgumentException("At least one symbol must have a non-zero count.", nameof(counts));
    if (nonZeroCount > tableSize)
      throw new ArgumentException("Table size is too small to hold every distinct symbol.", nameof(tableLog));

    var normalized = new short[maxSymbol + 1];

    if (nonZeroCount == 1) {
      normalized[onlySymbol] = (short)tableSize;
      return normalized;
    }

    var floorAlloc = new int[maxSymbol + 1];
    var remainder = new long[maxSymbol + 1];
    var symbols = new List<int>(nonZeroCount);
    long used = 0;

    for (var s = 0; s <= maxSymbol; ++s) {
      if (counts[s] <= 0)
        continue;

      var scaled = (long)counts[s] * tableSize;
      var floor = (int)(scaled / total);
      if (floor < 1)
        floor = 1;

      floorAlloc[s] = floor;
      remainder[s] = scaled - (long)floor * total; // may be negative when floor was forced up from 0
      symbols.Add(s);
      used += floor;
    }

    var diff = tableSize - used;

    if (diff > 0) {
      var order = symbols.OrderByDescending(s => remainder[s]).ThenBy(s => s).ToList();
      for (var i = 0; i < diff; ++i)
        floorAlloc[order[i % order.Count]] += 1;
    } else if (diff < 0) {
      var need = -diff;
      while (need > 0) {
        var order = symbols.Where(s => floorAlloc[s] > 1).OrderBy(s => remainder[s]).ThenBy(s => s).ToList();
        if (order.Count == 0)
          throw new InvalidOperationException("FSE normalization could not converge.");

        foreach (var s in order) {
          if (need == 0)
            break;
          floorAlloc[s] -= 1;
          --need;
        }
      }
    }

    foreach (var s in symbols)
      normalized[s] = (short)floorAlloc[s];

    return normalized;
  }
}
