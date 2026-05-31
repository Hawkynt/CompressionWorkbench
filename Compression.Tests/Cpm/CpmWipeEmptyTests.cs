#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Cpm;

namespace Compression.Tests.Cpm;

[TestFixture]
public class CpmWipeEmptyTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var list = files.Select(f => (f.Name, f.Data, (byte)0)).ToList();
    return CpmWriter.Build(list);
  }

  private static long LogicalSize(byte[] image, string fullName) {
    var v = CpmReader.Read(image);
    var f = v.Files.FirstOrDefault(x => string.Equals(x.FullName, fullName, StringComparison.OrdinalIgnoreCase));
    return f?.Data.LongLength ?? -1;
  }

  [Test]
  public void Descriptor_ImplementsIWipeEmpty() {
    Assert.That(new CpmFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void WipeEmpty_ZerosClusterTip_AndFileRoundTrips() {
    // Given a file smaller than one 1024-byte allocation block → tail slack.
    var content = new byte[100];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("TINY.BIN", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    ms.Position = 0;
    var extent = CpmExtentMap.Enumerate(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "TINY.BIN");
    var fileSize = LogicalSize(image, "TINY.BIN");
    Assert.That(fileSize, Is.GreaterThan(0));
    Assert.That(extent.Length, Is.GreaterThan(fileSize), "Block slack must exist");

    // Plant junk in the cluster tip beyond the logical file length.
    var tipStart = extent.Offset + fileSize;
    var tipLen = extent.Offset + extent.Length - tipStart;
    ms.Position = tipStart;
    var junk = new byte[tipLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk);

    // When wiping with cluster tips enabled.
    var desc = new CpmFormatDescriptor();
    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    // Then the tip bytes are all zero.
    ms.Position = tipStart;
    var tip = new byte[tipLen];
    ms.ReadExactly(tip);
    Assert.That(tip, Is.All.EqualTo((byte)0), "Cluster tip must be zeroed");

    // And the file round-trips: the first 100 live bytes survive.
    ms.Position = extent.Offset;
    var head = new byte[100];
    ms.ReadExactly(head);
    Assert.That(head, Is.All.EqualTo((byte)0xAA), "Live file bytes must be preserved");
  }

  [Test]
  public void WipeEmpty_ZerosFreeBlocks() {
    var content = new byte[100];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("KEEP.BIN", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Plant a forensic remnant in a free data block.
    ms.Position = 0;
    var free = CpmExtentMap.Enumerate(ms).First(e => e.Kind == DefragBlockKind.Free);
    var remnant = "FORENSIC_REMNANT!"u8.ToArray();
    ms.Position = free.Offset;
    ms.Write(remnant);

    var desc = new CpmFormatDescriptor();
    var wiped = desc.WipeUnusedSpace(ms);
    Assert.That(wiped, Is.GreaterThan(0));

    ms.Position = free.Offset;
    var check = new byte[remnant.Length];
    ms.ReadExactly(check);
    Assert.That(check, Is.All.EqualTo((byte)0), "Free block remnant must be wiped");
  }
}
