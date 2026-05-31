#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Adf;

namespace Compression.Tests.Adf;

[TestFixture]
public class AdfWipeEmptyTests {

  // ADF cluster tips are N/A: a file's extent is a coalesced run that interleaves
  // its header/extension blocks with data blocks (and OFS data blocks carry a
  // 24-byte header), so there is no flat offset..offset+size data region whose
  // tail can be treated as slack. These tests verify free-sector wiping instead.

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new AdfWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void DescriptorOffersWipeEmptyCapability() {
    Assert.That(new AdfFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
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
    var free = new AdfFormatDescriptor().EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Free);
    var marker = System.Text.Encoding.ASCII.GetBytes("STALE_REMNANT_ADF");
    ms.Position = free.Offset;
    for (var i = 0L; i + marker.Length <= free.Length && i < 512; i += marker.Length)
      ms.Write(marker);

    // When the unused space is wiped ...
    var wiped = new AdfFormatDescriptor().WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0));

    // ... the live file still round-trips through the reader unchanged ...
    ms.Position = 0;
    using var reader = new AdfReader(ms, leaveOpen: true);
    var entry = reader.Entries.Single(e => !e.IsDirectory && e.Name == "DATA");
    Assert.That(reader.Extract(entry), Is.EqualTo(content));

    // ... and the previously dirty free sector is all zero.
    ms.Position = free.Offset;
    var probe = new byte[Math.Min(512, (int)free.Length)];
    _ = ms.Read(probe, 0, probe.Length);
    Assert.That(probe, Is.All.Zero, "Free sectors must be wiped");
  }
}
