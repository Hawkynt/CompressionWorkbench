using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Pc98;

[TestFixture]
public class Pc98DetectionTests {

  // Build a minimal NEC PC-98 disk image with "NECIPL" at offset 0 and
  // a valid FAT BPB at offset 0x80.
  private static byte[] BuildMinimalImage() {
    const int sectorSize = 512;
    var img = new byte[sectorSize * 32];
    // IPL signature at file offset 0.
    Encoding.ASCII.GetBytes("NECIPL").CopyTo(img.AsSpan(0, 6));

    // FAT BPB at offset 0x80.
    const int bpb = 0x80;
    img[bpb + 0] = 0xEB; img[bpb + 1] = 0x3C; img[bpb + 2] = 0x90;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(bpb + 0x0B, 2), 512); // bytes per sector
    img[bpb + 0x0D] = 1; // sectors per cluster
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(bpb + 0x0E, 2), 1); // reserved sectors
    img[bpb + 0x10] = 2; // FAT count
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(bpb + 0x11, 2), 16); // root entries
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(bpb + 0x13, 2), 32); // total sectors
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(bpb + 0x16, 2), 1);  // sectors per FAT

    // Root directory starts at sector (1+2*1) + 1 IPL block = sector 4 (offset 4*512 = 2048).
    var rootDirOff = 4 * sectorSize;
    Encoding.ASCII.GetBytes("HELLO   ").CopyTo(img.AsSpan(rootDirOff, 8));
    Encoding.ASCII.GetBytes("TXT").CopyTo(img.AsSpan(rootDirOff + 8, 3));
    img[rootDirOff + 0x0B] = 0x20; // archive attribute
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(rootDirOff + 0x1A, 2), 2); // first cluster
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(rootDirOff + 0x1C, 4), 12); // file size
    return img;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Pc98.Pc98FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Pc98"));
    Assert.That(d.DisplayName, Is.EqualTo("NEC PC-98 DOS"));
    Assert.That(d.Extensions, Does.Contain(".hdm"));
    Assert.That(d.Extensions, Does.Contain(".fdi"));
    Assert.That(d.Extensions, Does.Contain(".d88"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("NECIPL"u8.ToArray()));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Detect_NecIplSignature() {
    var img = BuildMinimalImage();
    using var r = new FileSystem.Pc98.Pc98Reader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.True);
    Assert.That(r.SectorsPerCluster, Is.EqualTo(1));
    Assert.That(r.FatCount, Is.EqualTo(2));
    Assert.That(r.RootEntries, Is.EqualTo(16));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO.TXT"));
  }

  [Test, Category("Sad")]
  public void Detect_NotPc98_HasNoValidVolume() {
    var img = new byte[1024];
    img[0] = 0xEB; // IBM PC FAT BPB, no NECIPL signature.
    using var r = new FileSystem.Pc98.Pc98Reader(new MemoryStream(img));
    Assert.That(r.ValidVolume, Is.False);
  }
}
