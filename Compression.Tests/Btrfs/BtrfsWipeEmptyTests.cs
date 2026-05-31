#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Btrfs;

namespace Compression.Tests.Btrfs;

[TestFixture]
public class BtrfsWipeEmptyTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new BtrfsWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test]
  public void Descriptor_ImplementsIWipeEmpty() {
    Assert.That(new BtrfsFormatDescriptor(), Is.InstanceOf<IWipeEmpty>());
  }

  [Test]
  public void WipeEmpty_ZerosForensicRemnantInFreeDataRegion_AndPreservesFile() {
    // Given a Btrfs image with one small (inline) file.
    var content = new byte[200];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("secret.bin", content));

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);

    // And a forensic remnant planted in a free region (a Free gap in the map).
    ms.Position = 0;
    var freeRegion = new BtrfsFormatDescriptor()
      .EnumerateExtents(ms)
      .Where(e => e.Kind == DefragBlockKind.Used)
      .ToList();
    // The reserved DATA chunk [0x80000, 0xC0000) carries no live extent, so it
    // is treated as free. Plant the remnant there.
    var remnant = "FORENSIC_REMNANT!"u8.ToArray();
    const long dataRegion = 0x80000;
    ms.Position = dataRegion;
    ms.Write(remnant);

    Assert.That(FindMarker(ms.ToArray(), remnant), Is.True, "Precondition: remnant present in free space");

    // When wiping unused space.
    var desc = new BtrfsFormatDescriptor();
    var wiped = desc.WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);

    // Then the remnant is gone.
    Assert.That(wiped, Is.GreaterThan(0));
    Assert.That(FindMarker(ms.ToArray(), remnant), Is.False, "Free-space remnant must be wiped");

    // And the file still round-trips intact (inline data lives in metadata, untouched).
    ms.Position = 0;
    using var reader = new BtrfsReader(ms);
    var entry = reader.Entries.First(e => !e.IsDirectory && e.Name == "secret.bin");
    Assert.That(reader.Extract(entry), Is.EqualTo(content));
  }

  // Cluster tips are N/A for the Btrfs WORM writer: file data is stored as
  // inline EXTENT_DATA inside the metadata leaf, byte-exact with no allocation
  // slack, so there is no on-disk data extent whose tail could hold slack.
  [Test]
  public void WipeEmpty_HasNoClusterTips_BecauseDataIsInlinePacked() {
    var content = new byte[100];
    Array.Fill(content, (byte)0xAA);
    var image = BuildImageWith(("tiny.bin", content));

    using var ms = new MemoryStream(image);
    var usedDataExtents = new BtrfsFormatDescriptor()
      .EnumerateExtents(ms)
      .Where(e => e.Kind == DefragBlockKind.Used)
      .ToList();
    // No Used (on-disk data) extents — everything is inline MetadataReserved.
    Assert.That(usedDataExtents, Is.Empty, "Inline writer produces no separate data extents → no cluster tips");
  }

  private static bool FindMarker(byte[] image, byte[] marker) {
    for (var i = 0; i <= image.Length - marker.Length; ++i) {
      var match = true;
      for (var j = 0; j < marker.Length; ++j)
        if (image[i + j] != marker[j]) { match = false; break; }
      if (match) return true;
    }
    return false;
  }
}
