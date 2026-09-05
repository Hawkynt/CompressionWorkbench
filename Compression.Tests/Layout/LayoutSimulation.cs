#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// Runs a plan against a layout on paper, the way
/// <see cref="DefragPlannerExecutor" /> runs it against an image, and hands back
/// the layout that results.
/// </summary>
/// <remarks>
/// <para>It is an oracle as much as a convenience. Every block carries the
/// address it started at, so a move that writes over a block which has not
/// moved yet is caught and named instead of quietly costing a file its bytes —
/// which is the failure a plan can have that no count of moves would show.</para>
///
/// <para>Owner order comes from the layout, not from the order the moves happen
/// to arrive in, exactly as the executor's chain tracker takes it. That is what
/// makes the result something the ascending-order property can be read off.</para>
/// </remarks>
internal static class LayoutSimulation {

  /// <summary>Applies <paramref name="moves" /> and returns the resulting layout.</summary>
  public static List<DefragBlockInfo> Apply(IReadOnlyList<DefragBlockInfo> layout,
      IReadOnlyList<ClusterMove> moves, int clusterSize, long dataOrigin, long imageSize) {
    var occupant = new Dictionary<long, long>();
    var finalOf = new Dictionary<long, long>();
    var owners = new List<string>();
    var blocksOf = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
    var reserved = new List<DefragBlockInfo>();

    foreach (var extent in layout) {
      if (extent.Kind != DefragBlockKind.Used) {
        if (extent.Kind != DefragBlockKind.Free) reserved.Add(extent);
        continue;
      }
      var owner = extent.FileName ?? "<unknown>";
      if (!blocksOf.TryGetValue(owner, out var list)) {
        blocksOf[owner] = list = [];
        owners.Add(owner);
      }
      var count = (extent.Length + clusterSize - 1) / clusterSize;
      for (var block = 0L; block < count; ++block) {
        var at = extent.Offset + block * clusterSize;
        Assert.That(occupant.ContainsKey(at), Is.False,
          $"The layout has two owners on {at:N0}.");
        occupant[at] = at;
        finalOf[at] = at;
        list.Add(at);
      }
    }

    var held = new Dictionary<(int Slot, long At), long>();

    foreach (var move in moves) {
      var step = clusterSize;

      if (move.Staging == DefragStaging.Park) {
        for (var at = 0L; at < move.Length; at += step) {
          var from = move.SrcOffset + at;
          Assert.That(occupant.TryGetValue(from, out var origin), Is.True,
            $"'{move.FileName}' is lifted out of {from:N0}, where nothing lives.");
          occupant.Remove(from);
          held[(move.StagingSlot, at)] = origin;
        }
        continue;
      }

      // A move is a memmove: everything it reads is read before anything it
      // writes, so a run shifted by less than its own length is not a clobber.
      var carried = new List<(long To, long Origin)>();
      for (var at = 0L; at < move.Length; at += step) {
        var from = move.SrcOffset + at;
        var to = move.DstOffset + at;
        long origin;
        if (move.Staging == DefragStaging.Unpark) {
          Assert.That(held.Remove((move.StagingSlot, at), out origin), Is.True,
            $"'{move.FileName}' is put down from slot {move.StagingSlot}, which holds nothing.");
        } else {
          Assert.That(occupant.TryGetValue(from, out origin), Is.True,
            $"'{move.FileName}' is read from {from:N0}, where nothing lives.");
          occupant.Remove(from);
        }
        carried.Add((to, origin));
      }

      foreach (var (to, origin) in carried) {
        Assert.That(occupant.ContainsKey(to), Is.False,
          $"'{move.FileName}' is written to {to:N0}, which still holds a block that has not moved.");
        occupant[to] = origin;
        finalOf[origin] = to;
      }
    }

    Assert.That(held, Is.Empty, "A run was lifted out of the volume and never put back down.");

    var result = new List<DefragBlockInfo>();
    var live = new List<(long Start, long End)>();
    foreach (var owner in owners) {
      var addresses = blocksOf[owner].Select(o => finalOf[o]).ToList();
      for (var i = 0; i < addresses.Count;) {
        var run = 1;
        while (i + run < addresses.Count && addresses[i + run] == addresses[i] + (long)run * clusterSize) ++run;
        result.Add(new DefragBlockInfo(addresses[i], (long)run * clusterSize, DefragBlockKind.Used, owner));
        live.Add((addresses[i], addresses[i] + (long)run * clusterSize));
        i += run;
      }
    }

    foreach (var extent in reserved) {
      result.Add(extent);
      live.Add((extent.Offset, extent.Offset + extent.Length));
    }

    live.Sort((a, b) => a.Start.CompareTo(b.Start));
    var cursor = dataOrigin;
    foreach (var (start, end) in live) {
      if (start > cursor) result.Add(new DefragBlockInfo(cursor, start - cursor, DefragBlockKind.Free, null));
      cursor = Math.Max(cursor, end);
    }
    if (cursor < imageSize) result.Add(new DefragBlockInfo(cursor, imageSize - cursor, DefragBlockKind.Free, null));

    return result;
  }

  /// <summary>Total bytes the plan copies. Held runs are copied twice, and counted twice.</summary>
  public static long BytesMoved(IReadOnlyList<ClusterMove> moves) {
    var total = 0L;
    foreach (var move in moves) total += move.Length;
    return total;
  }

  /// <summary>How many blocks the plan asks to be held outside the volume.</summary>
  public static int Parks(IReadOnlyList<ClusterMove> moves)
    => moves.Count(m => m.Staging == DefragStaging.Park);

  /// <summary>Runs per owner, the measure of how fragmented a layout is.</summary>
  public static int TotalRuns(IReadOnlyList<DefragBlockInfo> layout) {
    var runs = 0;
    var endOf = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var extent in layout) {
      if (extent.Kind != DefragBlockKind.Used) continue;
      var owner = extent.FileName ?? "<unknown>";
      if (!endOf.TryGetValue(owner, out var previous) || previous != extent.Offset) ++runs;
      endOf[owner] = extent.Offset + extent.Length;
    }
    return runs;
  }

  /// <summary>
  /// A deliberately scattered volume: <paramref name="owners" /> owners of
  /// <paramref name="blocksPerOwner" /> blocks each, dealt slots at random from
  /// a data area sized so that <paramref name="freeFraction" /> of it is left
  /// over, with one reserved block at the front and one in the middle.
  /// </summary>
  public static List<DefragBlockInfo> Scattered(int seed, int owners, int blocksPerOwner,
      double freeFraction, int clusterSize, out long dataOrigin, out long imageSize) {
    var liveBlocks = owners * blocksPerOwner;
    var dataBlocks = (int)Math.Ceiling(liveBlocks / (1.0 - freeFraction));
    dataOrigin = clusterSize;                                     // one block of structure at the front
    var reservedAt = dataOrigin + (dataBlocks / 2L) * clusterSize; // and one reserved block inside
    imageSize = dataOrigin + (dataBlocks + 1L) * clusterSize;

    var slots = new List<long>();
    for (var at = dataOrigin; at + clusterSize <= imageSize; at += clusterSize)
      if (at != reservedAt) slots.Add(at);

    Shuffle(slots, seed);

    var layout = new List<DefragBlockInfo>();
    var taken = 0;
    for (var owner = 0; owner < owners; ++owner) {
      var name = $"F{owner:D4}.BIN";
      for (var block = 0; block < blocksPerOwner; ++block)
        layout.Add(new DefragBlockInfo(slots[taken++], clusterSize, DefragBlockKind.Used, name));
    }

    layout.Add(new DefragBlockInfo(0, clusterSize, DefragBlockKind.MetadataReserved, "superblock"));
    layout.Add(new DefragBlockInfo(reservedAt, clusterSize, DefragBlockKind.MetadataReserved, "table"));

    var used = new HashSet<long>(layout.Where(e => e.Kind == DefragBlockKind.Used).Select(e => e.Offset));
    foreach (var slot in slots)
      if (!used.Contains(slot)) layout.Add(new DefragBlockInfo(slot, clusterSize, DefragBlockKind.Free, null));

    return layout;
  }

  /// <summary>
  /// Fisher-Yates over SplitMix64, so a seed deals the same layout on every
  /// machine and the figures a survey prints are reproducible.
  /// </summary>
  private static void Shuffle(List<long> slots, int seed) {
    var state = (ulong)(uint)seed * 0x9E3779B97F4A7C15ul + 0x1234567890ABCDEFul;

    ulong Next() {
      state += 0x9E3779B97F4A7C15ul;
      var z = state;
      z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ul;
      z = (z ^ (z >> 27)) * 0x94D049BB133111EBul;
      return z ^ (z >> 31);
    }

    for (var i = slots.Count - 1; i > 0; --i) {
      var j = (int)(Next() % (ulong)(i + 1));
      (slots[i], slots[j]) = (slots[j], slots[i]);
    }
  }
}
