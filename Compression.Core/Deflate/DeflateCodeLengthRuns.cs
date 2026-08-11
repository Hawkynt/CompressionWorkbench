namespace Compression.Core.Deflate;

/// <summary>
/// Encodes the concatenated literal/length and distance code lengths of a dynamic block
/// with the run-length alphabet of RFC 1951 section 3.2.7.
/// </summary>
internal static class DeflateCodeLengthRuns {
  /// <summary>One entry of the encoded code-length stream.</summary>
  /// <param name="Symbol">The code-length alphabet symbol (0-18).</param>
  /// <param name="ExtraBits">How many extra bits follow the symbol.</param>
  /// <param name="ExtraValue">The value those extra bits carry.</param>
  internal readonly record struct Run(int Symbol, int ExtraBits, int ExtraValue);

  /// <summary>
  /// Encodes the given code lengths.
  /// </summary>
  /// <param name="lengths">The concatenated literal/length and distance code lengths.</param>
  /// <returns>The encoded stream.</returns>
  public static List<Run> Encode(ReadOnlySpan<int> lengths) {
    var result = new List<Run>();
    var i = 0;

    while (i < lengths.Length) {
      var value = lengths[i];

      if (value == 0) {
        var zeros = 1;
        while (i + zeros < lengths.Length && lengths[i + zeros] == 0)
          ++zeros;

        var remaining = zeros;
        while (remaining > 0)
          switch (remaining) {
            // Symbol 18 repeats a zero 11 to 138 times.
            case >= 11: {
              var run = Math.Min(remaining, 138);
              result.Add(new(18, 7, run - 11));
              remaining -= run;
              continue;
            }
            // Symbol 17 repeats a zero 3 to 10 times.
            case >= 3:
              result.Add(new(17, 3, remaining - 3));
              remaining = 0;
              continue;
            default:
              result.Add(new(0, 0, 0));
              --remaining;
              continue;
          }

        i += zeros;
        continue;
      }

      // Symbol 16 repeats the previous length 3 to 6 times, so the length itself is
      // written once first.
      result.Add(new(value, 0, 0));
      ++i;

      var repeats = 0;
      while (i + repeats < lengths.Length && lengths[i + repeats] == value)
        ++repeats;

      var left = repeats;
      while (left >= 3) {
        var run = Math.Min(left, 6);
        result.Add(new(16, 2, run - 3));
        left -= run;
      }

      while (left > 0) {
        result.Add(new(value, 0, 0));
        --left;
      }

      i += repeats;
    }

    return result;
  }
}
