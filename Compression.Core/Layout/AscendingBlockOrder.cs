#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Core.Layout;

/// <summary>
/// What one owner's block order looks like: how many blocks it has, and where —
/// if anywhere — a block sits at a lower address than the one it follows.
/// </summary>
/// <param name="Owner">The owner the reading is about.</param>
/// <param name="Blocks">Blocks the owner holds, counted in logical order.</param>
/// <param name="Runs">Stretches those blocks break into. One is contiguous.</param>
/// <param name="Descents">Places where the next block is not above the last.</param>
/// <param name="FirstDescentFrom">Address of the block the first descent leaves,
/// or -1 when there is none.</param>
/// <param name="FirstDescentTo">Address the first descent lands on, or -1.</param>
/// <param name="FirstDescentIndex">Logical index of the block that descends, or -1.</param>
public sealed record class BlockOrderReading(
    string Owner,
    int Blocks,
    int Runs,
    int Descents,
    long FirstDescentFrom,
    long FirstDescentTo,
    int FirstDescentIndex) {

  /// <summary>Whether every block sits above the one it follows.</summary>
  public bool Ascends => this.Descents == 0;

  /// <summary>Whether the owner is one uninterrupted stretch.</summary>
  public bool Contiguous => this.Runs <= 1;

  /// <summary>A sentence naming what is wrong, for a failure message.</summary>
  public override string ToString()
    => this.Ascends
      ? $"'{this.Owner}': {this.Blocks} block(s) in {this.Runs} ascending run(s)"
      : $"'{this.Owner}': block {this.FirstDescentIndex} sits at {this.FirstDescentTo:N0}, below " +
        $"block {this.FirstDescentIndex - 1} at {this.FirstDescentFrom:N0} — a sequential read seeks " +
        $"backwards here ({this.Descents} such place(s) over {this.Blocks} block(s))";
}

/// <summary>
/// The ordering property a placement and a defragmentation both promise:
/// reading an owner from start to finish never seeks backwards.
/// </summary>
/// <remarks>
/// <para>Formally, over an owner's own blocks taken in logical order,
/// <c>block(n) &gt; block(n-1)</c>. It is strictly weaker than contiguity —
/// every contiguous owner ascends, but an owner split around a bad block or a
/// reserved table still ascends — which is the whole point of naming it. Where
/// full contiguity cannot be reached, ascending order is still worth something,
/// and it can often be reached by moving blocks forward into free space, with
/// nothing ever lifted out of the volume.</para>
///
/// <para>Logical order is the order an extent map yields an owner's runs: the
/// maps walk each owner's cluster or block chain run by run, so the map's order
/// is the owner's own order and not the address order. Sorting by address first
/// would make the property vacuously true and prove nothing.</para>
/// </remarks>
public static class AscendingBlockOrder {

  /// <summary>
  /// Reads <paramref name="owner" />'s block order out of a layout.
  /// </summary>
  /// <param name="layout">A layout as an extent map yields it — chain order per
  /// owner, which is what makes the reading meaningful.</param>
  /// <param name="owner">The owner to read. Matched case-insensitively.</param>
  /// <param name="blockSize">One allocation block in bytes. Zero compares whole
  /// runs instead, which answers the same question: blocks inside a run ascend
  /// by construction, so only the run boundaries can descend.</param>
  /// <returns>The reading, with <see cref="BlockOrderReading.Blocks" /> zero
  /// when the layout holds nothing for that owner.</returns>
  public static BlockOrderReading Read(IReadOnlyList<DefragBlockInfo> layout, string owner,
      int blockSize = 0) {
    ArgumentNullException.ThrowIfNull(layout);
    ArgumentNullException.ThrowIfNull(owner);

    var blocks = 0;
    var runs = 0;
    var descents = 0;
    var previous = long.MinValue;
    var previousEnd = long.MinValue;
    var firstFrom = -1L;
    var firstTo = -1L;
    var firstIndex = -1;

    foreach (var extent in layout) {
      if (extent.Kind != DefragBlockKind.Used) continue;
      if (!string.Equals(extent.FileName ?? "<unknown>", owner, StringComparison.OrdinalIgnoreCase)) continue;
      if (extent.Length <= 0) continue;

      if (extent.Offset != previousEnd) ++runs;
      previousEnd = extent.Offset + extent.Length;

      var step = blockSize > 0 ? blockSize : extent.Length;
      var count = (extent.Length + step - 1) / step;
      for (var index = 0L; index < count; ++index) {
        var at = extent.Offset + index * step;
        if (blocks > 0 && at <= previous) {
          ++descents;
          if (firstIndex < 0) {
            firstFrom = previous;
            firstTo = at;
            firstIndex = blocks;
          }
        }
        previous = at;
        ++blocks;
      }
    }

    return new BlockOrderReading(owner, blocks, runs, descents, firstFrom, firstTo, firstIndex);
  }

  /// <summary>Every owner the layout holds, read the same way.</summary>
  public static IReadOnlyList<BlockOrderReading> ReadAll(IReadOnlyList<DefragBlockInfo> layout,
      int blockSize = 0) {
    ArgumentNullException.ThrowIfNull(layout);
    var owners = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var extent in layout) {
      if (extent.Kind != DefragBlockKind.Used) continue;
      var owner = extent.FileName ?? "<unknown>";
      if (seen.Add(owner)) owners.Add(owner);
    }

    var readings = new List<BlockOrderReading>(owners.Count);
    foreach (var owner in owners) readings.Add(Read(layout, owner, blockSize));
    return readings;
  }

  /// <summary>Whether one owner's blocks ascend.</summary>
  public static bool Holds(IReadOnlyList<DefragBlockInfo> layout, string owner, int blockSize = 0)
    => Read(layout, owner, blockSize).Ascends;

  /// <summary>Whether every owner in the layout ascends.</summary>
  public static bool HoldsForAll(IReadOnlyList<DefragBlockInfo> layout, int blockSize = 0) {
    foreach (var reading in ReadAll(layout, blockSize))
      if (!reading.Ascends) return false;
    return true;
  }

  /// <summary>
  /// The owners that break the property, in the order the layout lists them.
  /// Empty when it holds throughout.
  /// </summary>
  public static IReadOnlyList<BlockOrderReading> Violations(IReadOnlyList<DefragBlockInfo> layout,
      int blockSize = 0) {
    var bad = new List<BlockOrderReading>();
    foreach (var reading in ReadAll(layout, blockSize))
      if (!reading.Ascends) bad.Add(reading);
    return bad;
  }

  /// <summary>
  /// Throws naming the first owner that reads backwards. Used by the planners
  /// to refuse a layout rather than write one that is worse than what it
  /// replaced.
  /// </summary>
  public static void Require(IReadOnlyList<DefragBlockInfo> layout, string what, int blockSize = 0) {
    var bad = Violations(layout, blockSize);
    if (bad.Count == 0) return;
    throw new InvalidOperationException(
      $"{what} would leave {bad.Count} owner(s) reading backwards — {bad[0]}. Nothing was changed.");
  }
}
