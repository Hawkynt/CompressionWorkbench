#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Core.Layout;

/// <summary>
/// Planning result from <see cref="FilesystemDefragmentor.Plan"/>.
/// </summary>
/// <param name="Moves">The byte-range moves that, when applied, achieve the
/// planned layout. Apply via <see cref="ExtentLayoutPlanner.ApplyMoves"/>.</param>
/// <param name="ResidualHoles">Holes that still exist after the plan is
/// applied. Empty for <see cref="DefragMode.ConsolidateAtStart"/> /
/// <see cref="DefragMode.ConsolidateAtEnd"/> (those guarantee contiguity).</param>
/// <param name="CarvedHoles">Holes that were intentionally created. Equals
/// the requested region for <see cref="DefragMode.CarveHole"/>; equals the
/// trailing free region for <see cref="DefragMode.ConsolidateAtStart"/>.</param>
/// <param name="FinalImageLength">Image length after the moves are applied
/// (caller can pass to <c>Stream.SetLength</c> if they want to truncate
/// trailing free space).</param>
/// <param name="TotalBytesMoved">Sum of move lengths — the actual byte cost
/// of executing this plan. Useful for picking between modes.</param>
public sealed record class DefragPlan(
  IReadOnlyList<ExtentMove> Moves,
  IReadOnlyList<(long Offset, long Length)> ResidualHoles,
  IReadOnlyList<(long Offset, long Length)> CarvedHoles,
  long FinalImageLength,
  long TotalBytesMoved
);

/// <summary>
/// Filesystem-agnostic defragmentor. Composes <see cref="ExtentLayoutPlanner"/>
/// into named, useful, mode-driven workflows. Like the planner itself, this
/// is pure logic — it returns moves; the caller decides whether to apply them.
///
/// <para>The intended pipeline:</para>
/// <list type="number">
/// <item>Filesystem-specific code walks its directory structures and produces
/// a <see cref="LiveExtent"/> per file (with <see cref="LiveExtent.Tag"/>
/// set to the originating directory record / FAT chain head).</item>
/// <item>Caller invokes <see cref="Plan"/> with chosen <see cref="DefragOptions"/>.</item>
/// <item>Caller iterates <see cref="DefragPlan.Moves"/> and updates each
/// pointer that referenced the old offset, using the move's <c>Tag</c> to
/// identify which directory record / inode to patch.</item>
/// <item>Caller invokes <see cref="ExtentLayoutPlanner.ApplyMoves"/> on the
/// underlying stream to perform the byte-level shuffling.</item>
/// <item>Caller optionally truncates the stream to <c>FinalImageLength</c>.</item>
/// </list>
/// </summary>
public static class FilesystemDefragmentor {

  /// <summary>
  /// Plans a defragmentation pass over <paramref name="extents"/> using the
  /// chosen <paramref name="options"/>.
  /// </summary>
  public static DefragPlan Plan(IReadOnlyList<LiveExtent> extents, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(extents);
    ArgumentNullException.ThrowIfNull(options);
    if (options.Origin < 0)
      throw new ArgumentOutOfRangeException(nameof(options), "Origin must be non-negative.");
    if (options.Alignment < 1)
      throw new ArgumentOutOfRangeException(nameof(options), "Alignment must be >= 1.");

    return options.Mode switch {
      DefragMode.ConsolidateAtStart => PlanConsolidateAtStart(extents, options),
      DefragMode.ConsolidateAtEnd => PlanConsolidateAtEnd(extents, options),
      DefragMode.FillHolesLazy => PlanFillHolesLazy(extents, options),
      DefragMode.CarveHole => PlanCarveHole(extents, options),
      _ => throw new ArgumentException($"Unsupported mode: {options.Mode}", nameof(options)),
    };
  }

  private static DefragPlan PlanConsolidateAtStart(IReadOnlyList<LiveExtent> extents, DefragOptions opts) {
    var moves = ExtentLayoutPlanner.PackFromOrigin(extents, opts.Origin, opts.Alignment);
    var totalLive = SumLengths(extents);
    var packedEnd = AlignUp(opts.Origin + totalLive, opts.Alignment);
    var imageEnd = opts.ImageEnd < 0 ? AutoEnd(extents) : opts.ImageEnd;
    var trailingFree = packedEnd < imageEnd
      ? new[] { ((long Offset, long Length))(packedEnd, imageEnd - packedEnd) }
      : [];
    return new DefragPlan(
      Moves: moves,
      ResidualHoles: [],
      CarvedHoles: trailingFree,
      FinalImageLength: packedEnd,
      TotalBytesMoved: TotalCost(moves)
    );
  }

  private static DefragPlan PlanConsolidateAtEnd(IReadOnlyList<LiveExtent> extents, DefragOptions opts) {
    var imageEnd = opts.ImageEnd < 0 ? AutoEnd(extents) : opts.ImageEnd;
    if (imageEnd < opts.Origin)
      throw new ArgumentException("ImageEnd must be >= Origin.", nameof(opts));

    // Sort by source so target slots in same source-order, just shifted to the end.
    var sorted = extents.Where(e => e.Length > 0).OrderBy(static e => e.SourceOffset).ToArray();
    var totalLive = sorted.Sum(static e => e.Length);
    if (totalLive > imageEnd - opts.Origin)
      throw new ArgumentException("Total live data exceeds available image span.", nameof(opts));

    // Cursor walks backward from imageEnd; assign each extent's tail to imageEnd, ...
    // To respect alignment naturally, we lay out forward starting from
    // (imageEnd - totalLive) rounded up to alignment.
    var packStart = AlignUp(imageEnd - totalLive, opts.Alignment);
    var cursor = packStart;
    var moves = new List<ExtentMove>(sorted.Length);
    foreach (var extent in sorted) {
      if (extent.SourceOffset != cursor)
        moves.Add(new ExtentMove(extent.SourceOffset, cursor, extent.Length, extent.Tag));
      cursor = AlignUp(cursor + extent.Length, opts.Alignment);
    }

    var leadingFree = packStart > opts.Origin
      ? new[] { ((long Offset, long Length))(opts.Origin, packStart - opts.Origin) }
      : [];
    return new DefragPlan(
      Moves: moves,
      ResidualHoles: [],
      CarvedHoles: leadingFree,
      FinalImageLength: imageEnd,
      TotalBytesMoved: TotalCost(moves)
    );
  }

  private static DefragPlan PlanFillHolesLazy(IReadOnlyList<LiveExtent> extents, DefragOptions opts) {
    var (moves, residual) = ExtentLayoutPlanner.FillHolesBestFit(extents, opts.Origin);
    var imageEnd = opts.ImageEnd < 0 ? AutoEnd(extents) : opts.ImageEnd;
    return new DefragPlan(
      Moves: moves,
      ResidualHoles: residual,
      CarvedHoles: [],
      FinalImageLength: imageEnd,
      TotalBytesMoved: TotalCost(moves)
    );
  }

  private static DefragPlan PlanCarveHole(IReadOnlyList<LiveExtent> extents, DefragOptions opts) {
    if (opts.HoleSize <= 0)
      throw new ArgumentException("HoleSize must be positive for CarveHole.", nameof(opts));

    var sorted = extents.Where(e => e.Length > 0).OrderBy(static e => e.SourceOffset).ToList();
    var imageEnd = opts.ImageEnd < 0 ? AutoEnd(extents) : opts.ImageEnd;

    // Auto-pick: carve at the end (just past the highest live extent, aligned).
    var holeAt = opts.HoleAt < 0
      ? AlignUp(sorted.Count == 0 ? opts.Origin : sorted[^1].SourceOffset + sorted[^1].Length, opts.Alignment)
      : opts.HoleAt;
    var holeEnd = holeAt + opts.HoleSize;

    if (holeAt < opts.Origin)
      throw new ArgumentException("Carved hole starts before image origin.", nameof(opts));

    // Collect every extent whose byte-range intersects [holeAt, holeEnd).
    var displaced = sorted.Where(e =>
        e.SourceOffset < holeEnd && e.SourceOffset + e.Length > holeAt).ToList();
    var keepers = sorted.Where(e =>
        e.SourceOffset >= holeEnd || e.SourceOffset + e.Length <= holeAt).ToList();

    if (displaced.Count == 0) {
      // Nothing in the way. Image already has a hole there (or empty space past last extent).
      var finalEnd = Math.Max(imageEnd, holeEnd);
      return new DefragPlan(
        Moves: [],
        ResidualHoles: [],
        CarvedHoles: new[] { (holeAt, opts.HoleSize) },
        FinalImageLength: finalEnd,
        TotalBytesMoved: 0
      );
    }

    // Append displaced extents past the carved region, in source order. Keepers
    // are unmoved. Final image grows if needed to fit the displaced run.
    var moves = new List<ExtentMove>();
    var cursor = AlignUp(holeEnd, opts.Alignment);
    // Skip past any keepers that already live in the post-hole region — we
    // append AFTER the last keeper to avoid trampling them.
    var lastKeeperEnd = keepers
      .Where(e => e.SourceOffset >= holeEnd)
      .Select(e => AlignUp(e.SourceOffset + e.Length, opts.Alignment))
      .DefaultIfEmpty(cursor)
      .Max();
    cursor = Math.Max(cursor, lastKeeperEnd);

    foreach (var ext in displaced) {
      moves.Add(new ExtentMove(ext.SourceOffset, cursor, ext.Length, ext.Tag));
      cursor = AlignUp(cursor + ext.Length, opts.Alignment);
    }

    var finalLen = Math.Max(imageEnd, cursor);
    return new DefragPlan(
      Moves: moves,
      ResidualHoles: [],
      CarvedHoles: new[] { (holeAt, opts.HoleSize) },
      FinalImageLength: finalLen,
      TotalBytesMoved: TotalCost(moves)
    );
  }

  private static long SumLengths(IReadOnlyList<LiveExtent> extents) {
    long total = 0;
    foreach (var e in extents)
      if (e.Length > 0)
        total += e.Length;
    return total;
  }

  private static long AutoEnd(IReadOnlyList<LiveExtent> extents) {
    long max = 0;
    foreach (var e in extents) {
      var end = e.SourceOffset + e.Length;
      if (end > max) max = end;
    }
    return max;
  }

  private static long TotalCost(IReadOnlyList<ExtentMove> moves) {
    long total = 0;
    foreach (var m in moves) total += m.Length;
    return total;
  }

  private static long AlignUp(long value, long alignment)
    => alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;
}
