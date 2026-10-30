namespace FileFormat.Uharc;

/// <summary>
/// Where a UHARC 0.6 solid stream resets its adaptive models (docs/UHARC-ON-DISK.md,
/// "Model resets"). The points are not stored: both sides derive them from the file sizes.
/// </summary>
/// <remarks>
/// From the current reset point the coder looks <see cref="LzpSpan"/> / <see cref="AlzSpan"/>
/// bytes ahead and resets at the first file end at or beyond that. LZP additionally moves the
/// point past a run of files that shrink after a file smaller than 16 KiB.
/// </remarks>
internal sealed class UharcModelResets {

  public const int LzpSpan = 0x10000;
  public const int AlzSpan = 0x20000;
  private const long SmallFile = 0x4000;

  private readonly IReadOnlyList<long> _ends;
  private readonly long _total;
  private readonly int _span;
  private readonly bool _mergeSmallFiles;
  private int _file;
  private long _next;

  public UharcModelResets(IReadOnlyList<long> fileEnds, long total, int span, bool mergeSmallFiles) {
    this._ends = fileEnds;
    this._total = total;
    this._span = span;
    this._mergeSmallFiles = mergeSmallFiles;
    this.Advance();
  }

  /// <summary>True when the models must be reset before decoding the byte at <paramref name="position"/>.</summary>
  public bool IsResetPoint(long position) {
    if (position < this._next)
      return false;

    this.Advance();
    return true;
  }

  private void Advance() {
    var target = this._total - this._next > this._span ? this._next + this._span : this._total;
    var count = this._ends.Count;
    while (this._file < count && this._ends[this._file] < target)
      ++this._file;

    if (this._file >= count) {
      this._next = this._total;
      return;
    }

    this._next = this._ends[this._file];
    if (!this._mergeSmallFiles || this._file == 0 || this._file >= count - 1)
      return;

    var size = this._ends[this._file] - this._ends[this._file - 1];
    if (size >= SmallFile)
      return;

    while (this._file < count - 1) {
      var following = this._ends[this._file + 1] - this._ends[this._file];
      if (following >= size)
        break;

      ++this._file;
      this._next = this._ends[this._file];
      size = following;
    }
  }
}
