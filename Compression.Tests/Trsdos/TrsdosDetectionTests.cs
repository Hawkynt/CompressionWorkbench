using System.Text;
using Compression.Registry;

namespace Compression.Tests.Trsdos;

[TestFixture]
public class TrsdosDetectionTests {

  // Build a minimal TRSDOS sector-dump image with the 0xFE GAT signature
  // and one directory entry.
  private static byte[] BuildMinimalImage() {
    const int sectorSize = 256;
    const int spt = 18; // sectors per track (DD)
    const int dirTrack = 17;
    var totalSectors = (dirTrack + 5) * spt; // a few tracks beyond directory
    var img = new byte[totalSectors * sectorSize];

    var trackOffset = dirTrack * spt * sectorSize;
    // GAT sector 0 of track 17: signature 0xFE at offset 0xCD.
    img[trackOffset + 0xCD] = 0xFE;

    // First directory record at sector 2 of track 17.
    var dirOff = trackOffset + sectorSize * 2;
    img[dirOff + 0] = 0x10; // attributes: visible system file
    // Filename (8 chars) at offset 5..12, ext (3 chars) at offset 13..15.
    Encoding.ASCII.GetBytes("FOO     ").CopyTo(img.AsSpan(dirOff + 5, 8));
    Encoding.ASCII.GetBytes("BAR").CopyTo(img.AsSpan(dirOff + 13, 3));
    img[dirOff + 27] = 0;   // eof low
    img[dirOff + 28] = 1;   // sector count low
    img[dirOff + 29] = 0;
    img[dirOff + 30] = 0;   // eof high
    img[dirOff + 24] = 1;   // first granule (1 → sector 5)
    return img;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Trsdos.TrsdosFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Trsdos"));
    Assert.That(d.DisplayName, Is.EqualTo("TRSDOS / LDOS"));
    Assert.That(d.Extensions, Does.Contain(".dmk"));
    Assert.That(d.Extensions, Does.Contain(".jv1"));
    Assert.That(d.Extensions, Does.Contain(".jv3"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Detect_GatSignature() {
    var img = BuildMinimalImage();
    using var r = new FileSystem.Trsdos.TrsdosReader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.True);
    Assert.That(r.SectorsPerTrack, Is.EqualTo(18));
    Assert.That(r.Entries, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("FOO.BAR"));
  }

  [Test, Category("Sad")]
  public void Detect_NotTrsdos_HasNoValidVolume() {
    var img = new byte[1024];
    using var r = new FileSystem.Trsdos.TrsdosReader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.False);
  }
}
