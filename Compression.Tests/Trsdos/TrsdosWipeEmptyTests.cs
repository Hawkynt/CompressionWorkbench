#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Trsdos;

namespace Compression.Tests.Trsdos;

/// <summary>
/// Verifies the <see cref="IWipeEmpty"/> implementation: free sectors
/// are zero-filled, track 17 (GAT + HIT + directory) and live file data
/// are preserved, and the rewritten image still parses.
/// </summary>
[TestFixture]
public class TrsdosWipeEmptyTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new TrsdosWriter();
    w.SetGeometry(80, 18); // plenty of free space.
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  [Test]
  public void DescriptorAdvertisesWipeEmpty() {
    Assert.That(new TrsdosFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("HappyPath")]
  public void WipingUnusedSpace_ZerosFreeSectors_KeepsDirectoryAndFiles() {
    var content = new byte[64];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImage(("DATA.BIN", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var d = new TrsdosFormatDescriptor();

    // Smear a forensic marker at the tail of the image.
    var trailStart = Math.Max(0L, ms.Length - 4096);
    ms.Position = trailStart;
    var marker = System.Text.Encoding.ASCII.GetBytes("STALE_TRSDOS_REMNANT");
    var room = Math.Min(2048L, ms.Length - trailStart);
    for (long i = 0; i + marker.Length <= room; i += marker.Length) ms.Write(marker);

    ms.Position = 0;
    var wiped = d.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0), "Some bytes must be wiped.");

    // GAT signature byte must still be intact at track 17 sector 0 offset 0xCD.
    var gatOff = 17 * 18 * 256;
    ms.Position = gatOff + 0xCD;
    Assert.That(ms.ReadByte(), Is.EqualTo(0xFE), "GAT signature must survive wiping.");

    // Live file is still readable.
    ms.Position = 0;
    using var r = new TrsdosReader(ms);
    Assert.That(r.ValidVolume, Is.True);
    var entry = r.Entries.Single(e => e.Name == "DATA.BIN");
    var extracted = r.Extract(entry);
    Assert.That(extracted.AsSpan(0, content.Length).ToArray(), Is.EqualTo(content),
      "Live file data must survive wiping.");

    // Tail region must be zero.
    ms.Position = ms.Length - 256;
    var probe = new byte[256];
    _ = ms.Read(probe, 0, probe.Length);
    Assert.That(probe, Is.All.Zero, "Trailing free region must be wiped.");
  }
}
