#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Jfs1;

namespace Compression.Tests.Jfs1;

[TestFixture]
public class Jfs1WipeEmptyTests {

  [Test, Category("HappyPath")]
  public void Descriptor_OffersWipeEmptyCapability() {
    Assert.That(new Jfs1FormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("HappyPath")]
  public void WipeUnusedSpace_ZerosTrailingFreeBytes_AndKeepsFilesIntact() {
    var w = new Jfs1Writer();
    w.AddFile("data.bin", "live data"u8.ToArray());
    var img = w.Build();
    using var ms = new MemoryStream();
    ms.Write(img);
    ms.SetLength(img.Length + 16384);

    var marker = System.Text.Encoding.ASCII.GetBytes("STALE_REMNANT_JFS1");
    ms.Position = img.Length;
    for (var i = 0; i + marker.Length <= 16384; i += marker.Length)
      ms.Write(marker);

    ms.Position = 0;
    var wiped = new Jfs1FormatDescriptor().WipeUnusedSpace(ms);
    Assert.That(wiped, Is.GreaterThan(0));

    ms.Position = 0;
    var r = new Jfs1Reader(ms);
    var live = r.Entries.First(e => !e.IsDirectory && e.Name == "data.bin");
    Assert.That(r.Extract(live), Is.EqualTo("live data"u8.ToArray()));

    ms.Position = img.Length;
    var probe = new byte[Math.Min(512, 16384)];
    _ = ms.Read(probe, 0, probe.Length);
    Assert.That(probe, Is.All.Zero);
  }
}
