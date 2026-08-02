#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// A plan is only worth running if it can be run without losing anything.
/// </summary>
/// <remarks>
/// <para>Both cases here come from a full volume with metadata between the
/// files — a YAFFS2 image, where an object header sits ahead of every file's
/// chunks. Packing that against the tail is where the planner ran out of room
/// part way through, and what it did next was the problem: it spun looking for
/// a slot that could not exist, and once that was fixed it emitted the moves it
/// had already worked out and left the rest of the files where they were,
/// which put one file's destination on top of another file's bytes.</para>
/// </remarks>
[TestFixture]
public class DefragPlannerSafetyTests {

  private const int ChunkSize = 2112;

  /// <summary>
  /// The layout that provoked both faults: five files, each preceded by a
  /// header the planner may not touch, filling the volume exactly.
  /// </summary>
  private static List<DefragBlockInfo> FullVolume() {
    var extents = new List<DefragBlockInfo>();
    var offset = 0L;

    extents.Add(new DefragBlockInfo(offset, ChunkSize, DefragBlockKind.MetadataReserved, "header:"));
    offset += ChunkSize;

    foreach (var chunks in new[] { 5, 6, 6, 7, 7 }) {
      extents.Add(new DefragBlockInfo(offset, ChunkSize, DefragBlockKind.MetadataReserved,
        $"header:F{extents.Count}"));
      offset += ChunkSize;
      extents.Add(new DefragBlockInfo(offset, (long)chunks * ChunkSize, DefragBlockKind.Used,
        $"F{extents.Count}"));
      offset += (long)chunks * ChunkSize;
    }

    return extents;
  }

  [Test, Category("EdgeCase")]
  public void EndPacking_AFullVolume_Terminates() {
    var extents = FullVolume();
    var imageSize = extents[^1].Offset + extents[^1].Length;

    // Before the fix this never returned: the search for a slot below the
    // leading header kept landing back on the data origin, which overlaps that
    // header, and asked again.
    var planning = Task.Run(() => {
      try {
        return DefragPlanner.Plan(extents, 0, imageSize, ChunkSize,
          LayoutProfile.Performance, DefragMode.ConsolidateAtEnd).Count;
      } catch (InvalidOperationException) {
        return -1;   // refused is a fine answer; not returning at all is not
      }
    });

    Assert.That(planning.Wait(TimeSpan.FromSeconds(20)), Is.True,
      "Planning a full volume against the tail did not finish.");
  }

  [Test, Category("EdgeCase")]
  public void APlanThatWouldOverwriteAStationaryFile_IsRefused() {
    var extents = FullVolume();
    var imageSize = extents[^1].Offset + extents[^1].Length;

    // Whatever the packing pass decides, what it hands back must not write a
    // file where another file still lives: the one being overwritten has no
    // move of its own to collide with, so nothing else would notice.
    IReadOnlyList<ClusterMove> moves;
    try {
      moves = DefragPlanner.Plan(extents, 0, imageSize, ChunkSize,
        LayoutProfile.Performance, DefragMode.ConsolidateAtEnd);
    } catch (InvalidOperationException) {
      Assert.Pass("The planner refused the layout rather than emitting an unsafe plan.");
      return;
    }

    var vacated = moves.Select(m => (Start: m.SrcOffset, End: m.SrcOffset + m.Length)).ToList();
    foreach (var extent in extents.Where(e => e.Kind == DefragBlockKind.Used)) {
      var stillLive = vacated.Any(v => v.Start <= extent.Offset
                                    && v.End >= extent.Offset + extent.Length) == false;
      if (!stillLive) continue;

      foreach (var move in moves)
        Assert.That(move.DstOffset < extent.Offset + extent.Length
                 && extent.Offset < move.DstOffset + move.Length, Is.False,
          $"'{move.FileName}' is planned onto {move.DstOffset}..{move.DstOffset + move.Length}, " +
          $"which still holds '{extent.FileName}'.");
    }
  }
}
