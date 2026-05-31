#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Os9Rbf;

namespace Compression.Tests.Os9Rbf;

/// <summary>
/// Sector-tip / unused-space wiping for the OS-9 RBF descriptor. RBF stores
/// file data in 256-byte sectors; the slack between a file's logical size
/// (FD.SIZ) and the end of its last allocated sector is the sector tip and must
/// be zeroed. Tip wiping is applied to single-segment files (a contiguous run);
/// the chosen small file occupies one segment.
/// </summary>
[TestFixture]
public class Os9RbfWipeEmptyTests {

  private const int SectorSize = 256;

  [Test]
  public void Os9RbfDescriptorImplementsIWipeEmpty() {
    var desc = new Os9RbfFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_SectorTip_ZeroedAndFileRoundTrips() {
    var fileSize = SectorSize - 56; // 200 bytes in a 256-byte sector — leaves a 56-byte tip.
    var content = new byte[fileSize];
    Array.Fill(content, (byte)0xAA);
    var image = Os9RbfWriter.Build([("SECRET", content)], "TESTVOL");

    var desc = new Os9RbfFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    // Locate the file's single data segment and dirty its sector tip.
    ms.Position = 0;
    var extent = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "SECRET");
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
    ms.ReadExactly(tip);
    Assert.That(tip, Is.All.EqualTo((byte)0), "sector tip slack must be zeroed");

    ms.Position = 0;
    using var roundTrip = new MemoryStream();
    ms.CopyTo(roundTrip);
    var v = Os9RbfReader.Read(roundTrip.ToArray());
    var entry = v.Files.First(f => !f.IsDirectory && f.Name == "SECRET");
    Assert.That(Os9RbfReader.Extract(v, entry), Is.EqualTo(content), "file content survives the wipe intact");
  }
}
