using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Tests.Qnx4;

[TestFixture]
public class Qnx4Tests {

  // Minimal QNX4 image:
  //   Block 0      boot (zeroed)
  //   Block 1..4   root directory cluster — first inode entry points to a file
  //   Block 5      file data
  private const int BlockSize = 512;
  private const int InodeSize = 64;
  private const uint FileBlock = 5;

  private static byte[] BuildMinimalQnx4() {
    var image = new byte[32 * BlockSize];

    // Inode entry #0 in block 1 (offset 512):
    var inodeOff = BlockSize + 0 * InodeSize;
    var name = "hello"u8.ToArray();
    name.CopyTo(image.AsSpan(inodeOff)); // name in first 16 bytes (NUL-padded)
    var content = "Hello QNX4!\n"u8.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(inodeOff + 0x10), (uint)content.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(inodeOff + 0x14), FileBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(inodeOff + 0x18), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(inodeOff + 0x20), 0x81A4); // regular + 0644
    image[inodeOff + 0x3D] = 0x08; // ACTIVE

    // File data at block 5
    content.CopyTo(image.AsSpan((int)(FileBlock * BlockSize)));

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Qnx4.Qnx4FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Qnx4"));
    Assert.That(d.Extensions, Does.Contain(".qnx4"));
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Reader_ReadsSyntheticImage() {
    var img = BuildMinimalQnx4();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Qnx4.Qnx4Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(12));
    var data = r.Extract(r.Entries[0]);
    Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo("Hello QNX4!\n"));
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream() {
    var img = BuildMinimalQnx4();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Qnx4.Qnx4FormatDescriptor();
    using var s = d.OpenEntry(ms, "hello", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(12));
    var buf = new byte[64];
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(12));
    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsCorruptedImage() {
    var img = BuildMinimalQnx4();
    // Strip ACTIVE byte
    img[BlockSize + 0x3D] = 0x00;
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => new FileSystem.Qnx4.Qnx4Reader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalQnx4();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Qnx4.Qnx4FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("hello"));
    ms.Position = 0;
    var bytes = d.ExtractEntryToMemory(ms, "hello", null);
    Assert.That(Encoding.ASCII.GetString(bytes), Is.EqualTo("Hello QNX4!\n"));
  }
}
