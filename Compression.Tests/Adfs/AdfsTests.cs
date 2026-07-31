using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Tests.Adfs;

[TestFixture]
public class AdfsTests {

  // Build a minimal ADFS old-map image: 256-byte sectors. Root dir starts at
  // sector 2 (file offset 0x200) and is 1280 bytes. We put a single file entry
  // pointing to a data sector containing "Hello from ADFS\n".
  private const int SectorSize = 256;
  private const int RootDirOffset = 0x200;
  private const int FileSector = 10;

  private static byte[] BuildMinimalAdfs() {
    var image = new byte[64 * SectorSize]; // 16 KiB

    // Directory header: "Hugo" at +0
    Encoding.ASCII.GetBytes("Hugo").CopyTo(image, RootDirOffset + 0);
    // First entry at +5
    var entryOff = RootDirOffset + 5;
    var content = "Hello from ADFS\n"u8.ToArray();

    // Name: "HELLO" (5 chars, no attribute bits set except no D=not dir)
    var nameBytes = "HELLO"u8.ToArray();
    nameBytes.CopyTo(image.AsSpan(entryOff + 0));
    // load/exec ignored
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entryOff + 0x12, 4), (uint)content.Length);
    // Start sector (3 bytes)
    image[entryOff + 0x16] = (byte)(FileSector & 0xFF);
    image[entryOff + 0x17] = (byte)((FileSector >> 8) & 0xFF);
    image[entryOff + 0x18] = (byte)((FileSector >> 16) & 0xFF);

    // End-of-directory sentinel: entry 2's first byte = 0 — already zero.

    // End magic "Hugo" at +0x4CB
    Encoding.ASCII.GetBytes("Hugo").CopyTo(image, RootDirOffset + 0x4CB);

    // File data at FileSector * SectorSize
    content.CopyTo(image.AsSpan(FileSector * SectorSize));

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Adfs.AdfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Adfs"));
    Assert.That(d.Extensions, Does.Contain(".adl"));
    // Four directory markers (old/new map, Hugo/Nick) plus the new-map disc
    // record, which is what identifies a volume whose root has no fixed offset.
    Assert.That(d.MagicSignatures.Count, Is.EqualTo(5));
    // WORM promotion: descriptor now advertises IArchiveCreatable + CanCreate
    // for ADFS-L emission.
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Reader_ReadsSyntheticImage() {
    var img = BuildMinimalAdfs();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Adfs.AdfsReader(ms);
    Assert.That(r.DirectoryMagic, Is.EqualTo("Hugo"));
    Assert.That(r.SectorSize, Is.EqualTo(256));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(16));
    Assert.That(r.Entries[0].IsDirectory, Is.False);
    var data = r.Extract(r.Entries[0]);
    Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo("Hello from ADFS\n"));
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream() {
    var img = BuildMinimalAdfs();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Adfs.AdfsFormatDescriptor();
    using var s = d.OpenEntry(ms, "HELLO", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(16));
    var buf = new byte[64];
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(16));
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsCorruptedImage() {
    var img = BuildMinimalAdfs();
    // Wreck the Hugo marker
    img[RootDirOffset + 0] = (byte)'X';
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => new FileSystem.Adfs.AdfsReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalAdfs();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Adfs.AdfsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("HELLO"));
    ms.Position = 0;
    var bytes = d.ExtractEntryToMemory(ms, "HELLO", null);
    Assert.That(Encoding.ASCII.GetString(bytes), Is.EqualTo("Hello from ADFS\n"));
  }
}
