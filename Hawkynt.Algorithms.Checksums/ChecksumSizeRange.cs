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
  public static ChecksumSizeRange Exact(int bits) => new(bits, bits);

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
      throw new InvalidOperationException("Checksum sizes must be positive.");
    if (MaximumBits < MinimumBits)
      throw new InvalidOperationException("MaximumBits must be greater than or equal to MinimumBits.");
    if (StepBits <= 0)
      throw new InvalidOperationException("StepBits must be positive.");
    if ((MaximumBits - MinimumBits) % StepBits != 0)
      throw new InvalidOperationException("The range end must be reachable by StepBits.");
  }

  public struct Enumerator(ChecksumSizeRange range) : IEnumerator<int> {
    private readonly ChecksumSizeRange _range = range;
    private int _current;
    private bool _started;

    public readonly int Current => _current;
    readonly object IEnumerator.Current => Current;

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

    public void Reset() {
      _current = default;
      _started = false;
    }

    public readonly void Dispose() { }
  }
}

public static class ChecksumSizeRangeExtensions {
  public static bool Supports(this IReadOnlyList<ChecksumSizeRange> ranges, int bits) {
    ArgumentNullException.ThrowIfNull(ranges);
    for (var i = 0; i < ranges.Count; ++i)
      if (ranges[i].Contains(bits))
        return true;
    return false;
  }

  public static IEnumerable<int> EnumerateSizes(this IReadOnlyList<ChecksumSizeRange> ranges) {
    ArgumentNullException.ThrowIfNull(ranges);
    for (var i = 0; i < ranges.Count; ++i)
      foreach (var bits in ranges[i])
        yield return bits;
  }
}
