#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// Cluster-tip / unused-space wiping for the ISO 9660 descriptor. ECMA-119
/// stores every file contiguously and pads its final 2048-byte sector with
/// zeros; the bytes between a file's logical length and the sector boundary are
/// the sector tip and must be zeroed. The extent map clamps each Used run to
/// the file's logical length, so the tip is a free gap the generic wiper
/// zero-fills.
/// </summary>
[TestFixture]
public class IsoWipeEmptyTests {

  private const int SectorSize = 2048;

  private static byte[] BuildImageWith(string name, byte[] data) {
    var w = new IsoWriter();
    w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void IsoDescriptorImplementsIWipeEmpty() {
    var desc = new IsoFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_SectorTip_ZeroedAndFileRoundTrips() {
    var fileSize = SectorSize - 600; // 1448 bytes in a 2048-byte sector — leaves a tip.
    var content = new byte[fileSize];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith("SECRET.BIN", content);

    var desc = new IsoFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    // The only Used file extent is our file.
    ms.Position = 0;
    var extent = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.Length == fileSize);
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
    using var reader = new IsoReader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory);
    Assert.That(reader.Extract(entry), Is.EqualTo(content), "file content survives the wipe intact");
  }
}
