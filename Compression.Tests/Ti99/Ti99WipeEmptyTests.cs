#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Ti99;

namespace Compression.Tests.Ti99;

[TestFixture]
public class Ti99WipeEmptyTests {

  [Test]
  public void Descriptor_OffersWipeEmptyCapability() {
    Assert.That(new Ti99FormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void Wipe_SectorDump_ZerosFreeRegions_KeepsLiveFilesIntact() {
    var w = new Ti99Writer();
    var payload = Encoding.ASCII.GetBytes("Live TI-99 file");
    w.AddFile("HELLO", payload);
    var image = w.BuildSectorDump();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    ms.Position = 0;

    var stainAt = image.Length - 256;
    if (stainAt > 256 * 4) {
      ms.Position = stainAt;
      ms.Write(Encoding.ASCII.GetBytes("STALE_TI99_REMNANT"));
    }

    var d = new Ti99FormatDescriptor();
    var wiped = d.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThanOrEqualTo(0));

    ms.Position = 0;
    using var r = new Ti99Reader(ms);
    var entry = r.Entries.Single(e => !e.IsDirectory && e.Name == "HELLO");
    var got = r.Extract(entry);
    Assert.That(got.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));

    // Stain region zero again.
    if (stainAt > 256 * 4) {
      ms.Position = stainAt;
      var probe = new byte[20];
      ms.Read(probe, 0, probe.Length);
      Assert.That(probe, Is.All.Zero, "Wiped region must be zero.");
    }
  }
}
