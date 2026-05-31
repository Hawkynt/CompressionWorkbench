#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ext1;

namespace Compression.Tests.Ext1;

/// <summary>
/// Cluster-tip / unused-space wiping for the ext1 descriptor. ext1 stores file
/// data in 1024-byte blocks; the slack between a file's logical size and the
/// end of its last block is the block tip and must be zeroed. The extent map
/// clamps each file's run to its logical length, so the tip is a free gap the
/// generic wiper zero-fills.
/// </summary>
[TestFixture]
public class Ext1WipeEmptyTests {

  private const int BlockSize = 1024;

  private static byte[] BuildImageWith(string name, byte[] data) {
    var w = new Ext1Writer();
    w.AddFile(name, data);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test]
  public void Ext1DescriptorImplementsIWipeEmpty() {
    var desc = new Ext1FormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("RoundTrip")]
  public void WipeEmpty_ClusterTip_ZeroedAndFileRoundTrips() {
    // A file a bit less than one 1024-byte block — leaves a tip of slack.
    var fileSize = BlockSize - 200;
    var content = new byte[fileSize];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith("secret.bin", content);

    var desc = new Ext1FormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    // Locate the file's Used extent and pollute its block tip with 0xFF.
    ms.Position = 0;
    var extent = desc.EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "secret.bin");
    var tipStart = extent.Offset + fileSize;
    var blockEnd = extent.Offset + ((fileSize + BlockSize - 1) / BlockSize) * BlockSize;
    var tipLen = (int)(blockEnd - tipStart);
    Assert.That(tipLen, Is.GreaterThan(0), "the chosen file size must leave a non-empty block tip");

    ms.Position = tipStart;
    var junk = new byte[tipLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk, 0, junk.Length);

    // Wipe with cluster tips enabled.
    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    // The block tip must now be all zero.
    ms.Position = tipStart;
    var tip = new byte[tipLen];
    ms.Read(tip, 0, tip.Length);
    Assert.That(tip, Is.All.EqualTo((byte)0), "block tip slack must be zeroed");

    // The file must still round-trip with its original bytes.
    ms.Position = 0;
    using var reader = new Ext1Reader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory && e.Name == "secret.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(content), "file content survives the wipe intact");
  }

  [Test]
  public void WipeEmpty_FreeSpace_Zeroed() {
    var content = new byte[BlockSize - 200];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith("data.bin", content);

    var desc = new Ext1FormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);

    // Pollute a free region with a forensic marker.
    ms.Position = 0;
    var free = desc.EnumerateExtents(ms).ToList();
    // The trailing region past the last live extent is free; write into it.
    var marker = "FORENSIC_REMNANT!"u8.ToArray();
    ms.Position = ms.Length - 600;
    ms.Write(marker, 0, marker.Length);
    _ = free;

    desc.WipeUnusedSpace(ms);

    ms.Position = ms.Length - 600;
    var readBack = new byte[marker.Length];
    ms.Read(readBack, 0, readBack.Length);
    Assert.That(readBack, Is.All.EqualTo((byte)0), "free-space remnant must be zeroed");
  }
}
