#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Hfs;

namespace Compression.Tests.Hfs;

/// <summary>
/// Cluster-tip / unused-space wiping for the classic HFS descriptor. HFS uses
/// 512-byte allocation blocks; the slack between a file's logical size and the
/// end of its last block is the block tip and must be zeroed. The catalog
/// extent map clamps each file's run to its logical length, so the tip is a
/// free gap the generic wiper zero-fills.
/// </summary>
[TestFixture]
public class HfsWipeEmptyTests {

  private const int BlockSize = 512;

  private static byte[] BuildImageWith(string name, byte[] data) {
    var w = new HfsWriter();
    w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void HfsDescriptorImplementsIWipeEmpty() {
    var desc = new HfsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_ClusterTip_ZeroedAndFileRoundTrips() {
    var fileSize = BlockSize - 150; // 362 bytes in a 512-byte block — leaves a tip.
    var content = new byte[fileSize];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith("SECRET", content);

    var desc = new HfsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    ms.Position = 0;
    var extent = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "SECRET");
    var tipStart = extent.Offset + fileSize;
    var blockEnd = extent.Offset + ((fileSize + BlockSize - 1) / BlockSize) * BlockSize;
    var tipLen = (int)(blockEnd - tipStart);
    Assert.That(tipLen, Is.GreaterThan(0), "the chosen file size must leave a non-empty block tip");

    ms.Position = tipStart;
    var junk = new byte[tipLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk, 0, junk.Length);

    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    ms.Position = tipStart;
    var tip = new byte[tipLen];
    ms.Read(tip, 0, tip.Length);
    Assert.That(tip, Is.All.EqualTo((byte)0), "block tip slack must be zeroed");

    ms.Position = 0;
    var reader = new HfsReader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory && e.Name == "SECRET");
    Assert.That(reader.Extract(entry), Is.EqualTo(content), "file content survives the wipe intact");
  }
}
