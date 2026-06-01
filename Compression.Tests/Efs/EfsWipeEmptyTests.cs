#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Efs;

namespace Compression.Tests.Efs;

[TestFixture]
public class EfsWipeEmptyTests {

  [Test, Category("HappyPath")]
  public void Descriptor_OffersWipeEmptyCapability() {
    Assert.That(new EfsFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("HappyPath")]
  public void WipeUnusedSpace_ZerosTrailingFreeBytes_AndKeepsFilesIntact() {
    var w = new EfsWriter();
    w.AddFile("data.bin", "live data"u8.ToArray());
    var img = w.Build();
    using var ms = new MemoryStream();
    ms.Write(img);
    ms.SetLength(img.Length + 4096); // append a stale forensic tail

    // Stripe a marker across the appended tail (after the live image).
    var marker = System.Text.Encoding.ASCII.GetBytes("STALE_REMNANT_EFS");
    ms.Position = img.Length;
    for (var i = 0; i + marker.Length <= 4096; i += marker.Length)
      ms.Write(marker);

    // When the unused space is wiped ...
    ms.Position = 0;
    var wiped = new EfsFormatDescriptor().WipeUnusedSpace(ms);
    Assert.That(wiped, Is.GreaterThan(0));

    // ... the live file still round-trips through the reader ...
    ms.Position = 0;
    var r = new EfsReader(ms);
    var live = r.Entries.First(e => !e.IsDirectory && e.Name == "data.bin");
    Assert.That(r.Extract(live), Is.EqualTo("live data"u8.ToArray()));

    // ... and the marker stripe in the trailing free region is zeroed.
    ms.Position = img.Length;
    var probe = new byte[Math.Min(512, 4096)];
    _ = ms.Read(probe, 0, probe.Length);
    Assert.That(probe, Is.All.Zero);
  }
}
