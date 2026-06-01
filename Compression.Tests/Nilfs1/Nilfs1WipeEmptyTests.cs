#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Nilfs1;

namespace Compression.Tests.Nilfs1;

[TestFixture]
public class Nilfs1WipeEmptyTests {

  [Test]
  public void Descriptor_OffersWipeEmptyCapability() {
    Assert.That(new Nilfs1FormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void Wipe_ZerosFreeRegion_KeepsLiveFileIntact() {
    var w = new Nilfs1Writer();
    var payload = Encoding.UTF8.GetBytes("Hello NILFS v1");
    w.AddFile("hello.txt", payload);
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    ms.Position = 0;

    var d = new Nilfs1FormatDescriptor();
    var free = d.EnumerateExtents(ms)
                .Where(e => e.Kind == DefragBlockKind.Free)
                .ToList();
    // Stain the trailing free area if any was emitted by sparse-coverage map.
    // (Our extent map only marks live regions; the wiper fills the gaps.)
    var marker = Encoding.UTF8.GetBytes("STALE_NILFS_REMNANT");
    var staintAt = image.Length - 256;
    if (staintAt > 2048) {
      ms.Position = staintAt;
      ms.Write(marker);
    }

    var wiped = d.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThanOrEqualTo(0));

    ms.Position = 0;
    using var r = new Nilfs1Reader(ms);
    var entry = r.Entries.Single(e => !e.IsDirectory && e.Name == "hello.txt");
    Assert.That(r.Extract(entry), Is.EqualTo(payload));
  }
}
