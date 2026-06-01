#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Cromemco;

namespace Compression.Tests.Cromemco;

/// <summary>
/// Verifies the <see cref="IWipeEmpty"/> implementation: free sectors
/// are zero-filled, the boot block + directory + live file data are
/// preserved, and the rewritten image still parses.
/// </summary>
[TestFixture]
public class CromemcoWipeEmptyTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new CromemcoWriter();
    w.SetGeometry(77, 26); // ample free space.
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  [Test]
  public void DescriptorAdvertisesWipeEmpty() {
    Assert.That(new CromemcoFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test, Category("HappyPath")]
  public void WipingUnusedSpace_ZerosFreeSectors_KeepsBootAndFiles() {
    var content = new byte[64];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImage(("DATA.BIN", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    var d = new CromemcoFormatDescriptor();

    // Smear a forensic marker across the first free region.
    ms.Position = 0;
    var free = d.EnumerateExtents(ms)
      .Select(e => e.Offset + e.Length)
      .DefaultIfEmpty(0L)
      .Max();
    var trailStart = free;
    if (trailStart < ms.Length) {
      ms.Position = trailStart;
      var marker = System.Text.Encoding.ASCII.GetBytes("STALE_CROMEMCO_REMNANT");
      var room = Math.Min(256L, ms.Length - trailStart);
      for (long i = 0; i + marker.Length <= room; i += marker.Length) ms.Write(marker);
    }

    ms.Position = 0;
    var wiped = d.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0), "At least some trailing-free bytes must be wiped.");

    // Boot block JP and "CROMEMCO" tag must still be intact.
    Assert.That(image[0], Is.EqualTo((byte)0xC3));
    ms.Position = 0;
    var firstByte = ms.ReadByte();
    Assert.That(firstByte, Is.EqualTo(0xC3));

    // Reader still produces the file with its content.
    ms.Position = 0;
    using var r = new CromemcoReader(ms);
    Assert.That(r.ValidVolume, Is.True);
    var entry = r.Entries.Single(e => e.Name == "DATA.BIN");
    var extracted = r.Extract(entry);
    Assert.That(extracted.AsSpan(0, content.Length).ToArray(), Is.EqualTo(content),
      "Live file data must survive wiping.");

    // The trailing wiped region is all zero.
    ms.Position = trailStart;
    var probe = new byte[Math.Min(64, (int)(ms.Length - trailStart))];
    _ = ms.Read(probe, 0, probe.Length);
    Assert.That(probe, Is.All.Zero, "Free region must be wiped.");
  }
}
