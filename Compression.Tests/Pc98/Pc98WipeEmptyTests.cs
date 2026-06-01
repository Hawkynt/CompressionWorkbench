#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Pc98;

namespace Compression.Tests.Pc98;

/// <summary>
/// Verifies the <see cref="IWipeEmpty"/> implementation: trailing free
/// clusters are zeroed, IPL block + FAT + root directory + live file
/// data are preserved, and the rewritten image still parses.
/// </summary>
[TestFixture]
public class Pc98WipeEmptyTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new Pc98Writer();
    w.SetBytesPerSector(512);
    w.SetSectorsPerCluster(1);
    w.SetTotalSectors(64);
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  [Test]
  public void DescriptorAdvertisesWipeEmpty() {
    Assert.That(new Pc98FormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("HappyPath")]
  public void WipingUnusedSpace_ZerosFreeSectors_KeepsHeaderAndFiles() {
    var content = new byte[64];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImage(("DATA.BIN", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var d = new Pc98FormatDescriptor();

    // Smear a forensic marker at the tail.
    var trailStart = Math.Max(0L, ms.Length - 2048);
    ms.Position = trailStart;
    var marker = System.Text.Encoding.ASCII.GetBytes("STALE_PC98_REMNANT");
    var room = Math.Min(1024L, ms.Length - trailStart);
    for (long i = 0; i + marker.Length <= room; i += marker.Length) ms.Write(marker);

    ms.Position = 0;
    var wiped = d.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0));

    // "NECIPL" tag at offset 0 must survive.
    ms.Position = 0;
    var tag = new byte[6];
    _ = ms.Read(tag, 0, 6);
    Assert.That(System.Text.Encoding.ASCII.GetString(tag), Is.EqualTo("NECIPL"));

    // File still readable.
    ms.Position = 0;
    using var r = new Pc98Reader(ms);
    Assert.That(r.ValidVolume, Is.True);
    var entry = r.Entries.Single(e => e.Name.StartsWith("DATA", StringComparison.OrdinalIgnoreCase));
    var extracted = r.Extract(entry);
    Assert.That(extracted.AsSpan(0, content.Length).ToArray(), Is.EqualTo(content));

    // Tail region zero.
    ms.Position = ms.Length - 128;
    var probe = new byte[128];
    _ = ms.Read(probe, 0, probe.Length);
    Assert.That(probe, Is.All.Zero, "Trailing free region must be wiped.");
  }
}
