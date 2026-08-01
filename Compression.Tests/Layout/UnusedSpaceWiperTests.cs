#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Layout;

[TestFixture]
public class UnusedSpaceWiperTests {

  [Test]
  public void Wipe_ZerosGapsBetweenExtents() {
    // Image layout: [Used 0..100] [gap 100..200] [Used 200..300] [gap 300..400]
    var imageSize = 400L;
    var data = new byte[imageSize];
    // Fill entire image with 0xFF to simulate dirty state.
    Array.Fill(data, (byte)0xFF);

    using var ms = new MemoryStream(data);
    var extents = new List<DefragBlockInfo> {
      new(0, 100, DefragBlockKind.Used, "file1"),
      new(200, 100, DefragBlockKind.Used, "file2"),
    };

    var wiped = UnusedSpaceWiper.Wipe(ms, extents, imageSize, wipeClusterTips: false);

    // Gaps [100..200] and [300..400] should be zeroed = 200 bytes.
    Assert.That(wiped, Is.EqualTo(200));

    // Verify gap bytes are zero.
    var result = ms.ToArray();
    for (var i = 100; i < 200; i++)
      Assert.That(result[i], Is.EqualTo(0), $"Gap byte at {i} should be zero");
    for (var i = 300; i < 400; i++)
      Assert.That(result[i], Is.EqualTo(0), $"Gap byte at {i} should be zero");

    // Verify live data is untouched.
    for (var i = 0; i < 100; i++)
      Assert.That(result[i], Is.EqualTo(0xFF), $"Live byte at {i} should be untouched");
    for (var i = 200; i < 300; i++)
      Assert.That(result[i], Is.EqualTo(0xFF), $"Live byte at {i} should be untouched");
  }

  [Test]
  public void Wipe_HandlesUnsortedExtents() {
    var imageSize = 300L;
    var data = new byte[imageSize];
    Array.Fill(data, (byte)0xAA);

    using var ms = new MemoryStream(data);
    // Extents out of order — wiper must sort them.
    var extents = new List<DefragBlockInfo> {
      new(200, 50, DefragBlockKind.Used, "b"),
      new(0, 50, DefragBlockKind.MetadataReserved, "boot"),
      new(100, 50, DefragBlockKind.Used, "a"),
    };

    var wiped = UnusedSpaceWiper.Wipe(ms, extents, imageSize, wipeClusterTips: false);

    // Gaps: [50..100]=50, [150..200]=50, [250..300]=50 = 150 bytes.
    Assert.That(wiped, Is.EqualTo(150));

    var result = ms.ToArray();
    // Live regions untouched.
    Assert.That(result[0], Is.EqualTo(0xAA));
    Assert.That(result[100], Is.EqualTo(0xAA));
    Assert.That(result[200], Is.EqualTo(0xAA));
    // Gap regions zeroed.
    Assert.That(result[50], Is.EqualTo(0));
    Assert.That(result[150], Is.EqualTo(0));
    Assert.That(result[250], Is.EqualTo(0));
  }

  [Test]
  public void Wipe_ClusterTips_ZerosSlack() {
    var imageSize = 200L;
    var data = new byte[imageSize];
    Array.Fill(data, (byte)0xBB);

    using var ms = new MemoryStream(data);
    // Extent is 100 bytes (one cluster) but actual file is 60 bytes.
    var extents = new List<DefragBlockInfo> {
      new(0, 100, DefragBlockKind.Used, "small.txt"),
    };

    var fileSizeLookup = (string name) => name == "small.txt" ? 60L : -1L;
    var wiped = UnusedSpaceWiper.Wipe(ms, extents, imageSize, wipeClusterTips: true, fileSizeLookup);

    // Cluster tip [60..100] = 40 bytes zeroed, plus trailing gap [100..200] = 100 bytes.
    Assert.That(wiped, Is.EqualTo(140));

    var result = ms.ToArray();
    // File data untouched.
    for (var i = 0; i < 60; i++)
      Assert.That(result[i], Is.EqualTo(0xBB), $"File byte at {i} should be untouched");
    // Cluster tip zeroed.
    for (var i = 60; i < 100; i++)
      Assert.That(result[i], Is.EqualTo(0), $"Cluster tip byte at {i} should be zero");
  }

  [Test]
  public void Wipe_NoClusterTips_LeavesSlackAlone() {
    var imageSize = 100L;
    var data = new byte[imageSize];
    Array.Fill(data, (byte)0xCC);

    using var ms = new MemoryStream(data);
    var extents = new List<DefragBlockInfo> {
      new(0, 100, DefragBlockKind.Used, "file.txt"),
    };

    // Wipe without cluster tips — entire image is one used extent, no gaps.
    var wiped = UnusedSpaceWiper.Wipe(ms, extents, imageSize, wipeClusterTips: false);
    Assert.That(wiped, Is.EqualTo(0));

    // All bytes untouched.
    var result = ms.ToArray();
    Assert.That(result, Is.All.EqualTo((byte)0xCC));
  }

  [Test]
  public void Wipe_AlreadyClean_ReturnsZero() {
    var imageSize = 200L;
    var data = new byte[imageSize]; // all zeros

    // Put some non-zero in the live region only.
    Array.Fill(data, (byte)0x42, 0, 100);

    using var ms = new MemoryStream(data);
    var extents = new List<DefragBlockInfo> {
      new(0, 100, DefragBlockKind.Used, "file"),
    };

    // Gap [100..200] is already all-zero.
    var wiped = UnusedSpaceWiper.Wipe(ms, extents, imageSize, wipeClusterTips: false);
    Assert.That(wiped, Is.EqualTo(0), "Already-clean gap should not count as wiped");
  }

  /// <summary>
  /// An empty volume still describes itself — its map reports the superblock,
  /// the allocation tables and the free space between them — so wiping it
  /// zeros everything the map calls free.
  /// </summary>
  [Test]
  public void Wipe_EmptyImage_ZerosEverything() {
    var imageSize = 256L;
    var data = new byte[imageSize];
    Array.Fill(data, (byte)0xDD);

    using var ms = new MemoryStream(data);
    var extents = new List<DefragBlockInfo> {
      new(0, 16, DefragBlockKind.MetadataReserved, "Superblock"),
    };
    var wiped = UnusedSpaceWiper.Wipe(ms, extents, imageSize, wipeClusterTips: false);
    Assert.That(wiped, Is.EqualTo(240));

    var result = ms.ToArray();
    Assert.That(result[16..], Is.All.EqualTo((byte)0));
  }

  /// <summary>
  /// A map that claims nothing has not read the image — every filesystem it
  /// understands accounts for at least its own superblock. Treating that as
  /// "all free" wiped live files off volumes whose reader simply did not
  /// recognise the layout, so it now wipes nothing at all.
  /// </summary>
  [Test]
  public void Wipe_NoExtentsAtAll_LeavesTheImageAlone() {
    var imageSize = 256L;
    var data = new byte[imageSize];
    Array.Fill(data, (byte)0xDD);

    using var ms = new MemoryStream(data);
    var wiped = UnusedSpaceWiper.Wipe(ms, [], imageSize, wipeClusterTips: false);

    Assert.That(wiped, Is.EqualTo(0));
    Assert.That(ms.ToArray(), Is.All.EqualTo((byte)0xDD));
  }

  [Test]
  public void Wipe_FreeExtentsAreIgnored() {
    // Free extents in the input should not prevent wiping.
    var imageSize = 200L;
    var data = new byte[imageSize];
    Array.Fill(data, (byte)0xEE);

    using var ms = new MemoryStream(data);
    var extents = new List<DefragBlockInfo> {
      new(0, 50, DefragBlockKind.Used, "file"),
      new(50, 50, DefragBlockKind.Free),          // this is Free — should be wiped
      new(100, 50, DefragBlockKind.MetadataReserved, "meta"),
      new(150, 50, DefragBlockKind.Free),          // this is Free — should be wiped
    };

    var wiped = UnusedSpaceWiper.Wipe(ms, extents, imageSize, wipeClusterTips: false);
    Assert.That(wiped, Is.EqualTo(100)); // [50..100] + [150..200]

    var result = ms.ToArray();
    Assert.That(result[50], Is.EqualTo(0));
    Assert.That(result[150], Is.EqualTo(0));
    Assert.That(result[0], Is.EqualTo(0xEE));
    Assert.That(result[100], Is.EqualTo(0xEE));
  }

  [Test]
  public void ComputeUnusedBytes_CountsGapsRegardlessOfContent() {
    // Same layout as Wipe_ZerosGapsBetweenExtents but verifies that the
    // separate count helper reports the FULL gap span, not "bytes that
    // would have to be written". This is the figure the UI shows so users
    // understand how much of the image is unused even when those bytes
    // already happen to be zero.
    var imageSize = 400L;
    var extents = new List<DefragBlockInfo> {
      new(0, 100, DefragBlockKind.Used, "file1"),
      new(200, 100, DefragBlockKind.Used, "file2"),
    };

    var unused = UnusedSpaceWiper.ComputeUnusedBytes(extents, imageSize);
    Assert.That(unused, Is.EqualTo(200)); // [100..200] + [300..400]
  }

  [Test]
  public void ComputeUnusedBytes_ReportsUnusedEvenWhenWipeWritesZero() {
    // The original bug: on a mostly-empty image where free clusters are
    // already zero, Wipe returns ~0 bytes written because its read-first
    // optimization skips already-zero chunks. ComputeUnusedBytes must
    // still report the full unused span so the UI doesn't claim
    // "0% unused" on a 95%-empty image.
    var imageSize = 1024L;
    var data = new byte[imageSize]; // all zeros — simulates fresh fat.img free space
    using var ms = new MemoryStream(data);
    var extents = new List<DefragBlockInfo> {
      new(0, 64, DefragBlockKind.MetadataReserved, "BootSector"),
      new(64, 32, DefragBlockKind.Used, "small.txt"),
    };

    var wiped = UnusedSpaceWiper.Wipe(ms, extents, imageSize, wipeClusterTips: false);
    var unused = UnusedSpaceWiper.ComputeUnusedBytes(extents, imageSize);

    Assert.That(wiped, Is.EqualTo(0), "Image is already zero — nothing to write");
    Assert.That(unused, Is.EqualTo(1024 - 96), "Span of unused bytes is still reported");
  }

  [Test]
  public void ComputeUnusedBytes_IncludesClusterTipsWhenRequested() {
    // A Used extent of length 100 holds a file of actual size 20.
    // includeClusterTips=true must count the 80-byte tail as unused.
    var imageSize = 200L;
    var extents = new List<DefragBlockInfo> {
      new(0, 100, DefragBlockKind.Used, "tiny.bin"),
    };
    long Lookup(string n) => n == "tiny.bin" ? 20 : -1;

    var withoutTips = UnusedSpaceWiper.ComputeUnusedBytes(extents, imageSize);
    var withTips = UnusedSpaceWiper.ComputeUnusedBytes(extents, imageSize,
      includeClusterTips: true, fileSizeLookup: Lookup);

    Assert.That(withoutTips, Is.EqualTo(100), "Trailing gap [100..200] only");
    Assert.That(withTips, Is.EqualTo(100 + 80), "Trailing gap + 80-byte cluster tip");
  }
}
