#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Mfs;

namespace Compression.Tests.Mfs;

/// <summary>
/// Cluster-tip / unused-space wiping for the Macintosh MFS descriptor. MFS
/// stores each file's data contiguously in 1024-byte allocation blocks, so the
/// slack between a file's logical size and the end of its last block is the
/// block tip and must be zeroed. The directory entry records the true logical
/// size, so the generic wiper can locate and zero every tip.
/// </summary>
[TestFixture]
public class MfsWipeEmptyTests {

  private const int BlockSize = 1024;

  private static byte[] BuildImageWith(string name, byte[] data) {
    var w = new MfsWriter();
    w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void MfsDescriptorImplementsIWipeEmpty() {
    var desc = new MfsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_ClusterTip_ZeroedAndFileRoundTrips() {
    var fileSize = BlockSize - 200; // 824 bytes in a 1024-byte block — leaves a tip.
    var content = new byte[fileSize];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith("SECRET", content);

    var desc = new MfsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    ms.Position = 0;
    var extent = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "SECRET");
    var tipStart = extent.Offset + fileSize;
    var blockEnd = extent.Offset + extent.Length;
    var tipLen = (int)(blockEnd - tipStart);
    Assert.That(tipLen, Is.GreaterThan(0), "the chosen file size must leave a non-empty block tip");

    // Dirty the tip so we can prove the wipe zeroed it.
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
    var reader = new MfsReader(ms);
    var entry = reader.Entries.First(e => e.Name == "SECRET");
    Assert.That(reader.Extract(entry), Is.EqualTo(content), "file content survives the wipe intact");
  }
}
