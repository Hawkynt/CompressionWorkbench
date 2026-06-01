#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.ApplePascal;

namespace Compression.Tests.ApplePascal;

[TestFixture]
public class ApplePascalWipeEmptyTests {

  [Test]
  public void Descriptor_OffersWipeEmptyCapability() {
    Assert.That(new ApplePascalFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void Wipe_ZerosFreeBlocks_KeepsLiveFileIntact() {
    var w = new ApplePascalWriter();
    var payload = Encoding.ASCII.GetBytes("Pascal payload");
    w.AddFile("HELLO.TXT", payload);
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    ms.Position = 0;

    // Stain a region known to be beyond the file's contiguous extent.
    var stainAt = image.Length - 1024;
    if (stainAt > 6 * 512) {
      ms.Position = stainAt;
      ms.Write(Encoding.ASCII.GetBytes("STALE_PASCAL_REMNANT"));
    }

    var d = new ApplePascalFormatDescriptor();
    var wiped = d.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThanOrEqualTo(0));

    ms.Position = 0;
    using var r = new ApplePascalReader(ms);
    var entry = r.Entries.Single(e => !e.IsDirectory && e.Name == "HELLO.TXT");
    Assert.That(r.Extract(entry).AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));

    // Stain location is now zero.
    if (stainAt > 6 * 512) {
      ms.Position = stainAt;
      var probe = new byte[20];
      var read = ms.Read(probe, 0, probe.Length);
      Assert.That(read, Is.GreaterThan(0));
      Assert.That(probe, Is.All.Zero, "Wiped region must be zero.");
    }
  }
}
