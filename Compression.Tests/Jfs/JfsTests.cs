using System.Buffers.Binary;

namespace Compression.Tests.Jfs;

[TestFixture]
public class JfsTests {

  private static byte[] BuildMinimalJfs(params (string Name, byte[] Data)[] files) {
    const int blockSize = 4096;
    const int sbOff = 32768;
    const int inodeSize = 512;
    var imageSize = 256 * 1024; // 256KB
    var img = new byte[imageSize];

    // Superblock
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(sbOff), 0x3153464A); // JFS1
    BinaryPrimitives.WriteInt32LittleEndian(img.AsSpan(sbOff + 88), blockSize);

    // Aggregate inode table at block 9 (offset 36864)
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(sbOff + 100), 9);

    // AIT: inode 16 (FILESYSTEM_I) at offset 9*4096 + 16*512
    var fsInoOff = 9 * blockSize + 16 * inodeSize;
    // xtree first extent points to fileset inode table at block 20
    var xtreeOff = fsInoOff + 160;
    if (xtreeOff + 48 < imageSize) {
      img[xtreeOff] = 0; // flag
      img[xtreeOff + 1] = 1; // nextindex
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(xtreeOff + 36), 20); // addr of first extent
    }

    // Fileset inode table at block 20 (offset 81920)
    var fsitOff = 20 * blockSize;

    // Root inode (inode 2) — directory
    var rootInoOff = fsitOff + 2 * inodeSize;
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(rootInoOff), 0x41ED); // S_IFDIR

    // dtree at rootInoOff + 160
    var dtOff = rootInoOff + 160;
    img[dtOff] = 0; // flag
    img[dtOff + 1] = (byte)files.Length; // nextindex

    var nextInode = 3;
    var dataBlock = 30; // file data starts at block 30

    for (int i = 0; i < files.Length && i < 8; i++) {
      var (name, data) = files[i];
      var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
      var ino = nextInode++;

      // stbl entry
      img[dtOff + 8 + i] = (byte)(i + 1); // slot index

      // Slot at dtOff + (i+1)*32
      var slotOff = dtOff + (i + 1) * 32;
      if (slotOff + 32 > imageSize) break;
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(slotOff), (uint)ino);
      img[slotOff + 4] = (byte)nameBytes.Length;
      nameBytes.CopyTo(img, slotOff + 5);

      // File inode
      var fInoOff = fsitOff + ino * inodeSize;
      if (fInoOff + inodeSize > imageSize) break;
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(fInoOff), 0x81A4); // S_IFREG
      BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(fInoOff + 48), data.Length);

      // xtree with one extent pointing to data
      var fXtOff = fInoOff + 160;
      img[fXtOff] = 0;
      img[fXtOff + 1] = 1; // nextindex
      var blocks = Math.Max(1, (data.Length + blockSize - 1) / blockSize);
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(fXtOff + 24 + 8), (uint)blocks);
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(fXtOff + 24 + 12), (uint)dataBlock);

      var dataOff = dataBlock * blockSize;
      if (data.Length > 0 && dataOff + data.Length <= imageSize)
        data.CopyTo(img, dataOff);
      dataBlock += blocks;
    }

    return img;
  }

  [Test, Category("HappyPath")]
  public void Read_SingleFile() {
    var content = "Hello JFS!"u8.ToArray();
    var img = BuildMinimalJfs(("test.txt", content));
    using var ms = new MemoryStream(img);
    var r = new FileFormat.Jfs.JfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_SingleFile() {
    var content = "Hello JFS!"u8.ToArray();
    var img = BuildMinimalJfs(("test.txt", content));
    using var ms = new MemoryStream(img);
    var r = new FileFormat.Jfs.JfsReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Jfs.JfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Jfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".jfs"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("JFS1"u8.ToArray()));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Jfs.JfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[40000];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Jfs.JfsReader(ms));
  }
}
