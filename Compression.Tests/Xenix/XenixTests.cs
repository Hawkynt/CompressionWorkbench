using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Tests.Xenix;

[TestFixture]
public class XenixTests {

  // Minimal Xenix V image — same shape as our SysV synthetic. 1024-byte
  // blocks, magic 0xFD187E20, type=2.
  private static byte[] BuildMinimalXenix() {
    var image = new byte[8 * 1024];
    var sb = 1024;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 504, 4), 0xFD187E20);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 508, 4), 2);

    var ilist = 2 * 1024;
    var ino2 = ilist + (2 - 1) * 64;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ino2 + 0, 2), 0x41ED);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ino2 + 8, 4), 48);
    Write24(image.AsSpan(ino2 + 12), 3);

    var content = "Xenix from Microsoft '83"u8.ToArray();
    var ino3 = ilist + (3 - 1) * 64;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ino3 + 0, 2), 0x81A4);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ino3 + 8, 4), (uint)content.Length);
    Write24(image.AsSpan(ino3 + 12), 4);

    var rootDir = 3 * 1024;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 0, 2), 2);
    image[rootDir + 2] = (byte)'.';
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 16, 2), 2);
    image[rootDir + 18] = (byte)'.';
    image[rootDir + 19] = (byte)'.';
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rootDir + 32, 2), 3);
    Encoding.ASCII.GetBytes("notice").CopyTo(image.AsSpan(rootDir + 34, 14));

    content.CopyTo(image.AsSpan(4 * 1024));
    return image;
  }

  private static void Write24(Span<byte> dest, uint val) {
    dest[0] = (byte)(val & 0xFF);
    dest[1] = (byte)((val >> 8) & 0xFF);
    dest[2] = (byte)((val >> 16) & 0xFF);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Xenix.XenixFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Xenix"));
    Assert.That(d.Extensions, Does.Contain(".xnx"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(1528));
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Reader_ReadsSyntheticImage() {
    var img = BuildMinimalXenix();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Xenix.XenixReader(ms);
    Assert.That(r.Magic, Is.EqualTo(0xFD187E20u));
    Assert.That(r.BlockSize, Is.EqualTo(1024));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("notice"));
    var data = r.Extract(r.Entries[0]);
    Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo("Xenix from Microsoft '83"));
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream() {
    var img = BuildMinimalXenix();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Xenix.XenixFormatDescriptor();
    using var s = d.OpenEntry(ms, "notice", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(24));
    var buf = new byte[64];
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(24));
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsCorruptedImage() {
    var img = BuildMinimalXenix();
    img[1528] ^= 0xFF;
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => new FileSystem.Xenix.XenixReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalXenix();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Xenix.XenixFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("notice"));
    ms.Position = 0;
    var bytes = d.ExtractEntryToMemory(ms, "notice", null);
    Assert.That(Encoding.ASCII.GetString(bytes), Is.EqualTo("Xenix from Microsoft '83"));
  }
}
