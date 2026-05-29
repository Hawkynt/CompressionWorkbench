#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

[TestFixture]
public class FatWipeEmptyTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new FatWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  [Test]
  public void FatDescriptorImplementsIWipeEmpty() {
    var desc = new FatFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void WipeEmpty_ZerosDeletedFileContent() {
    // Create an image with a file containing a distinctive marker.
    var marker = System.Text.Encoding.ASCII.GetBytes("SECRETDATA_WIPE_TEST");
    var image = BuildImageWith(("SECRET.TXT", marker), ("KEEP.TXT", new byte[] { 0xAA, 0xBB }));

    // Remove the file (leaves content in clusters, only marks dir entry 0xE5).
    FatRemover.Remove(image, "SECRET.TXT");

    // Verify the marker is still in the raw image (precondition: FatRemover
    // does wipe clusters, but let's use wipe-empty on an image where free
    // clusters still have non-zero data from a simulated deletion).
    // Instead, manually corrupt free clusters to simulate stale data.
    var desc = new FatFormatDescriptor();
    using var ms = new MemoryStream(image);

    // Write junk into free space to simulate a prior deletion that left remnants.
    // Find the first free cluster region via extent map.
    ms.Position = 0;
    var extents = FatExtentMap.Enumerate(ms).ToList();
    var freeExtents = extents.Where(e => e.Kind == DefragBlockKind.Free).ToList();
    Assert.That(freeExtents, Has.Count.GreaterThan(0), "Should have free space after removal");

    // Write distinctive marker into free space.
    var forensicMarker = System.Text.Encoding.ASCII.GetBytes("FORENSIC_REMNANT!");
    foreach (var free in freeExtents) {
      ms.Position = free.Offset;
      for (var i = 0L; i < free.Length && i < 512; i += forensicMarker.Length) {
        var bytesToWrite = (int)Math.Min(forensicMarker.Length, free.Length - i);
        ms.Write(forensicMarker, 0, bytesToWrite);
      }
    }

    // Verify marker is present.
    Assert.That(FindMarker(ms.ToArray(), forensicMarker), Is.True, "Precondition: forensic marker in free space");

    // Wipe empty.
    var wiped = desc.WipeUnusedSpace(ms);
    Assert.That(wiped, Is.GreaterThan(0), "Should have wiped some bytes");

    // Marker must be gone.
    Assert.That(FindMarker(ms.ToArray(), forensicMarker), Is.False,
      "Forensic marker must be wiped from free space");

    // KEEP.TXT must still be readable.
    ms.Position = 0;
    var reader = new FatReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "KEEP.TXT"), Is.True);
    var keepData = reader.Extract(reader.Entries.First(e => e.Name == "KEEP.TXT"));
    Assert.That(keepData, Is.EqualTo(new byte[] { 0xAA, 0xBB }));
  }

  [Test]
  public void WipeEmpty_PreservesLiveFileData() {
    var fileData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
    var image = BuildImageWith(("DATA.BIN", fileData));

    using var ms = new MemoryStream(image);
    var desc = new FatFormatDescriptor();
    desc.WipeUnusedSpace(ms);

    // File must be intact.
    ms.Position = 0;
    var reader = new FatReader(ms);
    var entry = reader.Entries.First(e => e.Name == "DATA.BIN");
    Assert.That(reader.Extract(entry), Is.EqualTo(fileData));
  }

  [Test]
  public void WipeEmpty_ClusterTips_ZerosSlack() {
    // Create a file smaller than a cluster. The cluster-tip (slack) should be zeroed.
    var smallFile = new byte[] { 0x42, 0x43 }; // 2 bytes in a 512-byte cluster
    var image = BuildImageWith(("TINY.BIN", smallFile));

    // Manually write junk into the cluster tip (beyond the 2-byte file).
    // Find the Used extent for TINY.BIN.
    using var ms = new MemoryStream(image);
    var extents = FatExtentMap.Enumerate(ms).ToList();
    var tinyExtent = extents.FirstOrDefault(e => e.Kind == DefragBlockKind.Used && e.FileName == "TINY.BIN");
    Assert.That(tinyExtent, Is.Not.Null, "Should find TINY.BIN extent");

    // The extent covers one full cluster. Write junk after the 2-byte file data.
    if (tinyExtent!.Length > 2) {
      ms.Position = tinyExtent.Offset + 2;
      var junk = new byte[Math.Min(64, tinyExtent.Length - 2)];
      Array.Fill(junk, (byte)0xFF);
      ms.Write(junk);

      // Verify junk is there.
      ms.Position = tinyExtent.Offset + 2;
      var readBack = new byte[junk.Length];
      ms.Read(readBack, 0, readBack.Length);
      Assert.That(readBack[0], Is.EqualTo(0xFF), "Precondition: junk in cluster tip");
    }

    // Wipe with cluster tips enabled.
    var desc = new FatFormatDescriptor();
    var wiped = desc.WipeUnusedSpace(ms, wipeClusterTips: true);

    // Verify cluster tip is now zero.
    if (tinyExtent.Length > 2) {
      ms.Position = tinyExtent.Offset + 2;
      var tipBytes = new byte[Math.Min(64, tinyExtent.Length - 2)];
      ms.Read(tipBytes, 0, tipBytes.Length);
      Assert.That(tipBytes, Is.All.EqualTo((byte)0), "Cluster tip must be zeroed");
    }

    // File data must be intact.
    ms.Position = 0;
    var reader = new FatReader(ms);
    var entry = reader.Entries.First(e => e.Name == "TINY.BIN");
    Assert.That(reader.Extract(entry), Is.EqualTo(smallFile));
  }

  [Test]
  public void WipeEmpty_ReturnsZeroOnCleanImage() {
    // Build an image, wipe it once, then wipe again — second pass should return 0.
    var image = BuildImageWith(("A.TXT", [1, 2, 3]));
    using var ms = new MemoryStream(image);
    var desc = new FatFormatDescriptor();

    // First wipe may or may not find something.
    desc.WipeUnusedSpace(ms);

    // Second wipe should find nothing to wipe.
    var secondWiped = desc.WipeUnusedSpace(ms);
    Assert.That(secondWiped, Is.EqualTo(0), "Second wipe should report 0 bytes (already clean)");
  }

  [Test]
  public void WipeEmpty_ViaGenericWiper_WorksWithExtentMap() {
    var image = BuildImageWith(("FILE.BIN", new byte[100]));
    using var ms = new MemoryStream(image);

    // Write junk into free space.
    var extents = FatExtentMap.Enumerate(ms).ToList();
    var freeExtent = extents.FirstOrDefault(e => e.Kind == DefragBlockKind.Free);
    if (freeExtent != null && freeExtent.Length > 0) {
      ms.Position = freeExtent.Offset;
      ms.WriteByte(0xDE);
    }

    // Use the generic wiper directly.
    ms.Position = 0;
    var extentsForWipe = FatExtentMap.Enumerate(ms);
    var wiped = UnusedSpaceWiper.Wipe(ms, extentsForWipe, ms.Length);
    Assert.That(wiped, Is.GreaterThan(0));

    // Free space should now be zero.
    if (freeExtent != null) {
      ms.Position = freeExtent.Offset;
      Assert.That(ms.ReadByte(), Is.EqualTo(0));
    }
  }

  private static bool FindMarker(byte[] image, byte[] marker) {
    for (var i = 0; i <= image.Length - marker.Length; ++i) {
      var match = true;
      for (var j = 0; j < marker.Length; ++j) {
        if (image[i + j] != marker[j]) { match = false; break; }
      }
      if (match) return true;
    }
    return false;
  }
}
