#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// A volume with no room left still has to be able to change its layout.
/// </summary>
/// <remarks>
/// Rearranging in place needs somewhere to put a run whose destination is still
/// occupied. Usually that is a free region of the volume; a full one has none,
/// and the pass used to end there and write the whole volume out again instead.
/// Memory is somewhere: a run is lifted out, the moves it was blocking run into
/// the space it leaves, and it is put down when its own destination is clear.
/// </remarks>
[TestFixture]
public class DefragFullVolumeTests {

  private const int BlockSize = 512;

  /// <summary>Six equal files filling a volume exactly, with nothing spare.</summary>
  private static List<DefragBlockInfo> FullVolume(out long imageSize) {
    var extents = new List<DefragBlockInfo>();
    var offset = (long)BlockSize;                 // one block of structure at the front
    extents.Add(new DefragBlockInfo(0, BlockSize, DefragBlockKind.MetadataReserved, "superblock"));

    for (var i = 0; i < 6; ++i) {
      extents.Add(new DefragBlockInfo(offset, 4L * BlockSize, DefragBlockKind.Used, $"F{i}"));
      offset += 4L * BlockSize;
    }

    imageSize = offset;
    return extents;
  }

  [Test, Category("EdgeCase")]
  public void PackingAFullVolumeAgainstTheTail_IsPlanned_NotRefused() {
    var extents = FullVolume(out var imageSize);

    var moves = DefragPlanner.Plan(extents, BlockSize, imageSize, BlockSize,
      LayoutProfile.Performance, DefragMode.ConsolidateAtEnd);

    Assert.That(moves, Is.Not.Empty, "A full volume was left exactly as it was.");
    Assert.That(moves.Any(m => m.Staging == DefragStaging.Park), Is.True,
      "Nothing was lifted out, so the cycle cannot have been broken.");

    // Every run that is lifted out has to be put back down, in the same slot.
    foreach (var park in moves.Where(m => m.Staging == DefragStaging.Park))
      Assert.That(moves.Any(m => m.Staging == DefragStaging.Unpark && m.StagingSlot == park.StagingSlot),
        Is.True, $"'{park.FileName}' was lifted out and never put down.");
  }

  [Test, Category("EdgeCase")]
  public void ACallerThatCannotHoldRuns_GetsARefusalRatherThanAPlanItCannotRun() {
    var extents = FullVolume(out var imageSize);

    // A caller running its own move loop that knows nothing about held runs
    // must not be handed a plan containing them.
    Assert.Throws<InvalidOperationException>(() =>
      DefragPlanner.Plan(extents, BlockSize, imageSize, BlockSize,
        LayoutProfile.Performance, DefragMode.ConsolidateAtEnd, allowMemoryStaging: false));
  }

  [Test, Category("HappyPath")]
  public void AHeldRun_ComesBackByteForByte_WhicheverSideOfTheBudgetItFallsOn() {
    var payload = new byte[64 * 1024];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)(i * 31 + 7);

    // A budget of half the run forces the spill to scratch; a generous one
    // keeps it in memory. Both have to give the same bytes back.
    foreach (var budget in new[] { payload.Length * 4L, payload.Length / 2L }) {
      using var image = new MemoryStream(new byte[payload.Length * 2]);
      image.Position = 0;
      image.Write(payload);

      using var staging = new DefragStagingBuffer(budget);
      staging.Park(image, slot: 0, offset: 0, length: payload.Length);
      Assert.That(staging.Spilled, Is.EqualTo(budget < payload.Length),
        $"A {budget:N0}-byte budget held a {payload.Length:N0}-byte run the wrong way.");

      // Whatever was there is gone as far as the volume is concerned.
      image.Position = 0;
      image.Write(new byte[payload.Length]);

      staging.Unpark(image, slot: 0, offset: payload.Length);

      var readBack = new byte[payload.Length];
      image.Position = payload.Length;
      image.ReadExactly(readBack);
      Assert.That(readBack, Is.EqualTo(payload),
        $"A run held under a {budget:N0}-byte budget did not come back byte for byte.");
    }
  }
}
