#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Rt11;

namespace Compression.Tests.Rt11;

[TestFixture]
public class Rt11WipeEmptyTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files)
    => Rt11Writer.Build(files.Select(f => (f.Name, f.Data)).ToList());

  [Test]
  public void Descriptor_ImplementsIWipeEmpty() {
    Assert.That(new Rt11FormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  // RT-11 stores files contiguously in whole 512-byte blocks and records only a
  // block count — there is no sub-block logical length, so cluster tips are N/A.
  // The wiper zeros only the free regions; the live file must round-trip intact.
  [Test]
  public void WipeEmpty_ZerosDirtiedFreeRegion_AndFileRoundTrips() {
    // One block (512 bytes) of content, smaller than a block on purpose so the
    // remaining bytes of the block are part of the stored run (not a tip).
    var content = new byte[300];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("DATA.BIN", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // Dirty a free region to simulate a stale deletion remnant.
    ms.Position = 0;
    var freeExtent = Rt11ExtentMap.Enumerate(ms)
      .FirstOrDefault(e => e.Kind == DefragBlockKind.Free && e.Length > 0)
      ?? Rt11ExtentMap.Enumerate(ms).Last(); // fallback: trailing area
    var dirtyOffset = freeExtent.Offset;
    ms.Position = dirtyOffset;
    var junk = new byte[64];
    Array.Fill(junk, (byte)0xFF);
    ms.Write(junk);

    ms.Position = dirtyOffset;
    Assert.That(ms.ReadByte(), Is.EqualTo(0xFF), "Precondition: dirtied free region");

    var desc = new Rt11FormatDescriptor();
    var wiped = desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0), "Should have wiped the dirtied free region");

    // The dirtied region must now be zero.
    ms.Position = dirtyOffset;
    var readBack = new byte[64];
    ms.ReadExactly(readBack);
    Assert.That(readBack, Is.All.EqualTo((byte)0), "Free region must be zeroed");

    // File still round-trips (RT-11 returns the whole stored block run).
    ms.Position = 0;
    using var rms = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(rms);
    var vol = Rt11Reader.Read(rms.GetBuffer().AsSpan(0, (int)rms.Length));
    var entry = vol.Files.First(f => f.Name == "DATA.BIN");
    var extracted = Rt11Reader.Extract(vol, entry);
    Assert.That(extracted.AsSpan(0, content.Length).ToArray(), Is.EqualTo(content),
      "File content must survive the wipe");
  }
}
