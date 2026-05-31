#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.TrDos;

namespace Compression.Tests.TrDos;

[TestFixture]
public class TrDosWipeEmptyTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new TrDosWriter();
    foreach (var (name, data) in files) w.AddFile(name, 'C', data);
    return w.Build();
  }

  [Test]
  public void Descriptor_ImplementsIWipeEmpty() {
    Assert.That(new TrDosFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  // TR-DOS stores files in whole 256-byte sectors and the reader round-trips the
  // full sector run as the file content — there is no sub-sector logical length,
  // so cluster tips are N/A. The wiper zeros only the free sectors; the live file
  // must round-trip intact.
  [Test]
  public void WipeEmpty_ZerosDirtiedFreeRegion_AndFileRoundTrips() {
    var content = new byte[200]; // under one 256-byte sector
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("DATA", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Dirty a free region.
    ms.Position = 0;
    var freeExtent = TrDosExtentMap.Enumerate(ms)
      .First(e => e.Kind == DefragBlockKind.Free && e.Length >= 64);
    var dirtyOffset = freeExtent.Offset;
    ms.Position = dirtyOffset;
    var junk = new byte[64];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk);

    ms.Position = dirtyOffset;
    Assert.That(ms.ReadByte(), Is.EqualTo(0xFF), "Precondition: dirtied free region");

    var desc = new TrDosFormatDescriptor();
    var wiped = desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0), "Should have wiped the dirtied free region");

    ms.Position = dirtyOffset;
    var readBack = new byte[64];
    ms.ReadExactly(readBack);
    Assert.That(readBack, Is.All.EqualTo((byte)0), "Free region must be zeroed");

    // File still round-trips (TR-DOS returns the whole stored sector run).
    ms.Position = 0;
    using var reader = new TrDosReader(ms);
    var entry = reader.Entries.First(e => e.Name.StartsWith("DATA", StringComparison.Ordinal));
    var extracted = reader.Extract(entry);
    Assert.That(extracted.AsSpan(0, content.Length).ToArray(), Is.EqualTo(content),
      "File content must survive the wipe");
  }
}
