using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Tests.Ods1;

[TestFixture]
public class Ods1Tests {

  // Minimal ODS-1 image:
  //   LBN 0       boot
  //   LBN 1       home block — DECFILE11A at +0x1F0, INDEXF.SYS LBN at +0x040
  //   LBN 4       file header for our test file
  //   LBN 10      file data
  private const int LbnSize = 512;
  private const int HomeLbn = 1;
  private const uint IndexfLbn = 4;
  private const uint FileLbn = 10;

  private static byte[] BuildMinimalOds1() {
    var image = new byte[32 * LbnSize];

    // Home block
    var hb = HomeLbn * LbnSize;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(hb + 0x00C, 2), 0x0101); // structure level
    Encoding.ASCII.GetBytes("TESTVOL".PadRight(12, '\0')).CopyTo(image.AsSpan(hb + 0x00E));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(hb + 0x040, 2), (ushort)IndexfLbn);
    Encoding.ASCII.GetBytes("DECFILE11A\0\0").CopyTo(image.AsSpan(hb + 0x1F0));

    // File header at LBN 4. Layout:
    //   +0  idOffWords  → 32 means 64 bytes from start
    //   +1  mpOffWords  → 64 means 128 bytes from start
    //   +2  fileNum     1 (active)
    //   +0x0A fileChar  0 (not a directory)
    //   +64  name(9) + ext(3) + version(2)
    //   +128 map: count_minus_1(2) + hi(2) + lo(2)
    var fh = (int)(IndexfLbn * LbnSize);
    image[fh + 0] = 32; // ident offset in words → 64 bytes
    image[fh + 1] = 64; // map offset in words → 128 bytes
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(fh + 2, 2), 1);
    image[fh + 0x0A] = 0x00; // regular file
    // Ident area at +64
    Encoding.ASCII.GetBytes("HELLO".PadRight(9, '\0')).CopyTo(image.AsSpan(fh + 64));
    Encoding.ASCII.GetBytes("TXT").CopyTo(image.AsSpan(fh + 64 + 9));
    // Map area at +128 — single retrieval pointer: count=0 (=> 1 block), lbn=FileLbn
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(fh + 128, 2), 0); // count - 1 = 0 → 1 block
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(fh + 128 + 2, 2), (ushort)(FileLbn >> 16));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(fh + 128 + 4, 2), (ushort)(FileLbn & 0xFFFF));

    // File data at LBN 10 — fill with "Hello ODS-1!\n" repeated; we only
    // declare 512 bytes worth in size, so truncated extraction shows the
    // first block.
    var content = "Hello ODS-1!\n"u8.ToArray();
    content.CopyTo(image.AsSpan((int)(FileLbn * LbnSize)));

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Ods1.Ods1FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ods1"));
    Assert.That(d.Extensions, Does.Contain(".ods1"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0x3F0));
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That((d.Capabilities & FormatCapabilities.CanCreate) != 0, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Reader_ReadsSyntheticImage() {
    var img = BuildMinimalOds1();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Ods1.Ods1Reader(ms);
    Assert.That(r.VolumeFormat, Does.StartWith("DECFILE11A"));
    Assert.That(r.VolumeName, Is.EqualTo("TESTVOL"));
    Assert.That(r.Entries, Is.Not.Empty);
    var entry = r.Entries.FirstOrDefault(e => e.Name == "HELLO.TXT");
    Assert.That(entry, Is.Not.Null);
    var data = r.Extract(entry!);
    // First 13 bytes of the 512-byte block should be our content.
    var first13 = Encoding.ASCII.GetString(data, 0, 13);
    Assert.That(first13, Is.EqualTo("Hello ODS-1!\n"));
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream() {
    var img = BuildMinimalOds1();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Ods1.Ods1FormatDescriptor();
    using var s = d.OpenEntry(ms, "HELLO.TXT", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(LbnSize)); // 1 block declared
    var buf = new byte[LbnSize + 100];
    var n = s.Read(buf, 0, buf.Length);
    Assert.That(n, Is.EqualTo(LbnSize));
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsCorruptedImage() {
    var img = BuildMinimalOds1();
    // Wreck the DECFILE11A signature
    img[HomeLbn * LbnSize + 0x1F0] = (byte)'X';
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => new FileSystem.Ods1.Ods1Reader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalOds1();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Ods1.Ods1FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Is.Not.Empty);
    Assert.That(entries.Any(e => e.Name == "HELLO.TXT"), Is.True);
  }
}
