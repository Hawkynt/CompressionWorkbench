namespace Compression.Core.Dictionary.Lzma;

/// <summary>
/// The LZMA dictionary sizes 7-Zip recognises: 2^n or 3 × 2^(n-1), from 4 KiB up to 1 GiB.
/// </summary>
public static class LzmaDictionarySize {
  /// <summary>The smallest dictionary written.</summary>
  public const int Minimum = 1 << 12;

  /// <summary>The largest dictionary written.</summary>
  public const int Maximum = 1 << 30;

  /// <summary>Rounds <paramref name="size"/> up to the next recognised size, within the bounds.</summary>
  public static int Normalize(long size) {
    if (size <= Minimum) return Minimum;
    if (size >= Maximum) return Maximum; // 2^31 would overflow below

    var value = (int)size;
    var bits = 31 - int.LeadingZeroCount(value);
    var pow2 = 1 << bits;
    if (pow2 == value) return value;

    var threeHalves = 3 << (bits - 1);
    return threeHalves >= value ? threeHalves : 1 << (bits + 1);
  }
}
