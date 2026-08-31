using System.Collections;

namespace Hawkynt.Algorithms.Checksums;

/// <summary>
/// Describes a contiguous arithmetic range of supported checksum-output sizes, in bits.
/// </summary>
/// <remarks>
/// Checksum sizes are not required to be whole bytes; parity and generalized sub-byte families are legitimate.
/// A range with <c>MinimumBits == MaximumBits</c> represents one exact size. Families with
/// discontiguous valid sizes expose multiple ranges.
/// </remarks>
public readonly record struct ChecksumSizeRange(int MinimumBits, int MaximumBits, int StepBits = 1) : IEnumerable<int> {
  /// <summary>
  /// Gets the smallest supported checksum output size, in bits.
  /// </summary>
  public int MinimumBits { get; init; } = MinimumBits;

  /// <summary>
  /// Gets the largest supported checksum output size, in bits.
  /// </summary>
  public int MaximumBits { get; init; } = MaximumBits;

  /// <summary>
  /// Gets the increment, in bits, between supported sizes in the range.
  /// </summary>
  public int StepBits { get; init; } = StepBits;

  /// <summary>
  /// Creates a <see cref="ChecksumSizeRange"/> containing exactly one bit size.
  /// </summary>
  public static ChecksumSizeRange Exact(int bits) => new(bits, bits);

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
      throw new InvalidOperationException("Checksum sizes must be positive.");
    if (MaximumBits < MinimumBits)
      throw new InvalidOperationException("MaximumBits must be greater than or equal to MinimumBits.");
    if (StepBits <= 0)
      throw new InvalidOperationException("StepBits must be positive.");
    if ((MaximumBits - MinimumBits) % StepBits != 0)
      throw new InvalidOperationException("The range end must be reachable by StepBits.");
  }

  /// <summary>
  /// Enumerates the bit sizes represented by <see cref="ChecksumSizeRange"/>.
  /// </summary>
  public struct Enumerator(ChecksumSizeRange range) : IEnumerator<int> {
    private readonly ChecksumSizeRange _range = range;
    private int _current;
    private bool _started;

    /// <summary>
    /// Gets the current bit size in the enumeration.
    /// </summary>
    public readonly int Current => _current;
    readonly object IEnumerator.Current => Current;

    /// <summary>
    /// Advances the enumerator to the next supported bit size.
    /// </summary>
    public bool MoveNext() {
      if (!_started) {
        _current = _range.MinimumBits;
        _started = true;
        return true;
      }

      if (_current >= _range.MaximumBits)
        return false;

      _current += _range.StepBits;
      return true;
    }

    /// <summary>
    /// Resets the enumerator to its initial position.
    /// </summary>
    public void Reset() {
      _current = default;
      _started = false;
    }

    /// <summary>
    /// Releases resources associated with the enumerator.
    /// </summary>
    public readonly void Dispose() { }
  }
}

/// <summary>
/// Provides helpers for collections of <see cref="ChecksumSizeRange"/> values.
/// </summary>
public static class ChecksumSizeRangeExtensions {
  /// <summary>
  /// Determines whether any range in the collection contains the specified bit size.
  /// </summary>
  public static bool Supports(this IReadOnlyList<ChecksumSizeRange> ranges, int bits) {
    ArgumentNullException.ThrowIfNull(ranges);
    for (var i = 0; i < ranges.Count; ++i)
      if (ranges[i].Contains(bits))
        return true;
    return false;
  }

  /// <summary>
  /// Enumerates every supported bit size represented by the supplied ranges.
  /// </summary>
  public static IEnumerable<int> EnumerateSizes(this IReadOnlyList<ChecksumSizeRange> ranges) {
    ArgumentNullException.ThrowIfNull(ranges);
    for (var i = 0; i < ranges.Count; ++i)
      foreach (var bits in ranges[i])
        yield return bits;
  }
}
