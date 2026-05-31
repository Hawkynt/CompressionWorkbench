#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Hpfs;

namespace Compression.Tests.Hpfs;

/// <summary>
/// Cluster-tip / unused-space wiping for the OS/2 HPFS descriptor. HPFS uses
/// 512-byte sectors; the slack between a file's logical size and the end of its
/// last allocated sector is the sector tip and must be zeroed. The extent map
/// clamps each file's data run to its logical length, so the tip is a free gap
/// the generic wiper zero-fills.
/// </summary>
[TestFixture]
public class HpfsWipeEmptyTests {

  private const int SectorSize = 512;

  private static byte[] BuildImageWith(string name, byte[] data) {
    var w = new HpfsWriter();
    w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void HpfsDescriptorImplementsIWipeEmpty() {
    var desc = new HpfsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_ClusterTip_ZeroedAndFileRoundTrips() {
    var fileSize = SectorSize - 150; // 362 bytes in a 512-byte sector — leaves a tip.
    var content = new byte[fileSize];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith("SECRET.BIN", content);

    var desc = new HpfsFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    ms.Position = 0;
    var extent = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "SECRET.BIN");
    var tipStart = extent.Offset + fileSize;
    var sectorEnd = extent.Offset + ((fileSize + SectorSize - 1) / SectorSize) * SectorSize;
    var tipLen = (int)(sectorEnd - tipStart);
    Assert.That(tipLen, Is.GreaterThan(0), "the chosen file size must leave a non-empty sector tip");

    ms.Position = tipStart;
    var junk = new byte[tipLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk, 0, junk.Length);

    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    ms.Position = tipStart;
    var tip = new byte[tipLen];
    ms.Read(tip, 0, tip.Length);
    Assert.That(tip, Is.All.EqualTo((byte)0), "sector tip slack must be zeroed");

    ms.Position = 0;
    using var reader = new HpfsReader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory && e.Name == "SECRET.BIN");
    Assert.That(reader.Extract(entry), Is.EqualTo(content), "file content survives the wipe intact");
  }
}
