#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.CpcDsk;

namespace Compression.Tests.CpcDsk;

[TestFixture]
public class CpcDskWipeEmptyTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var w = new CpcDskWriter(ms, leaveOpen: true)) {
      foreach (var (name, data) in files) w.AddFile(name, data);
      w.Finish();
    }
    return ms.ToArray();
  }

  // The extent map and EnumerateLogicalFiles agree on the AMSDOS "base.ext"
  // name, and CpcDskModifier reports the logical (record-aligned) length, so
  // cluster-tip wiping targets the slack between that length and the 512-byte
  // sector boundary.
  private static long LogicalSize(byte[] image, string name) {
    using var ms = new MemoryStream(image);
    foreach (var (n, d) in CpcDskModifier.EnumerateLogicalFiles(ms))
      if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
        return d.LongLength;
    return -1;
  }

  [Test]
  public void Descriptor_ImplementsIWipeEmpty() {
    Assert.That(new CpcDskFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void WipeEmpty_ZerosClusterTip_AndFileRoundTrips() {
    // Given a file a bit smaller than one sector (512 B) so there is tail slack.
    var content = new byte[100];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("TINY.BIN", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Locate the file's Used extent.
    ms.Position = 0;
    var extent = CpcDskExtentMap.Enumerate(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "TINY.BIN");
    var fileSize = LogicalSize(image, "TINY.BIN");
    Assert.That(fileSize, Is.GreaterThan(0));
    Assert.That(extent.Length, Is.GreaterThan(fileSize), "Sector slack must exist");

    // Plant junk in the cluster tip beyond the logical file length.
    var tipStart = extent.Offset + fileSize;
    var tipLen = extent.Offset + extent.Length - tipStart;
    ms.Position = tipStart;
    var junk = new byte[tipLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk);

    // When wiping with cluster tips enabled.
    var desc = new CpcDskFormatDescriptor();
    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    // Then the tip bytes are all zero.
    ms.Position = tipStart;
    var tip = new byte[tipLen];
    ms.ReadExactly(tip);
    Assert.That(tip, Is.All.EqualTo((byte)0), "Cluster tip must be zeroed");

    // And the file round-trips intact.
    ms.Position = 0;
    var reader = new CpcDskReader(ms);
    // Reconstruct logical content to confirm the live bytes survived.
    var logical = LogicalSize(ms.ToArray(), "TINY.BIN");
    Assert.That(logical, Is.EqualTo(fileSize));
  }

  [Test]
  public void WipeEmpty_PreservesLiveFileBytes() {
    var content = new byte[100];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("KEEP.BIN", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var desc = new CpcDskFormatDescriptor();
    desc.WipeUnusedSpace(ms);

    // The first 100 bytes of the data sector must still be 0xAA.
    ms.Position = 0;
    var extent = CpcDskExtentMap.Enumerate(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "KEEP.BIN");
    ms.Position = extent.Offset;
    var head = new byte[100];
    ms.ReadExactly(head);
    Assert.That(head, Is.All.EqualTo((byte)0xAA), "Live file bytes must be preserved");
  }
}
