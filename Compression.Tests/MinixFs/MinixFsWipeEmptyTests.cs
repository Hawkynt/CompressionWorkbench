#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.MinixFs;

namespace Compression.Tests.MinixFs;

/// <summary>
/// Cluster-tip / unused-space wiping for the Minix FS descriptor. Minix stores
/// file data in 1024-byte zones reached through the inode's direct zone
/// pointers; the writer allocates them contiguously per file. The slack between
/// a file's logical size (i_size) and the end of its last zone is the zone tip
/// and must be zeroed. The inode records the true logical size, so the generic
/// wiper can locate and zero each tip without disturbing the inode table,
/// bitmaps or directory zones.
/// </summary>
[TestFixture]
public class MinixFsWipeEmptyTests {

  private const int BlockSize = 1024;

  private static byte[] BuildImageWith(string name, byte[] data) {
    using var ms = new MemoryStream();
    using (var w = new MinixFsWriter(ms, leaveOpen: true)) {
      w.AddFile(name, data);
      w.Finish();
    }
    return ms.ToArray();
  }

  [Test]
  public void MinixFsDescriptorImplementsIWipeEmpty() {
    var desc = new MinixFsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_ZoneTip_ZeroedAndFileRoundTrips() {
    var fileSize = BlockSize - 300; // 724 bytes in a 1024-byte zone — leaves a tip.
    var content = new byte[fileSize];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith("secret.bin", content);

    var desc = new MinixFsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    ms.Position = 0;
    var extent = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "secret.bin");
    var tipStart = extent.Offset + fileSize;
    var zoneEnd = extent.Offset + extent.Length;
    var tipLen = (int)(zoneEnd - tipStart);
    Assert.That(tipLen, Is.GreaterThan(0), "the chosen file size must leave a non-empty zone tip");

    // Dirty the tip so we can prove the wipe zeroed it.
    ms.Position = tipStart;
    var junk = new byte[tipLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk, 0, junk.Length);

    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    ms.Position = tipStart;
    var tip = new byte[tipLen];
    ms.Read(tip, 0, tip.Length);
    Assert.That(tip, Is.All.EqualTo((byte)0), "zone tip slack must be zeroed");

    ms.Position = 0;
    var reader = new MinixFsReader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory && e.Name == "secret.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(content), "file content survives the wipe intact");
  }
}
