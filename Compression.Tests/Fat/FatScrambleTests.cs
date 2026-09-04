#pragma warning disable CS1591
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// The scramble verb, tested as the round trip it exists for: a volume is built,
/// scattered, read back, and defragmented again.
/// </summary>
/// <remarks>
/// Every planned-defrag fixture before this one started from a volume that was
/// already tidy, because nothing in the public surface could produce a
/// fragmented one — the writers lay a volume out from scratch and removing
/// files leaves gaps of a kilobyte or two. So the defragmenter was only ever
/// asked to tidy what was already in order. Scattering first is what makes the
/// defragmentation a real test of itself.
/// </remarks>
[TestFixture]
public class FatScrambleTests {

  private const int Seed = 20250904;

  private static byte[] Filler(int length, int seed) {
    var buffer = new byte[length];
    for (var i = 0; i < length; ++i) buffer[i] = (byte)(0x20 + ((i * 7 + seed) % 0x5F));
    return buffer;
  }

  /// <summary>A volume with multi-cluster files at two depths, laid out tight.</summary>
  private static MemoryStream BuildVolume() {
    var writer = new FatWriter();
    writer.AddFile("readme.txt", Encoding.ASCII.GetBytes("volume notes, one cluster's worth at most"));
    writer.AddFile("catalog.db", Filler(24_000, 0x22));
    writer.AddFile("docs/q1.csv", Filler(9_500, 0x33));
    writer.AddFile("docs/q2.csv", Filler(11_000, 0x44));
    writer.AddFile("capture/frame001.raw", Filler(30_000, 0x55));
    writer.AddFile("capture/frame002.raw", Filler(28_000, 0x66));
    var image = writer.BuildAutoSized();

    var stream = new MemoryStream();
    stream.Write(image);
    stream.SetLength(image.Length);
    return stream;
  }

  private static Dictionary<string, byte[]> ContentsOf(MemoryStream image) {
    image.Position = 0;
    var reader = new FatReader(image);
    return reader.Entries.Where(e => !e.IsDirectory)
      .ToDictionary(e => e.Name.Replace('\\', '/'), reader.Extract);
  }

  /// <summary>How many runs each owner's allocation breaks into, per the extent map.</summary>
  private static Dictionary<string, int> ExtentsPerOwner(MemoryStream image) {
    image.Position = 0;
    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var extent in ((IFilesystemExtentMap)new FatFormatDescriptor()).EnumerateExtents(image)) {
      if (extent.Kind != DefragBlockKind.Used) continue;
      var owner = extent.FileName ?? "<unknown>";
      counts.TryGetValue(owner, out var count);
      counts[owner] = count + 1;
    }
    return counts;
  }

  private static void Report(string label, Dictionary<string, int> counts) {
    var total = counts.Values.Sum();
    TestContext.Out.WriteLine($"{label}: {total} extent(s) over {counts.Count} owner(s)");
    foreach (var (owner, count) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
      TestContext.Out.WriteLine($"    {owner,-28} {count}");
  }

  private static void AssertSameContents(Dictionary<string, byte[]> expected,
      Dictionary<string, byte[]> actual, string what) {
    Assert.That(actual, Has.Count.EqualTo(expected.Count), $"file count changed {what}");
    foreach (var (path, data) in expected) {
      Assert.That(actual, Contains.Key(path), $"'{path}' went missing {what}");
      Assert.That(actual[path], Is.EqualTo(data), $"'{path}' does not read back byte-identical {what}");
    }
  }

  // ── The round trip ───────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void AScrambledVolume_ReadsBackIntact_IsFragmented_AndDefragmentsCleanAgain() {
    using var image = BuildVolume();
    var original = ContentsOf(image);
    var before = ExtentsPerOwner(image);
    Report("before scramble", before);

    new FatFormatDescriptor().Scramble(image, new ScrambleOptions { Seed = Seed });

    var scrambled = ExtentsPerOwner(image);
    Report("after scramble", scrambled);
    AssertSameContents(original, ContentsOf(image), "after the scramble");

    Assert.That(scrambled.Values.Sum(), Is.GreaterThan(before.Values.Sum() * 4),
      "The scramble barely moved anything — the volume is not meaningfully fragmented.");
    // A one-cluster owner cannot be fragmented; every other one must be.
    foreach (var (owner, count) in scrambled)
      if (count > 1)
        Assert.That(count, Is.GreaterThan(before[owner]),
          $"'{owner}' came out of the scramble no more fragmented than it went in.");

    new FatFormatDescriptor().Defragment(image, new DefragOptions {
      Mode = DefragMode.ConsolidateAtStart,
      Profile = LayoutProfile.Performance,
    });

    var defragmented = ExtentsPerOwner(image);
    Report("after defragment", defragmented);
    AssertSameContents(original, ContentsOf(image), "after the defragmentation");

    foreach (var (owner, count) in defragmented)
      Assert.That(count, Is.EqualTo(1),
        $"'{owner}' is still in {count} pieces after a defragmentation.");
  }

  [Test, Category("HappyPath")]
  public void TheSameSeed_DealsTheSameLayout_AndADifferentOneDoesNot() {
    // One build, three copies. A fresh build would differ at the volume serial
    // the writer stamps from the clock, which has nothing to do with the seed.
    using var source = BuildVolume();
    var bytes = source.ToArray();
    using var first = new MemoryStream(bytes.ToArray(), writable: true);
    using var second = new MemoryStream(bytes.ToArray(), writable: true);
    using var other = new MemoryStream(bytes.ToArray(), writable: true);

    new FatFormatDescriptor().Scramble(first, new ScrambleOptions { Seed = Seed });
    new FatFormatDescriptor().Scramble(second, new ScrambleOptions { Seed = Seed });
    new FatFormatDescriptor().Scramble(other, new ScrambleOptions { Seed = Seed + 1 });

    Assert.That(second.ToArray(), Is.EqualTo(first.ToArray()),
      "The same seed dealt two different layouts, so no fixture or capture can rely on it.");
    Assert.That(other.ToArray(), Is.Not.EqualTo(first.ToArray()),
      "Two different seeds dealt the same layout, so the seed is not reaching the shuffle.");
  }

  [Test, Category("HappyPath")]
  public void ScramblingTwice_StillReadsBack() {
    using var image = BuildVolume();
    var original = ContentsOf(image);

    new FatFormatDescriptor().Scramble(image, new ScrambleOptions { Seed = Seed });
    new FatFormatDescriptor().Scramble(image, new ScrambleOptions { Seed = Seed + 7 });

    AssertSameContents(original, ContentsOf(image), "after scrambling an already-scattered volume");
  }

  // ── Refusals ─────────────────────────────────────────────────────────────

  /// <summary>A mover with nothing but the interface's defaults.</summary>
  private sealed class RunAtATimeMover : IFilesystemBlockMover {
    public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) { }
    public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) { }
  }

  [Test, Category("EdgeCase")]
  public void AMoverThatCannotRelinkAScatteredOwner_IsRefusedByName() {
    var refusal = Assert.Throws<InvalidOperationException>(
      () => FilesystemScrambler.RequireScatteredRelink(new RunAtATimeMover()));
    Assert.That(refusal!.Message, Does.Contain(nameof(RunAtATimeMover)),
      "The refusal has to name the mover that could not do it.");
    Assert.That(refusal.Message, Does.Not.Contain("rebuild"),
      "A scramble must never offer a rebuild: a rebuild lays the volume out contiguously.");
  }

  [Test, Category("EdgeCase")]
  public void AVolumeWithNoSpareBlock_IsPlannedByHoldingOne_AndRefusedWhenItCannotBe() {
    // Six four-block owners filling the data area exactly: every destination is
    // somebody's source, so the shuffle's cycles have nowhere on the volume to
    // unwind through.
    const int BlockSize = 512;
    var extents = new List<DefragBlockInfo> {
      new(0, BlockSize, DefragBlockKind.MetadataReserved, "superblock"),
    };
    var offset = (long)BlockSize;
    for (var i = 0; i < 6; ++i) {
      extents.Add(new DefragBlockInfo(offset, 4L * BlockSize, DefragBlockKind.Used, $"F{i}"));
      offset += 4L * BlockSize;
    }

    var moves = ScramblePlanner.Plan(extents, BlockSize, offset, BlockSize, Seed);
    Assert.That(moves.Any(m => m.Staging == DefragStaging.Park), Is.True,
      "A volume with no spare block was scrambled without holding anything out of it.");
    foreach (var park in moves.Where(m => m.Staging == DefragStaging.Park))
      Assert.That(moves.Any(m => m.Staging == DefragStaging.Unpark && m.StagingSlot == park.StagingSlot),
        Is.True, $"'{park.FileName}' was lifted out and never put down.");

    Assert.Throws<InvalidOperationException>(
      () => ScramblePlanner.Plan(extents, BlockSize, offset, BlockSize, Seed, allowMemoryStaging: false),
      "A mover that cannot hold a run was handed a plan that needs one.");
  }

  [Test, Category("EdgeCase")]
  public void AnEmptyVolume_HasNothingToScatter() {
    var extents = new List<DefragBlockInfo> {
      new(0, 512, DefragBlockKind.MetadataReserved, "superblock"),
      new(512, 4096, DefragBlockKind.Free, null),
    };
    Assert.That(ScramblePlanner.Plan(extents, 512, 4608, 512, Seed), Is.Empty);
  }

  /// <summary>
  /// Replays a plan the way the FAT descriptor does and checks the two things
  /// that make it safe: nothing is read out of a slot whose contents have
  /// already been overwritten, and every block ends up somewhere of its own.
  /// </summary>
  private static void AssertPlanIsSafe(IReadOnlyList<ClusterMove> moves,
      IReadOnlyList<DefragBlockInfo> extents, int blockSize, string what) {
    var origins = new Dictionary<long, long>();
    foreach (var extent in extents) {
      if (extent.Kind != DefragBlockKind.Used) continue;
      var count = (extent.Length + blockSize - 1) / blockSize;
      for (var block = 0L; block < count; ++block) {
        var at = extent.Offset + block * blockSize;
        origins[at] = at;
      }
    }
    var finalOf = origins.ToDictionary(kv => kv.Key, kv => kv.Value);
    var occupant = origins.ToDictionary(kv => kv.Key, kv => kv.Value);
    var held = new Dictionary<int, long>();

    foreach (var move in moves)
      switch (move.Staging) {
        case DefragStaging.Park:
          Assert.That(occupant.Remove(move.SrcOffset, out var parked), Is.True,
            $"{what}: a block was lifted out of a slot that held nothing.");
          held[move.StagingSlot] = parked;
          break;
        case DefragStaging.Unpark:
          Assert.That(held.Remove(move.StagingSlot, out var putDown), Is.True,
            $"{what}: a block was put down that had never been lifted.");
          occupant[move.DstOffset] = putDown;
          finalOf[putDown] = move.DstOffset;
          break;
        default:
          Assert.That(occupant.TryGetValue(move.SrcOffset, out var moved), Is.True,
            $"{what}: a move reads {move.SrcOffset}, which nothing live occupies any more.");
          occupant.Remove(move.SrcOffset);
          occupant[move.DstOffset] = moved;
          finalOf[moved] = move.DstOffset;
          break;
      }

    Assert.That(held, Is.Empty, $"{what}: a block was left held outside the volume.");
    Assert.That(finalOf.Values.Distinct().Count(), Is.EqualTo(finalOf.Count),
      $"{what}: two blocks ended up in the same slot.");
  }

  [Test, Category("EdgeCase")]
  public void OverManySeeds_NoPlanEverWritesOverABlockThatHasNotMovedYet() {
    const int BlockSize = 512;

    // Free space from none at all — where every cycle has to be unwound by
    // holding a block outside the volume — up to plenty, where it is unwound
    // through a spare slot instead.
    foreach (var spare in new[] { 0, 1, 3, 40 })
      for (var seed = 1; seed <= 40; ++seed) {
        var extents = new List<DefragBlockInfo> {
          new(0, BlockSize, DefragBlockKind.MetadataReserved, "superblock"),
        };
        var at = (long)BlockSize;
        for (var owner = 0; owner < 7; ++owner) {
          var blocks = 1 + owner;
          extents.Add(new DefragBlockInfo(at, blocks * (long)BlockSize, DefragBlockKind.Used, $"F{owner}"));
          at += blocks * (long)BlockSize;
        }

        var moves = ScramblePlanner.Plan(extents, BlockSize, at + spare * (long)BlockSize, BlockSize, seed);
        AssertPlanIsSafe(moves, extents, BlockSize, $"spare={spare} seed={seed}");
      }
  }
}
