using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.ReiserFs;

[TestFixture]
public class ReiserFsTests {

  private static byte[] BuildMinimalReiserFs(params (string Name, byte[] Data)[] files) {
    const int blockSize = 4096;
    const int sbOff = 65536;
    var imageSize = 512 * 1024; // 512KB
    var img = new byte[imageSize];

    // Superblock
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(sbOff), (uint)(imageSize / blockSize)); // block_count
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(sbOff + 44), (ushort)blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(sbOff + 20), 18); // root_block = 18
    "ReIsEr3Fs"u8.CopyTo(img.AsSpan(sbOff + 52));

    // Root block (18) = leaf node with directory items
    var rootBlockOff = 18 * blockSize;

    // Block header: level=1 (leaf), nr_items
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(rootBlockOff), 1); // level = leaf

    var nrItems = files.Length > 0 ? 1 + files.Length : 0; // 1 dir item + N direct items
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(rootBlockOff + 2), (ushort)nrItems);

    if (files.Length == 0) return img;

    // Item 0: directory item containing file entries
    // Build directory entry headers + names
    var dirDehData = new List<byte>();
    var nameData = new List<byte>();
    var nameOffsets = new List<int>();

    for (int i = 0; i < files.Length; i++) {
      var nameBytes = Encoding.UTF8.GetBytes(files[i].Name);
      nameOffsets.Add(nameData.Count);
      nameData.AddRange(nameBytes);
      nameData.Add(0); // null terminator
    }

    // DEH entries (16 bytes each)
    for (int i = 0; i < files.Length; i++) {
      var deh = new byte[16];
      BinaryPrimitives.WriteUInt32LittleEndian(deh.AsSpan(0), 0); // offset
      BinaryPrimitives.WriteUInt32LittleEndian(deh.AsSpan(4), 2); // dir_id
      BinaryPrimitives.WriteUInt32LittleEndian(deh.AsSpan(8), (uint)(100 + i)); // objectid
      var nameLocInItem = files.Length * 16 + nameOffsets[i];
      BinaryPrimitives.WriteUInt16LittleEndian(deh.AsSpan(12), (ushort)nameLocInItem);
      BinaryPrimitives.WriteUInt16LittleEndian(deh.AsSpan(14), 4); // state: visible
      dirDehData.AddRange(deh);
    }

    var dirItemData = new byte[dirDehData.Count + nameData.Count];
    dirDehData.ToArray().CopyTo(dirItemData, 0);
    nameData.ToArray().CopyTo(dirItemData, dirDehData.Count);

    // Place directory item data at end of block
    var dirDataOff = rootBlockOff + blockSize - dirItemData.Length;
    dirItemData.CopyTo(img, dirDataOff);

    // Item header 0 (directory): key(16) + count(2) + length(2) + location(2) + version(2)
    var ih0Off = rootBlockOff + 24;
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(ih0Off), 1); // dir_id
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(ih0Off + 4), 2); // object_id (root dir)
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(ih0Off + 16), (ushort)files.Length); // count = num entries
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(ih0Off + 18), (ushort)dirItemData.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(ih0Off + 20), (ushort)(dirDataOff - rootBlockOff)); // location

    // Items 1..N: direct items for each file
    for (int i = 0; i < files.Length; i++) {
      var (_, data) = files[i];
      var ihOff = rootBlockOff + 24 + (i + 1) * 24;
      var dataLocation = dirDataOff - (i + 1) * Math.Max(data.Length, 1);

      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(ihOff), 2); // dir_id
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(ihOff + 4), (uint)(100 + i)); // object_id
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(ihOff + 16), 0xFFFF); // count (direct item indicator)
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(ihOff + 18), (ushort)data.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(ihOff + 20), (ushort)(dataLocation - rootBlockOff));

      if (data.Length > 0 && dataLocation >= rootBlockOff && dataLocation + data.Length <= img.Length)
        data.CopyTo(img, dataLocation);
    }

    return img;
  }

  [Test, Category("HappyPath")]
  public void Read_SingleFile() {
    var img = BuildMinimalReiserFs(("test.txt", "Hello"u8.ToArray()));
    using var ms = new MemoryStream(img);
    var r = new FileFormat.ReiserFs.ReiserFsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_SingleFile() {
    var content = "ReiserFS data"u8.ToArray();
    var img = BuildMinimalReiserFs(("test.txt", content));
    using var ms = new MemoryStream(img);
    var r = new FileFormat.ReiserFs.ReiserFsReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.ReiserFs.ReiserFsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("ReiserFs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".reiserfs"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(3));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.ReiserFs.ReiserFsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[70000];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.ReiserFs.ReiserFsReader(ms));
  }
}
