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
/// <param name="MinimumBits">The smallest supported hash-output size, in bits.</param>
/// <param name="MaximumBits">The largest supported hash-output size, in bits.</param>
/// <param name="StepBits">The increment, in bits, between supported sizes.</param>
public readonly record struct HashSizeRange(int MinimumBits, int MaximumBits, int StepBits = 1) : IEnumerable<int> {
  /// <summary>
  /// Gets the smallest supported hash output size, in bits.
  /// </summary>
  public int MinimumBits { get; init; } = MinimumBits;

  /// <summary>
  /// Gets the largest supported hash output size, in bits.
  /// </summary>
  public int MaximumBits { get; init; } = MaximumBits;

  /// <summary>
  /// Gets the increment, in bits, between supported sizes in the range.
  /// </summary>
  public int StepBits { get; init; } = StepBits;

  /// <summary>
  /// Creates a <see cref="HashSizeRange"/> containing exactly one bit size.
  /// </summary>
  public static HashSizeRange Exact(int bits) => new(bits, bits);

  /// <summary>
  /// Determines whether the range contains the specified bit size.
  /// </summary>
  public bool Contains(int bits) =>
    StepBits > 0 &&
    MinimumBits > 0 &&
    MaximumBits >= MinimumBits &&
    bits >= MinimumBits &&
    bits <= MaximumBits &&
    (bits - MinimumBits) % StepBits == 0;

  /// <summary>
  /// Returns an enumerator over the bit sizes represented by the range.
  /// </summary>
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

  /// <summary>
  /// Enumerates the bit sizes represented by <see cref="HashSizeRange"/>.
  /// </summary>
  public struct Enumerator(HashSizeRange range) : IEnumerator<int> {
    private int _current = range.MinimumBits - range.StepBits;

    /// <summary>
    /// Gets the current bit size in the enumeration.
    /// </summary>
    public readonly int Current => _current;
    readonly object IEnumerator.Current => Current;

    /// <summary>
    /// Advances the enumerator to the next supported bit size.
    /// </summary>
    public bool MoveNext() {
      var next = _current + range.StepBits;
      if (next > range.MaximumBits)
        return false;
      _current = next;
      return true;
    }

    /// <summary>
    /// Resets the enumerator to its initial position.
    /// </summary>
    public void Reset() => _current = range.MinimumBits - range.StepBits;
    /// <summary>
    /// Releases resources associated with the enumerator.
    /// </summary>
    public readonly void Dispose() { }
  }
}

/// <summary>
/// Provides helpers for collections of <see cref="HashSizeRange"/> values.
/// </summary>
public static class HashSizeRangeExtensions {
  /// <summary>
  /// Determines whether any range in the collection contains the specified bit size.
  /// </summary>
  public static bool Supports(this IReadOnlyList<HashSizeRange> ranges, int bits) {
    ArgumentNullException.ThrowIfNull(ranges);
    for (var i = 0; i < ranges.Count; ++i)
      if (ranges[i].Contains(bits))
        return true;
    return false;
  }

  /// <summary>
  /// Enumerates every supported bit size represented by the supplied ranges.
  /// </summary>
  public static IEnumerable<int> EnumerateSizes(this IReadOnlyList<HashSizeRange> ranges) {
    ArgumentNullException.ThrowIfNull(ranges);
    for (var i = 0; i < ranges.Count; ++i)
      foreach (var bits in ranges[i])
        yield return bits;
  }
}
