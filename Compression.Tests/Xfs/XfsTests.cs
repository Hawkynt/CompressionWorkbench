using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Xfs;

[TestFixture]
public class XfsTests {

  private static byte[] BuildMinimalXfs(params (string Name, byte[] Data)[] files) {
    const uint blockSize = 4096;
    const ushort inodeSize = 256;
    const uint agBlocks = 256;
    const int inoPerBlock = (int)(blockSize / inodeSize); // 16
    var agBlkLog = 8; // log2(256)
    var inoPbLog = 4; // log2(16)
    var aginoLog = agBlkLog + inoPbLog; // 12

    // Layout: 1 AG with 256 blocks = 1MB
    var imageSize = (int)(agBlocks * blockSize);
    var img = new byte[imageSize];

    // Superblock at offset 0
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0), 0x58465342); // XFSB
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(4), blockSize);
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(8), agBlocks); // dblocks
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(84), inodeSize);
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(86), inoPerBlock);
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(80), 1); // agcount
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(102), agBlocks);
    img[88] = (byte)agBlkLog;

    // Root inode: AG 0, block 4, inode 0 within block => ino = (0 << aginoLog) | (4 * inoPerBlock + 0)
    var rootIno = (ulong)(4 * inoPerBlock);
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(56), rootIno);

    // Root inode at block 4, offset 0
    var rootOff = 4 * (int)blockSize;
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(rootOff), 0x494E); // IN magic
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(rootOff + 2), 0x41ED); // S_IFDIR | 0755
    img[rootOff + 4] = 2; // version
    img[rootOff + 5] = 1; // format = local (short-form)
    // nextents placeholder at offset 76
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(rootOff + 76), 0);

    // Short-form dir data at rootOff + 176
    var sfOff = rootOff + 176;
    img[sfOff] = (byte)files.Length; // count
    img[sfOff + 1] = 0; // i8count
    // parent = rootIno (4 bytes)
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(sfOff + 2), (uint)rootIno);
    var entryPos = sfOff + 6;

    // Place file inodes starting at block 5
    var nextBlock = 5;
    for (int i = 0; i < files.Length; i++) {
      var (name, data) = files[i];
      var nameBytes = Encoding.UTF8.GetBytes(name);

      // File inode number
      var fileIno = (ulong)(nextBlock * inoPerBlock);

      // Write short-form dir entry: namelen(1) + offset(2) + name + ino(4)
      img[entryPos] = (byte)nameBytes.Length;
      BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(entryPos + 1), (ushort)(i + 3));
      nameBytes.CopyTo(img, entryPos + 3);
      BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(entryPos + 3 + nameBytes.Length), (uint)fileIno);
      entryPos += 3 + nameBytes.Length + 4;

      // Write file inode at block `nextBlock`
      var fInodeOff = nextBlock * (int)blockSize;
      BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(fInodeOff), 0x494E);
      BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(fInodeOff + 2), 0x81A4); // S_IFREG
      img[fInodeOff + 4] = 2; // version

      if (data.Length <= inodeSize - 176) {
        // Inline (local format)
        img[fInodeOff + 5] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(fInodeOff + 8), (ulong)data.Length);
        data.CopyTo(img, fInodeOff + 176);
      } else {
        // Extents format
        img[fInodeOff + 5] = 2;
        BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(fInodeOff + 8), (ulong)data.Length);
        var dataBlock = nextBlock + 1;
        var dataBlocks = (data.Length + (int)blockSize - 1) / (int)blockSize;
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(fInodeOff + 76), 1); // nextents = 1

        // Write extent at fInodeOff + 176 (16 bytes)
        // hi64: flag(1)|startoff(54)|startblock_hi(9)
        // lo64: startblock_lo(43)|blockcount(21)
        ulong startBlock = (ulong)dataBlock;
        ulong hi = (startBlock >> 43) & 0x1FF;
        ulong lo = ((startBlock & 0x7FFFFFFFFFF) << 21) | (uint)dataBlocks;
        BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(fInodeOff + 176), hi);
        BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(fInodeOff + 184), lo);

        // Write data
        data.CopyTo(img, dataBlock * (int)blockSize);
        nextBlock = dataBlock + dataBlocks;
        continue;
      }
      nextBlock++;
    }

    // Set root dir size
    var sfSize = entryPos - sfOff;
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(rootOff + 8), (ulong)sfSize);

    return img;
  }

  [Test, Category("HappyPath")]
  public void Read_SingleFile_Inline() {
    var content = "Hello XFS!"u8.ToArray();
    var img = BuildMinimalXfs(("test.txt", content));
    using var ms = new MemoryStream(img);

    var r = new FileFormat.Xfs.XfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_Inline_Data() {
    var content = "XFS data"u8.ToArray();
    var img = BuildMinimalXfs(("test.txt", content));
    using var ms = new MemoryStream(img);

    var r = new FileFormat.Xfs.XfsReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Read_MultipleFiles() {
    var img = BuildMinimalXfs(("a.txt", "A"u8.ToArray()), ("b.txt", "B"u8.ToArray()));
    using var ms = new MemoryStream(img);
    var r = new FileFormat.Xfs.XfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Xfs.XfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Xfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".xfs"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("XFSB"u8.ToArray()));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Xfs.XfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[4096];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Xfs.XfsReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Xfs.XfsFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleFile_RoundTrips() {
    var payload = new byte[500];
    new Random(42).NextBytes(payload);
    var w = new FileFormat.Xfs.XfsWriter();
    w.AddFile("test.bin", payload);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileFormat.Xfs.XfsReader(ms);
    var entry = r.Entries.FirstOrDefault(e => e.Name == "test.bin");
    Assert.That(entry, Is.Not.Null);
    Assert.That(entry!.Size, Is.EqualTo(payload.Length));
    Assert.That(r.Extract(entry), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "xfs descriptor test"u8.ToArray());
      var d = new FileFormat.Xfs.XfsFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "data.txt", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name), Has.Member("data.txt"));
    } finally {
      File.Delete(tmp);
    }
  }
}
