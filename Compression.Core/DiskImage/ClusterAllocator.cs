namespace Compression.Core.DiskImage;

/// <summary>
/// Bitmap-backed cluster allocator for filesystem writers. Allocates contiguous
/// cluster runs (first-fit); falls back to an automatic fast-defrag when no
/// contiguous hole is big enough but the total free space suffices. Tracks freed
/// clusters so they can be reused on subsequent allocations.
/// <para>
/// Writers keep their own on-disk bitmap representation — this helper manages an
/// in-memory view that's flushed to that bitmap by the writer. Decoupling means
/// the allocator doesn't need to understand per-filesystem bitmap formats; it
/// just exposes Allocate / Free / FastConsolidate over an opaque cluster index
/// space <c>[0..clusterCount)</c>.
/// </para>
/// <para>
/// Fast defrag notes: when <see cref="AllocateRun"/> can't find a contiguous run
/// of the requested size but the total free count is sufficient, it invokes
/// <see cref="FastConsolidate"/>, which asks the caller (via the
/// <c>relocateCluster</c> callback) to move allocated clusters out of the way.
/// The allocator never touches on-disk bytes itself — the caller does the
/// byte-level copy and then reports success by returning the new cluster index.
/// </para>
/// </summary>
public sealed class ClusterAllocator {
  private readonly bool[] _used;
  private int _hint;

  /// <summary>Total cluster count under management.</summary>
  public int ClusterCount => this._used.Length;

  /// <summary>Count of free clusters remaining.</summary>
  public int FreeCount { get; private set; }

  /// <summary>Builds an allocator managing <paramref name="clusterCount"/> clusters, all initially free.</summary>
  public ClusterAllocator(int clusterCount) {
    if (clusterCount <= 0) throw new ArgumentOutOfRangeException(nameof(clusterCount));
    this._used = new bool[clusterCount];
    this.FreeCount = clusterCount;
  }

  /// <summary>Marks <paramref name="cluster"/> as used; used by writers during initial setup
  /// to exclude system areas (boot sector region, FAT area, MFT area) before user data.</summary>
  public void Reserve(int cluster) {
    if (cluster < 0 || cluster >= this._used.Length)
      throw new ArgumentOutOfRangeException(nameof(cluster));
    if (this._used[cluster]) return;
    this._used[cluster] = true;
    --this.FreeCount;
  }

  /// <summary>Marks <paramref name="count"/> clusters starting at <paramref name="start"/> as used.</summary>
  public void ReserveRange(int start, int count) {
    for (var i = 0; i < count; ++i) this.Reserve(start + i);
  }

  /// <summary>
  /// Allocates a contiguous run of <paramref name="count"/> free clusters. Returns the
  /// starting cluster index, or <c>-1</c> if allocation failed even after fast-defrag
  /// attempts. Caller is responsible for writing data to the returned run.
  /// </summary>
  /// <param name="count">Number of clusters to allocate.</param>
  /// <param name="relocateCluster">Called during fast-defrag to relocate an existing
  /// allocated cluster. The callback receives <c>(fromCluster, toCluster)</c>, moves
  /// bytes on disk, and returns <c>true</c> on success (the allocator then updates the
  /// bitmap). If null, fast-defrag is disabled and allocation fails when no contiguous
  /// hole fits.</param>
  public int AllocateRun(int count, Func<int, int, bool>? relocateCluster = null) {
    if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
    var start = this.FindContiguousRun(count);
    if (start >= 0) {
      this.MarkUsed(start, count);
      return start;
    }

    // No contiguous hole — is there enough total free space to make room?
    if (this.FreeCount < count || relocateCluster == null) return -1;

    if (!this.FastConsolidate(count, relocateCluster)) return -1;

    start = this.FindContiguousRun(count);
    if (start < 0) return -1;
    this.MarkUsed(start, count);
    return start;
  }

  /// <summary>
  /// Frees a previously-allocated cluster, returning it to the pool for reuse.
  /// </summary>
  public void Free(int cluster) {
    if (cluster < 0 || cluster >= this._used.Length)
      throw new ArgumentOutOfRangeException(nameof(cluster));
    if (!this._used[cluster]) return;
    this._used[cluster] = false;
    ++this.FreeCount;
    if (cluster < this._hint) this._hint = cluster;
  }

  /// <summary>Frees <paramref name="count"/> clusters starting at <paramref name="start"/>.</summary>
  public void FreeRange(int start, int count) {
    for (var i = 0; i < count; ++i) this.Free(start + i);
  }

  /// <summary>Returns true if <paramref name="cluster"/> is currently allocated.</summary>
  public bool IsUsed(int cluster) => this._used[cluster];

  /// <summary>
  /// Consolidates enough free clusters to produce a contiguous run of
  /// <paramref name="neededRunSize"/>. Uses compact-left: scans for the leftmost
  /// free slot and moves the nearest used cluster from further right into it,
  /// which grows the trailing free run by one each iteration. Stops as soon as
  /// the tail run is long enough.
  /// </summary>
  private bool FastConsolidate(int neededRunSize, Func<int, int, bool> relocateCluster) {
    while (true) {
      var tailFree = this.TrailingFreeRunLength();
      if (tailFree >= neededRunSize) return true;

      // Find leftmost free slot lying before the tail free run — we'll fill it with
      // a used cluster from further right, growing the tail run by one.
      var cutoff = this._used.Length - tailFree;
      var leftmostFree = -1;
      for (var i = 0; i < cutoff; ++i)
        if (!this._used[i]) { leftmostFree = i; break; }
      if (leftmostFree < 0) return false;

      var victim = -1;
      for (var i = leftmostFree + 1; i < cutoff; ++i)
        if (this._used[i]) { victim = i; break; }
      if (victim < 0) return false;

      if (!relocateCluster(victim, leftmostFree)) return false;
      this._used[leftmostFree] = true;
      this._used[victim] = false;
      if (victim < this._hint) this._hint = victim;
    }
  }

  private int TrailingFreeRunLength() {
    var run = 0;
    for (var i = this._used.Length - 1; i >= 0 && !this._used[i]; --i) ++run;
    return run;
  }

  private int FindContiguousRun(int count) {
    var start = this._hint;
    var run = 0;
    for (var i = start; i < this._used.Length; ++i) {
      if (this._used[i]) { run = 0; continue; }
      if (++run == count) return i - count + 1;
    }
    // Wrap around in case the hint skipped earlier holes.
    run = 0;
    for (var i = 0; i < start; ++i) {
      if (this._used[i]) { run = 0; continue; }
      if (++run == count) return i - count + 1;
    }
    return -1;
  }

  private (int Start, int Length) FindLargestFreeRun() {
    var bestStart = -1; var bestLength = 0;
    var curStart = -1; var curLength = 0;
    for (var i = 0; i < this._used.Length; ++i) {
      if (!this._used[i]) {
        if (curStart < 0) curStart = i;
        ++curLength;
        if (curLength > bestLength) { bestStart = curStart; bestLength = curLength; }
      } else {
        curStart = -1; curLength = 0;
      }
    }
    return (bestStart, bestLength);
  }

  private int FindFreeClusterFrom(int from) {
    for (var i = from; i < this._used.Length; ++i)
      if (!this._used[i]) return i;
    return -1;
  }

  private void MarkUsed(int start, int count) {
    for (var i = 0; i < count; ++i) this._used[start + i] = true;
    this.FreeCount -= count;
    this._hint = start + count;
  }
}
