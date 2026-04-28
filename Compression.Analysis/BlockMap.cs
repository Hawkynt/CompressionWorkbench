namespace Compression.Analysis;

/// <summary>
/// Per-block ownership map of a binary file. Each block (default 4 KB) records
/// a stack of FormatIds — outermost first, innermost last — so callers can
/// see which container/filesystem owns a region and at which depth.
/// <para>
/// Backed by a flat <c>List&lt;string&gt;[]</c> indexed by block index.
/// Memory cost is roughly <c>BlockCount * (refsize + per-stack)</c> — for
/// a 1 GB file at 4 KB blocks that's ~262 144 entries which is well within
/// reach for desktop tooling.
/// </para>
/// <para>
/// <b>Frame caveat:</b> when populating from <see cref="NestedHit"/> children
/// via <see cref="MarkRecursive"/>, the child <see cref="NestedHit.ByteOffset"/>
/// frame is ambiguous (see <see cref="NestedHit"/>'s docs). This implementation
/// treats every offset as absolute on the host stream — that matches the
/// output of <see cref="RecursiveFilesystemCarver"/> when its underlying carver
/// happens to emit absolute offsets but will misplace nested entries when the
/// carver emits parent-payload-relative offsets. For a more accurate map,
/// resolve offsets in the caller before invoking <see cref="Mark"/>.
/// </para>
/// </summary>
public sealed class BlockMap {

  private readonly List<string>[] _owners;

  /// <summary>
  /// Creates a new empty map covering <paramref name="totalBytes"/> bytes,
  /// chunked into <paramref name="blockSize"/>-sized blocks.
  /// </summary>
  public BlockMap(long totalBytes, int blockSize = 4096) {
    if (totalBytes < 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));
    if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize));

    this.TotalBytes = totalBytes;
    this.BlockSize = blockSize;
    this._owners = new List<string>[this.BlockCount];
  }

  /// <summary>Total covered bytes (= the host stream length).</summary>
  public long TotalBytes { get; }

  /// <summary>Bytes per block (default 4096).</summary>
  public int BlockSize { get; }

  /// <summary>Total number of blocks (rounded up).</summary>
  public int BlockCount => (int)((this.TotalBytes + this.BlockSize - 1) / this.BlockSize);

  /// <summary>
  /// Returns the owner stack at <paramref name="blockIndex"/>: outermost first,
  /// innermost last. Empty list = unmapped.
  /// </summary>
  public IReadOnlyList<string> GetOwnerStack(int blockIndex) {
    if (blockIndex < 0 || blockIndex >= this.BlockCount) return [];
    return (IReadOnlyList<string>?)this._owners[blockIndex] ?? [];
  }

  /// <summary>
  /// Marks <paramref name="lengthBytes"/> bytes starting at <paramref name="offsetBytes"/>
  /// as owned by <paramref name="formatId"/>. Appends to each block's owner
  /// stack rather than replacing — call order determines depth.
  /// </summary>
  public void Mark(long offsetBytes, long lengthBytes, string formatId) {
    ArgumentNullException.ThrowIfNull(formatId);
    if (lengthBytes <= 0) return;
    if (offsetBytes >= this.TotalBytes) return;

    var clampedStart = Math.Max(0, offsetBytes);
    var clampedEnd = Math.Min(this.TotalBytes, offsetBytes + lengthBytes);
    if (clampedEnd <= clampedStart) return;

    var firstBlock = (int)(clampedStart / this.BlockSize);
    var lastBlock = (int)((clampedEnd - 1) / this.BlockSize);

    for (var i = firstBlock; i <= lastBlock; ++i) {
      var stack = this._owners[i] ??= [];
      stack.Add(formatId);
    }
  }

  /// <summary>
  /// Walks <paramref name="hits"/> and their <see cref="NestedHit.Children"/>
  /// in depth-first preorder, marking each block. Outer hits are pushed first
  /// so the innermost layer is the last entry of each block's owner stack.
  /// <para>
  /// See the class doc for the offset-frame caveat. This method assumes every
  /// offset is absolute on the host stream — pre-resolve if your carver emits
  /// parent-payload-relative offsets.
  /// </para>
  /// </summary>
  public void MarkRecursive(IReadOnlyList<NestedHit> hits) {
    ArgumentNullException.ThrowIfNull(hits);
    foreach (var h in hits) MarkOne(h);
  }

  private void MarkOne(NestedHit h) {
    Mark(h.ByteOffset, h.Length, h.FormatId);
    foreach (var c in h.Children) MarkOne(c);
  }

  /// <summary>
  /// Histogram of block ownership: <c>FormatId → block-count</c>. Counts every
  /// occurrence in every block's stack — a block owned by both <c>Qcow2</c> and
  /// <c>Fat</c> contributes one to each.
  /// </summary>
  public IReadOnlyDictionary<string, int> CountByFormat() {
    var dict = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var stack in this._owners) {
      if (stack is null) continue;
      foreach (var fmt in stack) {
        dict.TryGetValue(fmt, out var n);
        dict[fmt] = n + 1;
      }
    }
    return dict;
  }

  /// <summary>
  /// Maximum depth observed in any block's stack. Useful for renderers that
  /// need to allocate per-layer rows.
  /// </summary>
  public int MaxDepth {
    get {
      var max = 0;
      foreach (var stack in this._owners) {
        if (stack is null) continue;
        if (stack.Count > max) max = stack.Count;
      }
      return max;
    }
  }
}
