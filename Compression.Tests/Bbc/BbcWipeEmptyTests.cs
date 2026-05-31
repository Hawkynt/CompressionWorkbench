#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Bbc;

namespace Compression.Tests.Bbc;

[TestFixture]
public class BbcWipeEmptyTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new BbcWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void DescriptorOffersWipeEmptyCapability() {
    Assert.That(new BbcFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void WipingClusterTip_ZerosSectorSlack_AndKeepsFileIntact() {
    // A file deliberately shorter than one 256-byte sector leaves tail slack.
    const int fileSize = 200;
    var content = new byte[fileSize];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("DATA", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Locate the file's used extent and dirty its cluster tip with junk.
    ms.Position = 0;
    var fileExtent = BbcExtentMap.Enumerate(ms)
      .Single(e => e.Kind == DefragBlockKind.Used && e.FileName == "$.DATA");
    Assert.That(fileExtent.Length, Is.GreaterThan(fileSize), "Sector slack expected");

    ms.Position = fileExtent.Offset + fileSize;
    var junk = new byte[fileExtent.Length - fileSize];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk);

    // When the unused space is wiped with cluster tips enabled ...
    var wiped = new BbcFormatDescriptor().WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0));

    // ... the live file still round-trips through the reader unchanged ...
    ms.Position = 0;
    using var reader = new BbcReader(ms);
    var entry = reader.Entries.Single(e => e.FullName == "$.DATA");
    Assert.That(reader.Extract(entry), Is.EqualTo(content));

    // ... and the cluster tip (file end .. sector end) is all zero.
    var tip = new byte[fileExtent.Length - fileSize];
    ms.Position = fileExtent.Offset + fileSize;
    _ = ms.Read(tip, 0, tip.Length);
    Assert.That(tip, Is.All.Zero, "Cluster tip must be wiped");
  }

  [Test]
  public void WipingFreeSpace_ZerosStaleRemnants_AndKeepsFileIntact() {
    var content = new byte[300];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("KEEP", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Smear a forensic marker across the first free sector.
    ms.Position = 0;
    var free = BbcExtentMap.Enumerate(ms).First(e => e.Kind == DefragBlockKind.Free);
    var marker = System.Text.Encoding.ASCII.GetBytes("STALE_REMNANT_BBC");
    ms.Position = free.Offset;
    for (var i = 0L; i + marker.Length <= free.Length && i < 256; i += marker.Length)
      ms.Write(marker);

    new BbcFormatDescriptor().WipeUnusedSpace(ms);

    ms.Position = 0;
    using var reader = new BbcReader(ms);
    Assert.That(reader.Extract(reader.Entries.Single(e => e.FullName == "$.KEEP")), Is.EqualTo(content));

    ms.Position = free.Offset;
    var probe = new byte[Math.Min(256, free.Length)];
    _ = ms.Read(probe, 0, probe.Length);
    Assert.That(probe, Is.All.Zero, "Free sectors must be wiped");
  }
}
