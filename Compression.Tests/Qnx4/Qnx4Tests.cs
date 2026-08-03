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

  /// <summary>
  /// A minimal volume built by hand, following the on-disk struct rather than
  /// the one this project used to assume.
  /// </summary>
  /// <remarks>
  /// Block 1 is the superblock: four inode entries, the first of which
  /// describes the root directory. The directory itself lives elsewhere, an
  /// extent's block number counts from one, and the mode and status bytes sit
  /// at 0x32 and 0x3F — not 0x20 and 0x3D, which is where the first timestamp
  /// and a padding byte actually are.
  /// </remarks>
  private static byte[] BuildMinimalQnx4() {
    var image = new byte[32 * BlockSize];
    const uint rootDirBlock = 2;

    // The superblock's first entry: the root directory.
    WriteEntry(image, BlockSize, "/", size: 2 * InodeSize,
      extentBlock: rootDirBlock + 1, extentSize: 1, mode: 0x41ED);

    // The root directory: .bitmap first, because a driver looks for it, then
    // the one file.
    var dir = (int)(rootDirBlock * BlockSize);
    WriteEntry(image, dir, ".bitmap", size: BlockSize,
      extentBlock: 7, extentSize: 1, mode: 0x81A4);

    var content = "Hello QNX4!\n"u8.ToArray();
    WriteEntry(image, dir + InodeSize, "hello", size: (uint)content.Length,
      extentBlock: FileBlock + 1, extentSize: 1, mode: 0x81A4);
    content.CopyTo(image.AsSpan((int)(FileBlock * BlockSize)));

    return image;
  }

  private static void WriteEntry(
      byte[] image, int at, string name, uint size, uint extentBlock, uint extentSize, ushort mode) {
    Encoding.ASCII.GetBytes(name).CopyTo(image.AsSpan(at));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 0x10), size);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 0x14), extentBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 0x18), extentSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at + 0x30), 1);      // di_num_xtnts
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at + 0x32), mode);
    image[at + 0x3F] = 0x01;                                                   // QNX4_FILE_USED
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
    // Blank the name of the superblock's first entry. The root is found by
    // that entry being called "/", so a volume without it is not one.
    img.AsSpan(BlockSize, 16).Clear();
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
