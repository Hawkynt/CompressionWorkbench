#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Atari8;

namespace Compression.Tests.Atari8;

[TestFixture]
public class Atari8WipeEmptyTests {

  // AtariDOS cluster tips are N/A: every data sector ends with a 3-byte link
  // trailer (file number, next sector, byte count), so each sector mixes data
  // with metadata and there is no flat offset..offset+size data region whose
  // tail can be treated as slack. These tests verify free-sector wiping instead.

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new Atari8Writer();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void DescriptorOffersWipeEmptyCapability() {
    Assert.That(new Atari8FormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void WipingUnusedSpace_ZerosFreeSectors_AndKeepsFileIntact() {
    var content = new byte[100];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("DATA", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Smear a forensic marker across the first free sector.
    ms.Position = 0;
    var free = new Atari8FormatDescriptor().EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Free);
    var marker = System.Text.Encoding.ASCII.GetBytes("STALE_REMNANT_A8");
    ms.Position = free.Offset;
    for (var i = 0L; i + marker.Length <= free.Length && i < 128; i += marker.Length)
      ms.Write(marker);

    // When the unused space is wiped ...
    var wiped = new Atari8FormatDescriptor().WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0));

    // ... the live file still round-trips through the reader unchanged ...
    ms.Position = 0;
    using var reader = new Atari8Reader(ms);
    var entry = reader.Entries.Single(e => e.Name == "DATA");
    Assert.That(reader.Extract(entry), Is.EqualTo(content));

    // ... and the previously dirty free sector is all zero.
    ms.Position = free.Offset;
    var probe = new byte[Math.Min(128, (int)free.Length)];
    _ = ms.Read(probe, 0, probe.Length);
    Assert.That(probe, Is.All.Zero, "Free sectors must be wiped");
  }
}
