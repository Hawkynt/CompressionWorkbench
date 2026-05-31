#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.AppleDos;

namespace Compression.Tests.AppleDos;

[TestFixture]
public class AppleDosWipeEmptyTests {

  // AppleDOS cluster tips are N/A: a file's extent is a coalesced run that
  // interleaves its track/sector-list sectors with the data sectors, so there
  // is no flat offset..offset+size data region whose tail can be treated as
  // slack. These tests verify free-sector wiping instead.

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new AppleDosWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void DescriptorOffersWipeEmptyCapability() {
    Assert.That(new AppleDosFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
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
    var free = new AppleDosFormatDescriptor().EnumerateExtents(ms)
      .First(e => e.Kind == DefragBlockKind.Free);
    var marker = System.Text.Encoding.ASCII.GetBytes("STALE_REMNANT_DOS");
    ms.Position = free.Offset;
    for (var i = 0L; i + marker.Length <= free.Length && i < 256; i += marker.Length)
      ms.Write(marker);

    // When the unused space is wiped ...
    var wiped = new AppleDosFormatDescriptor().WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0));

    // ... the live file still extracts with its content intact (AppleDOS extracts
    // at sector granularity, so the result is padded up to the sector boundary;
    // wiping must not touch the bytes that belong to the file) ...
    ms.Position = 0;
    using var reader = new AppleDosReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "DATA");
    var extracted = reader.Extract(entry);
    Assert.That(extracted.Length, Is.GreaterThanOrEqualTo(content.Length));
    Assert.That(extracted.Take(content.Length), Is.EqualTo(content));

    // ... and the previously dirty free sector is all zero.
    ms.Position = free.Offset;
    var probe = new byte[Math.Min(256, (int)free.Length)];
    _ = ms.Read(probe, 0, probe.Length);
    Assert.That(probe, Is.All.Zero, "Free sectors must be wiped");
  }
}
