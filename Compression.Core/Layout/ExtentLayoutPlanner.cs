#pragma warning disable CS1591
using System.Buffers;

namespace Compression.Core.Layout;

/// <summary>
/// One contiguous run of bytes the caller wants to keep alive across a layout
/// rewrite. Carries an opaque <see cref="Tag"/> so callers can correlate the
/// planner's output back to their own metadata (a directory record, an inode,
/// a FAT chain head, …) without the planner having to know what filesystem
/// it's serving.
/// </summary>
public sealed record class LiveExtent(long SourceOffset, long Length, object? Tag = null);

/// <summary>
/// One planned move from <see cref="SourceOffset"/> to <see cref="TargetOffset"/>
/// of <see cref="Length"/> bytes. <see cref="Tag"/> is copied from the
/// originating <see cref="LiveExtent"/> so the caller can update whatever
/// pointer references the moved range.
/// </summary>
public sealed record class ExtentMove(long SourceOffset, long TargetOffset, long Length, object? Tag);

/// <summary>
/// Filesystem-agnostic block-layout planner for filesystems that store byte
/// data interleaved with directory metadata (FAT, ISO 9660, HFS+, ext, etc.).
///
/// Use case: a file is removed, leaving a hole in the middle of an image. The
/// caller wants to close the hole. Two strategies are available:
///
/// <list type="bullet">
/// <item><b>PackFromOrigin</b> — full sequential repack. Every live extent is
/// laid out contiguously starting at the requested origin, in source order.
/// Already-correctly-placed prefix extents stay in place; the suffix that has
/// shifted gets moved. Result: minimum image size; total bytes moved equal
/// the suffix from the first hole onwards.</item>
/// <item><b>FillHolesBestFit</b> — lazy compaction. Each hole is filled with
/// a single tail extent that fits exactly (or under-fits, leaving a smaller
/// hole behind). Best-fit pairing minimises wasted space. Result: residual
/// fragmentation, but only the moved extents pay the byte-copy cost. Useful
/// when the caller cares more about wall-clock time than image size — e.g.,
/// removing one small file from a 4GB ISO.</item>
/// </list>
///
/// The planner is pure: it returns a list of <see cref="ExtentMove"/> and
/// optional residual hole list. It does not touch any stream. Filesystem-
/// specific code is responsible for:
/// <list type="number">
/// <item>Computing live extents (which byte ranges contain file data).</item>
/// <item>Calling one of the planner methods.</item>
/// <item>Updating directory records / FAT chains / inode pointers using the
/// <see cref="ExtentMove.Tag"/> back-reference.</item>
/// <item>Calling <see cref="ApplyMoves"/> (or rolling its own equivalent) to
/// shuffle bytes in the underlying stream.</item>
/// </list>
/// </summary>
public static class ExtentLayoutPlanner {

  /// <summary>
  /// Plans a move set that packs every live extent contiguously starting at
  /// <paramref name="origin"/>, in source-offset order. Extents already at
  /// their target offset are not emitted as moves (zero-cost when the layout
  /// is already partially correct). Total byte-cost: sum of lengths of every
  /// extent that lies past the first source-order hole.
  /// </summary>
  /// <param name="extents">Live extents to keep, in any order.</param>
  /// <param name="origin">Byte offset where the packed region starts.</param>
  /// <param name="alignment">Round each target offset up to this byte
  /// alignment (1 for byte-tight, 2048 for ISO-9660 sector-aligned, etc.).</param>
  /// <returns>The ordered move list. Empty if every extent is already in its
  /// target slot.</returns>
  public static IReadOnlyList<ExtentMove> PackFromOrigin(
    IReadOnlyList<LiveExtent> extents,
    long origin,
    long alignment = 1) {
    ArgumentNullException.ThrowIfNull(extents);
    if (origin < 0)
      throw new ArgumentOutOfRangeException(nameof(origin), "Origin must be non-negative.");
    if (alignment < 1)
      throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be >= 1.");

    var sorted = extents.OrderBy(static e => e.SourceOffset).ToArray();
    var moves = new List<ExtentMove>(sorted.Length);
    var cursor = AlignUp(origin, alignment);
    foreach (var extent in sorted) {
      if (extent.Length < 0)
        throw new ArgumentException($"Negative extent length at offset {extent.SourceOffset}.");
      if (extent.SourceOffset != cursor && extent.Length > 0)
        moves.Add(new ExtentMove(extent.SourceOffset, cursor, extent.Length, extent.Tag));
      cursor = AlignUp(cursor + extent.Length, alignment);
    }
    return moves;
  }

  /// <summary>
  /// Lazy compaction: fills holes with single tail extents that fit, in best-
  /// fit order. Extents that don't fit any hole stay in place. Result is a
  /// non-contiguous layout but moves the minimum number of bytes.
  ///
  /// Best-fit pairing: holes are walked largest-first; for each hole the
  /// largest tail extent that still fits is selected. An exact fit closes
  /// the hole entirely; an under-fit leaves a residual hole at
  /// <c>(holeOffset + extent.Length, hole.Length - extent.Length)</c>
  /// reported in the returned residual list.
  /// </summary>
  /// <param name="extents">Live extents. The "image" is treated as spanning
  /// from <paramref name="imageOrigin"/> to the highest extent's end.</param>
  /// <param name="imageOrigin">Byte offset where addressable image data
  /// begins (16 * 2048 for ISO 9660, 0 for raw FAT, etc.). Holes between
  /// <paramref name="imageOrigin"/> and the first extent's start are
  /// considered fillable; holes after the last extent are NOT (they're free
  /// space the caller can truncate or reuse).</param>
  /// <returns>Tuple of (moves, residual holes after the moves are applied).
  /// Residual holes are the still-unfilled gaps; if the caller insists on a
  /// contiguous result, they should follow up with <see cref="PackFromOrigin"/>.
  /// </returns>
  public static (IReadOnlyList<ExtentMove> Moves, IReadOnlyList<(long Offset, long Length)> ResidualHoles)
    FillHolesBestFit(IReadOnlyList<LiveExtent> extents, long imageOrigin) {
    ArgumentNullException.ThrowIfNull(extents);
    if (imageOrigin < 0)
      throw new ArgumentOutOfRangeException(nameof(imageOrigin));

    var sorted = extents.Where(e => e.Length > 0).OrderBy(static e => e.SourceOffset).ToList();

    // Identify holes between consecutive extents (and between origin and first extent).
    var holes = new List<(long Offset, long Length)>();
    var cursor = imageOrigin;
    foreach (var extent in sorted) {
      if (extent.SourceOffset > cursor)
        holes.Add((cursor, extent.SourceOffset - cursor));
      cursor = extent.SourceOffset + extent.Length;
    }

    if (holes.Count == 0)
      return ([], []);

    // Best-fit pairing: process holes largest-first so big tail extents have
    // somewhere to land before we waste them on a tiny gap.
    var moves = new List<ExtentMove>();
    var residual = new List<(long, long)>();
    var moved = new HashSet<LiveExtent>();
    var mutableHoles = holes.Select(h => (h.Offset, h.Length)).ToList();

    foreach (var hole in mutableHoles.OrderByDescending(static h => h.Length).ToList()) {
      // Find the largest live extent that
      //   (a) lives strictly past this hole (otherwise we'd be moving an
      //       extent backwards through itself)
      //   (b) fits in the hole
      //   (c) hasn't already been moved
      LiveExtent? best = null;
      foreach (var extent in sorted) {
        if (moved.Contains(extent)) continue;
        if (extent.SourceOffset <= hole.Offset) continue;
        if (extent.Length > hole.Length) continue;
        if (best is null || extent.Length > best.Length)
          best = extent;
      }
      if (best is null) {
        residual.Add(hole);
        continue;
      }
      moves.Add(new ExtentMove(best.SourceOffset, hole.Offset, best.Length, best.Tag));
      moved.Add(best);

      // Under-fit: the hole's tail becomes a smaller residual hole.
      if (best.Length < hole.Length)
        residual.Add((hole.Offset + best.Length, hole.Length - best.Length));
    }

    return (moves, residual);
  }

  /// <summary>
  /// Applies a planned move set to <paramref name="stream"/>. Moves are
  /// ordered to avoid source-before-target overwrite hazards: backward moves
  /// (target &lt; source) execute in ascending source order; forward moves
  /// (target &gt; source) execute in descending source order. Each move's
  /// bytes are read into a pooled buffer and rewritten at the new offset.
  ///
  /// <para>The caller is responsible for any final <see cref="Stream.SetLength"/>
  /// truncation if they want to drop trailing free space.</para>
  /// </summary>
  public static void ApplyMoves(Stream stream, IReadOnlyList<ExtentMove> moves) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(moves);
    if (moves.Count == 0) return;
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable to apply extent moves.", nameof(stream));

    // Backward first (low source first to avoid stomping yet-to-be-read tail).
    // Forward second (high source first to avoid stomping yet-to-be-read head).
    var backward = moves.Where(static m => m.TargetOffset < m.SourceOffset)
                        .OrderBy(static m => m.SourceOffset);
    var forward = moves.Where(static m => m.TargetOffset > m.SourceOffset)
                       .OrderByDescending(static m => m.SourceOffset);
    // Same-offset moves are no-ops.

    var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    try {
      foreach (var move in backward.Concat(forward))
        Copy(stream, move, buffer);
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  private static void Copy(Stream stream, ExtentMove move, byte[] buffer) {
    var remaining = move.Length;
    var src = move.SourceOffset;
    var dst = move.TargetOffset;
    while (remaining > 0) {
      var chunk = (int)Math.Min(remaining, buffer.Length);
      stream.Position = src;
      stream.ReadExactly(buffer, 0, chunk);
      stream.Position = dst;
      stream.Write(buffer, 0, chunk);
      src += chunk;
      dst += chunk;
      remaining -= chunk;
    }
  }

  private static long AlignUp(long value, long alignment)
    => alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;
}
