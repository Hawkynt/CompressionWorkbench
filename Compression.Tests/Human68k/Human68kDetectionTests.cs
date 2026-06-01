using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Human68k;

[TestFixture]
public class Human68kDetectionTests {

  // Build a minimal Human68k FAT image with the "X68K" tag at offset 0x10
  // and one root-directory entry.
  private static byte[] BuildMinimalImage() {
    const int sectorSize = 512;
    var img = new byte[sectorSize * 64]; // small disk

    img[0] = 0x60; // BSR jump (Human68k 68k boot)
    img[1] = 0x00; img[2] = 0x00;
    // OEM name (0x03..0x0A).
    Encoding.ASCII.GetBytes("HUMAN68K").CopyTo(img.AsSpan(3, 8));
    // BPB at offset 0x0B.
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0B, 2), 512); // bytes/sector
    img[0x0D] = 1; // sectors per cluster
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x0E, 2), 1); // reserved sectors
    // "X68K" tag at offset 0x10.
    Encoding.ASCII.GetBytes("X68K").CopyTo(img.AsSpan(0x10, 4));
    img[0x14] = 2; // FAT count
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x15, 2), 16); // root entries
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x17, 2), 64); // total sectors (small)
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(0x1A, 2), 1);  // sectors per FAT

    // Root directory immediately after reserved + FATs:
    // reserved=1, FATs=2*1=2, so root starts at sector 3.
    var rootDirOff = 3 * sectorSize;
    Encoding.ASCII.GetBytes("HELLO   ").CopyTo(img.AsSpan(rootDirOff, 8));
    Encoding.ASCII.GetBytes("TXT").CopyTo(img.AsSpan(rootDirOff + 8, 3));
    img[rootDirOff + 0x0B] = 0x20; // archive attribute
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(rootDirOff + 0x1A, 2), 2); // first cluster
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(rootDirOff + 0x1C, 4), 11); // file size
    return img;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Human68k.Human68kFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Human68k"));
    Assert.That(d.DisplayName, Is.EqualTo("Sharp X68000 Human68k"));
    // .hdf is intentionally NOT claimed — it collides with HDF4 scientific
    // data, which is the far more common modern use of the extension.
    Assert.That(d.Extensions, Does.Not.Contain(".hdf"));
    Assert.That(d.Extensions, Does.Contain(".dim"));
    Assert.That(d.Extensions, Does.Contain(".2hd"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("X68K"u8.ToArray()));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0x10));
  }

  [Test, Category("HappyPath")]
  public void Detect_X68kTag() {
    var img = BuildMinimalImage();
    using var r = new FileSystem.Human68k.Human68kReader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.True);
    Assert.That(r.SectorsPerCluster, Is.EqualTo(1));
    Assert.That(r.FatCount, Is.EqualTo(2));
    Assert.That(r.RootEntries, Is.EqualTo(16));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO.TXT"));
  }

  [Test, Category("Sad")]
  public void Detect_NotHuman68k_HasNoValidVolume() {
    var img = new byte[1024];
    img[0] = 0xEB; // IBM PC FAT-style boot; no X68K tag.
    using var r = new FileSystem.Human68k.Human68kReader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.False);
  }
}
