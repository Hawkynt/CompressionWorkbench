#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.D64;

namespace Compression.Tests.D64;

[TestFixture]
public class D64WipeEmptyTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new D64Writer();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void Descriptor_ImplementsIWipeEmpty() {
    Assert.That(new D64FormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void WipeEmpty_ZerosFinalSectorSlack_AndFileRoundTrips() {
    // Given a 100-byte file → one sector chain (2 link bytes + 100 data + slack).
    var content = new byte[100];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("HELLO", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Locate the file's single Used sector extent.
    ms.Position = 0;
    var extent = D64ExtentMap.Enumerate(ms)
      .First(e => e.Kind == DefragBlockKind.Used && e.FileName == "HELLO");
    Assert.That(extent.Length, Is.EqualTo(256), "Single-sector chain");

    // The final sector layout: [0..2) link, [2..2+used) data, [2+used..256) slack.
    // Plant junk in the slack region.
    const int dataStart = 2;
    var usedBytes = content.Length; // 100
    var slackStart = extent.Offset + dataStart + usedBytes;
    var slackLen = extent.Offset + extent.Length - slackStart;
    Assert.That(slackLen, Is.GreaterThan(0), "Tail slack must exist");
    ms.Position = slackStart;
    var junk = new byte[slackLen];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk);

    // When wiping with cluster tips enabled.
    var desc = new D64FormatDescriptor();
    desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    // Then the final-sector slack is all zero.
    ms.Position = slackStart;
    var slack = new byte[slackLen];
    ms.ReadExactly(slack);
    Assert.That(slack, Is.All.EqualTo((byte)0), "Final-sector slack (cluster tip) must be zeroed");

    // And the link bytes + live data are preserved → file round-trips.
    ms.Position = 0;
    var r = new D64Reader(ms);
    var entry = r.Entries.First(e => e.Name == "HELLO");
    Assert.That(r.Extract(entry), Is.EqualTo(content));
  }

  [Test]
  public void WipeEmpty_ZerosFreeSectors() {
    var content = new byte[100];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("HELLO", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Plant a forensic remnant in a free sector.
    ms.Position = 0;
    var free = D64ExtentMap.Enumerate(ms).First(e => e.Kind == DefragBlockKind.Free);
    var remnant = "FORENSIC_REMNANT!"u8.ToArray();
    ms.Position = free.Offset;
    ms.Write(remnant);

    var desc = new D64FormatDescriptor();
    var wiped = desc.WipeUnusedSpace(ms);
    Assert.That(wiped, Is.GreaterThan(0));

    ms.Position = free.Offset;
    var check = new byte[remnant.Length];
    ms.ReadExactly(check);
    Assert.That(check, Is.All.EqualTo((byte)0), "Free-sector remnant must be wiped");

    // File still round-trips.
    ms.Position = 0;
    var r = new D64Reader(ms);
    var entry = r.Entries.First(e => e.Name == "HELLO");
    Assert.That(r.Extract(entry), Is.EqualTo(content));
  }
}
