#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Htfs;

namespace Compression.Tests.Htfs;

[TestFixture]
public class HtfsWipeEmptyTests {

  [Test, Category("HappyPath")]
  public void Descriptor_OffersWipeEmptyCapability() {
    Assert.That(new HtfsFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("HappyPath")]
  public void WipeUnusedSpace_ZerosTrailingFreeBytes_AndKeepsFilesIntact() {
    var w = new HtfsWriter();
    w.AddFile("data.bin", "live data"u8.ToArray());
    var img = w.Build();
    using var ms = new MemoryStream();
    ms.Write(img);
    ms.SetLength(img.Length + 4096);

    var marker = System.Text.Encoding.ASCII.GetBytes("STALE_REMNANT_HTFS");
    ms.Position = img.Length;
    for (var i = 0; i + marker.Length <= 4096; i += marker.Length)
      ms.Write(marker);

    ms.Position = 0;
    var wiped = new HtfsFormatDescriptor().WipeUnusedSpace(ms);
    Assert.That(wiped, Is.GreaterThan(0));

    ms.Position = 0;
    var r = new HtfsReader(ms);
    var live = r.Entries.First(e => !e.IsDirectory && e.Name == "data.bin");
    Assert.That(r.Extract(live), Is.EqualTo("live data"u8.ToArray()));

    ms.Position = img.Length;
    var probe = new byte[Math.Min(512, 4096)];
    _ = ms.Read(probe, 0, probe.Length);
    Assert.That(probe, Is.All.Zero);
  }
}
