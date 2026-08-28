using System.Collections;

namespace Hawkynt.Algorithms.Hashing;

/// <summary>
/// Describes a contiguous arithmetic range of supported hash-output sizes, in bits.
/// </summary>
/// <remarks>
/// A range with <c>MinimumBits == MaximumBits</c> represents one exact size. Families with
/// discontiguous valid sizes expose multiple ranges. The range itself is enumerable so callers
/// can use the metadata the same way the JavaScript registry exposes <c>SupportedOutputSizes</c>.
/// </remarks>
public readonly record struct HashSizeRange(int MinimumBits, int MaximumBits, int StepBits = 1) : IEnumerable<int> {
  public static HashSizeRange Exact(int bits) => new(bits, bits);

  public bool Contains(int bits) =>
    StepBits > 0 &&
    MinimumBits > 0 &&
    MaximumBits >= MinimumBits &&
    bits >= MinimumBits &&
    bits <= MaximumBits &&
    (bits - MinimumBits) % StepBits == 0;

  public Enumerator GetEnumerator() {
    Validate();
    return new(this);
  }

  IEnumerator<int> IEnumerable<int>.GetEnumerator() => GetEnumerator();
  IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

  private void Validate() {
    if (MinimumBits <= 0)
      throw new InvalidOperationException("Hash sizes must be positive.");
    if (MaximumBits < MinimumBits)
      throw new InvalidOperationException("MaximumBits must be greater than or equal to MinimumBits.");
    if (StepBits <= 0)
      throw new InvalidOperationException("StepBits must be positive.");
    if ((MaximumBits - MinimumBits) % StepBits != 0)
      throw new InvalidOperationException("The range end must be reachable by StepBits.");
  }

  public struct Enumerator(HashSizeRange range) : IEnumerator<int> {
    private int _current = range.MinimumBits - range.StepBits;

    public readonly int Current => _current;
    readonly object IEnumerator.Current => Current;

    public bool MoveNext() {
      var next = _current + range.StepBits;
      if (next > range.MaximumBits)
        return false;
      _current = next;
      return true;
    }

    public void Reset() => _current = range.MinimumBits - range.StepBits;
    public readonly void Dispose() { }
  }
}

public static class HashSizeRangeExtensions {
  public static bool Supports(this IReadOnlyList<HashSizeRange> ranges, int bits) {
    ArgumentNullException.ThrowIfNull(ranges);
    for (var i = 0; i < ranges.Count; ++i)
      if (ranges[i].Contains(bits))
        return true;
    return false;
  }

  public static IEnumerable<int> EnumerateSizes(this IReadOnlyList<HashSizeRange> ranges) {
    ArgumentNullException.ThrowIfNull(ranges);
    for (var i = 0; i < ranges.Count; ++i)
      foreach (var bits in ranges[i])
        yield return bits;
  }
}
