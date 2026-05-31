#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.ProDos;

namespace Compression.Tests.ProDos;

/// <summary>
/// Block-tip / unused-space wiping for the ProDOS descriptor. ProDOS uses
/// 512-byte blocks; a seedling file (≤ 512 bytes) occupies a single data block
/// whose trailing slack past the file's EOF is the block tip and must be
/// zeroed. Sapling/tree files interleave index blocks with data inside one
/// coalesced extent, so they are excluded from the tip pass to avoid corrupting
/// block pointers — this test exercises the safe seedling case.
/// </summary>
[TestFixture]
public class ProDosWipeEmptyTests {

  private const int BlockSize = 512;

  private static byte[] BuildImageWith(string name, byte[] data) {
    var w = new ProDosWriter();
    w.AddFile(name, data);
    return w.Build("WORM");
  }

  [Test]
  public void ProDosDescriptorImplementsIWipeEmpty() {
    var desc = new ProDosFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_SeedlingBlockTip_ZeroedAndFileRoundTrips() {
    var fileSize = BlockSize - 212; // 300 bytes in a 512-byte block — seedling, 212-byte tip.
    var content = new byte[fileSize];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith("SECRET", content);

    var desc = new ProDosFormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    // Locate the seedling's single data block and dirty its tip.
    ms.Position = 0;
    var extent = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "SECRET");
    Assert.That(extent.Length, Is.EqualTo(BlockSize), "a 300-byte file is a single-block seedling");
    var tipStart = extent.Offset + fileSize;
    var blockEnd = extent.Offset + BlockSize;
    var tipLen = (int)(blockEnd - tipStart);
    Assert.That(tipLen, Is.GreaterThan(0), "the chosen file size must leave a non-empty block tip");

    ms.Position = tipStart;
    var junk = new byte[tipLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk, 0, junk.Length);

    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    ms.Position = tipStart;
    var tip = new byte[tipLen];
    ms.ReadExactly(tip);
    Assert.That(tip, Is.All.EqualTo((byte)0), "block tip slack must be zeroed");

    ms.Position = 0;
    using var reader = new ProDosReader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory && e.FullPath == "SECRET");
    Assert.That(reader.Extract(entry), Is.EqualTo(content), "file content survives the wipe intact");
  }
}
