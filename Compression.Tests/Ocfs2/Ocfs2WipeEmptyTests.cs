#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ocfs2;

namespace Compression.Tests.Ocfs2;

/// <summary>
/// Cluster-tip / unused-space wiping for the OCFS2 descriptor. OCFS2 allocates
/// file data in 4096-byte clusters; the slack between a file's logical size and
/// the end of its last cluster is the cluster tip and must be zeroed. The
/// extent map clamps each Used data extent to the file's logical length, so the
/// tip surfaces as a free gap the generic wiper zero-fills without a size
/// lookup.
/// </summary>
[TestFixture]
public class Ocfs2WipeEmptyTests {

  private const int ClusterSize = 4096;

  private static byte[] BuildImageWith(string name, byte[] data) {
    var w = new Ocfs2Writer();
    w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void Ocfs2DescriptorImplementsIWipeEmpty() {
    var desc = new Ocfs2FormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_ClusterTip_ZeroedAndFileRoundTrips() {
    var fileSize = ClusterSize - 500; // 3596 bytes in a 4096-byte cluster — leaves a tip.
    var content = new byte[fileSize];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith("secret.bin", content);

    var desc = new Ocfs2FormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    // The Used data extent is clamped to the logical size; the tip is the gap
    // between that extent's end and the cluster boundary.
    ms.Position = 0;
    var extent = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "secret.bin");
    var dataStart = extent.Offset;
    var tipStart = dataStart + fileSize;
    var clusterEnd = dataStart + ((fileSize + ClusterSize - 1) / ClusterSize) * ClusterSize;
    var tipLen = (int)(clusterEnd - tipStart);
    Assert.That(tipLen, Is.GreaterThan(0), "the chosen file size must leave a non-empty cluster tip");

    ms.Position = tipStart;
    var junk = new byte[tipLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk, 0, junk.Length);

    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    ms.Position = tipStart;
    var tip = new byte[tipLen];
    ms.ReadExactly(tip);
    Assert.That(tip, Is.All.EqualTo((byte)0), "cluster tip slack must be zeroed");

    ms.Position = 0;
    var entries = desc.List(ms, null);
    Assert.That(entries.Any(e => e.Name == "secret.bin"), Is.True, "file entry survives the wipe");

    var dir = Path.Combine(Path.GetTempPath(), "ocfs2wipe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      ms.Position = 0;
      desc.Extract(ms, dir, null, null);
      var roundTripped = File.ReadAllBytes(Path.Combine(dir, "secret.bin"));
      Assert.That(roundTripped, Is.EqualTo(content), "file content survives the wipe intact");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
