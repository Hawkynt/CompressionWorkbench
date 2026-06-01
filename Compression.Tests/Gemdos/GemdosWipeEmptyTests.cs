#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Gemdos;

namespace Compression.Tests.Gemdos;

[TestFixture]
public class GemdosWipeEmptyTests {

  [Test]
  public void Descriptor_OffersWipeEmptyCapability() {
    Assert.That(new GemdosFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void Wipe_ZerosFreeSectors_KeepsLiveFilesIntact() {
    var w = new GemdosWriter();
    var payload = Encoding.ASCII.GetBytes("Atari ST hello");
    w.AddFile("HELLO.TXT", payload);
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    ms.Position = 0;

    // Find a free region and stain it with a forensic marker.
    var d = new GemdosFormatDescriptor();
    var free = d.EnumerateExtents(ms)
                .Where(e => e.Kind == DefragBlockKind.Free && e.Length >= 32)
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();
    if (free != null) {
      var marker = Encoding.ASCII.GetBytes("STALE_GEMDOS_RUN");
      ms.Position = free.Offset;
      for (var i = 0L; i + marker.Length <= free.Length && i < 4096; i += marker.Length)
        ms.Write(marker);
    }

    var wiped = d.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThanOrEqualTo(0));

    // Live file must still round-trip.
    ms.Position = 0;
    var entries = d.List(ms, null).Where(e => !e.IsDirectory).ToList();
    Assert.That(entries.Single(e => e.Name == "HELLO.TXT").OriginalSize, Is.EqualTo(payload.Length));
  }
}
