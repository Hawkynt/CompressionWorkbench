namespace FileSystem.Zfs;

/// <summary>Disjoint, sorted <c>[start, end)</c> byte ranges: the allocated space of one metaslab.</summary>
internal sealed class ZfsRangeSet {
  private readonly List<(long Start, long End)> _ranges = [];

  public IReadOnlyList<(long Start, long End)> Ranges => this._ranges;

  public long Total {
    get {
      long total = 0;
      foreach (var (start, end) in this._ranges) total += end - start;
      return total;
    }
  }

  public ZfsRangeSet Clone() {
    var copy = new ZfsRangeSet();
    copy._ranges.AddRange(this._ranges);
    return copy;
  }

  /// <summary>Index of the first range whose end lies after <paramref name="offset"/>.</summary>
  private int FirstEndingAfter(long offset) {
    int lo = 0, hi = this._ranges.Count;
    while (lo < hi) {
      var mid = (lo + hi) >> 1;
      if (this._ranges[mid].End <= offset) lo = mid + 1;
      else hi = mid;
    }
    return lo;
  }

  public bool Overlaps(long start, long end) {
    var i = this.FirstEndingAfter(start);
    return i < this._ranges.Count && this._ranges[i].Start < end;
  }

  public bool Contains(long start, long end) {
    var i = this.FirstEndingAfter(start);
    return i < this._ranges.Count && this._ranges[i].Start <= start && this._ranges[i].End >= end;
  }

  /// <summary>Adds a range that overlaps nothing already held.</summary>
  /// <exception cref="InvalidDataException">The range is (partly) held already.</exception>
  public void Add(long start, long end) {
    if (end <= start) return;
    if (this.Overlaps(start, end))
      throw new InvalidDataException($"ZFS: space [{start:X}, {end:X}) is allocated twice.");
    var i = this.FirstEndingAfter(start);
    var mergeLeft = i > 0 && this._ranges[i - 1].End == start;
    var mergeRight = i < this._ranges.Count && this._ranges[i].Start == end;
    if (mergeLeft && mergeRight) {
      this._ranges[i - 1] = (this._ranges[i - 1].Start, this._ranges[i].End);
      this._ranges.RemoveAt(i);
    } else if (mergeLeft)
      this._ranges[i - 1] = (this._ranges[i - 1].Start, end);
    else if (mergeRight)
      this._ranges[i] = (start, this._ranges[i].End);
    else
      this._ranges.Insert(i, (start, end));
  }

  /// <summary>Removes a range that is wholly held.</summary>
  /// <exception cref="InvalidDataException">Part of the range is not held.</exception>
  public void Remove(long start, long end) {
    if (end <= start) return;
    var i = this.FirstEndingAfter(start);
    if (i >= this._ranges.Count || this._ranges[i].Start > start || this._ranges[i].End < end)
      throw new InvalidDataException($"ZFS: space [{start:X}, {end:X}) is freed but was not allocated.");
    var (s, e) = this._ranges[i];
    if (s == start && e == end) this._ranges.RemoveAt(i);
    else if (s == start) this._ranges[i] = (end, e);
    else if (e == end) this._ranges[i] = (s, start);
    else {
      this._ranges[i] = (s, start);
      this._ranges.Insert(i + 1, (end, e));
    }
  }

  /// <summary>The lowest <paramref name="alignment"/>-aligned start of a free gap of <paramref name="size"/> bytes in <c>[from, to)</c>.</summary>
  public long? FindGap(long from, long to, long size, long alignment) {
    var at = Align(from, alignment);
    var i = this.FirstEndingAfter(at);
    while (at + size <= to) {
      if (i >= this._ranges.Count || this._ranges[i].Start >= at + size) return at;
      at = Align(this._ranges[i].End, alignment);
      ++i;
    }
    return null;
  }

  private static long Align(long value, long alignment) => (value + alignment - 1) / alignment * alignment;
}
