using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

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

    // Superblock at offset 0 — canonical XFS v4 field offsets.
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(0), 0x58465342);      // sb_magicnum = XFSB
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(4), blockSize);        // sb_blocksize
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(8), agBlocks);         // sb_dblocks
    // sb_rootino at offset 56
    var rootIno = (ulong)(4 * inoPerBlock);
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(56), rootIno);
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(84), agBlocks);        // sb_agblocks
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(88), 1);               // sb_agcount
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(100), 4);              // sb_versionnum = 4 (v4, no CRC)
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(104), inodeSize);      // sb_inodesize
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(106), inoPerBlock);    // sb_inopblock
    img[124] = (byte)agBlkLog;                                              // sb_agblklog

    // Root inode at block 4, offset 0 — v2 dinode (pre-v5 layout): fork at offset 100.
    var rootOff = 4 * (int)blockSize;
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(rootOff), 0x494E);     // IN magic
    BinaryPrimitives.WriteUInt16BigEndian(img.AsSpan(rootOff + 2), 0x41ED); // S_IFDIR | 0755
    img[rootOff + 4] = 2; // di_version = 2
    img[rootOff + 5] = 1; // di_format = local (short-form)
    BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(rootOff + 76), 0);     // di_nextents placeholder

    // Short-form dir data at rootOff + 100 (v2 fork offset).
    var sfOff = rootOff + 100;
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
      img[fInodeOff + 4] = 2; // di_version

      if (data.Length <= inodeSize - 100) {
        // Inline (local format). v2 fork at offset 100.
        img[fInodeOff + 5] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(fInodeOff + 56), (ulong)data.Length); // di_size
        data.CopyTo(img, fInodeOff + 100);
      } else {
        // Extents format. v2 fork at offset 100 (one 16-byte extent fits in 156 bytes of remaining inode).
        img[fInodeOff + 5] = 2;
        BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(fInodeOff + 56), (ulong)data.Length); // di_size
        var dataBlock = nextBlock + 1;
        var dataBlocks = (data.Length + (int)blockSize - 1) / (int)blockSize;
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(fInodeOff + 76), 1); // di_nextents = 1

        // Write extent at fInodeOff + 100 (16 bytes), v2 fork offset.
        ulong startBlock = (ulong)dataBlock;
        ulong hi = (startBlock >> 43) & 0x1FF;
        ulong lo = ((startBlock & 0x7FFFFFFFFFF) << 21) | (uint)dataBlocks;
        BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(fInodeOff + 100), hi);
        BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(fInodeOff + 108), lo);

        // Write data
        data.CopyTo(img, dataBlock * (int)blockSize);
        nextBlock = dataBlock + dataBlocks;
        continue;
      }
      nextBlock++;
    }

    // Set root dir size
    var sfSize = entryPos - sfOff;
    BinaryPrimitives.WriteUInt64BigEndian(img.AsSpan(rootOff + 56), (ulong)sfSize); // di_size

    return img;
  }

  [Test, Category("HappyPath")]
  public void Read_SingleFile_Inline() {
    var content = "Hello XFS!"u8.ToArray();
    var img = BuildMinimalXfs(("test.txt", content));
    using var ms = new MemoryStream(img);

    var r = new FileSystem.Xfs.XfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_Inline_Data() {
    var content = "XFS data"u8.ToArray();
    var img = BuildMinimalXfs(("test.txt", content));
    using var ms = new MemoryStream(img);

    var r = new FileSystem.Xfs.XfsReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Read_MultipleFiles() {
    var img = BuildMinimalXfs(("a.txt", "A"u8.ToArray()), ("b.txt", "B"u8.ToArray()));
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Xfs.XfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.Xfs.XfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Xfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".xfs"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("XFSB"u8.ToArray()));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Xfs.XfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[4096];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Xfs.XfsReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileSystem.Xfs.XfsFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<Compression.Registry.IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<Compression.Registry.IArchiveWriteConstraints>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_WriteConstraints_Expose16MiBFloor() {
    var d = new FileSystem.Xfs.XfsFormatDescriptor();
    var c = (Compression.Registry.IArchiveWriteConstraints)d;
    Assert.That(c.MaxTotalArchiveSize, Is.Null, "XFS has no inherent ceiling.");
    Assert.That(c.MinTotalArchiveSize, Is.EqualTo(16L * 1024 * 1024));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleFile_RoundTrips() {
    var payload = new byte[500];
    new Random(42).NextBytes(payload);
    var w = new FileSystem.Xfs.XfsWriter();
    w.AddFile("test.bin", payload);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.Xfs.XfsReader(ms);
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
      var d = new FileSystem.Xfs.XfsFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
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

  // ───────────────────────── v5 CRC-32C tests ─────────────────────────

  [Test, Category("HappyPath")]
  public void Crc32C_StandardVector_IsE3069283() {
    // CRC-32C of ASCII "123456789" must equal 0xE3069283 (Castagnoli spec).
    Assert.That(Crc32.Compute("123456789"u8, Crc32.Castagnoli), Is.EqualTo(0xE3069283u));
  }

  [Test, Category("HappyPath")]
  public void Writer_SuperblockHasValidCrc32C() {
    var w = new FileSystem.Xfs.XfsWriter();
    w.AddFile("meta.txt", "hello"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var image = ms.ToArray();

    // v5 superblock: the low nibble of sb_versionnum (offset 100, big-endian)
    // is the VERSION_NUM field per XFS_SB_VERSION_NUMBITS = 0xF. The upper
    // 12 bits hold feature flags; mkfs.xfs emits 0xB4A5 for a plain v5 fs.
    var versionnum = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(100));
    Assert.That(versionnum & 0xF, Is.EqualTo(5), "writer must emit v5 superblock");

    // Superblock is hashed over the first sector (512 bytes) with sb_crc (offset 224) zeroed.
    const int sectorSize = 512;
    const int sbCrcOffset = 224;
    var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sbCrcOffset));
    Assert.That(storedCrc, Is.Not.EqualTo(0u), "sb_crc must be populated");

    var sector = image.AsSpan(0, sectorSize).ToArray();
    sector[sbCrcOffset] = 0;
    sector[sbCrcOffset + 1] = 0;
    sector[sbCrcOffset + 2] = 0;
    sector[sbCrcOffset + 3] = 0;
    var recomputed = Crc32.Compute(sector, Crc32.Castagnoli);
    Assert.That(recomputed, Is.EqualTo(storedCrc), "sb_crc does not match CRC-32C of superblock with crc-field zeroed");
  }

  [Test, Category("HappyPath")]
  public void Writer_InodeHasValidCrc32C() {
    var w = new FileSystem.Xfs.XfsWriter();
    w.AddFile("data.bin", new byte[32]);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var image = ms.ToArray();

    // xfs_repair requires the root-inode chunk to sit at the agbno computed from
    // XFS_PREALLOC_BLOCKS, which for our 4 KiB/256 B geometry is agbno 72. That
    // puts rootino at inode number 72×16 = 1152 and the dinode at byte offset
    // 72 × 4096 = 294 912. Slots 1 and 2 are sb_rbmino / sb_rsumino; user
    // files occupy slots 3+.
    const int inodeChunkBlock = 72;
    const int blockSize = 4096;
    const int inodeSize = 256;
    const int diCrcOffset = 100;
    var ioff = inodeChunkBlock * blockSize;

    // di_version must be 3 for v5-capable CRCed inodes.
    Assert.That(image[ioff + 4], Is.EqualTo(3), "root inode di_version must be 3 on v5 images");

    var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(ioff + diCrcOffset));
    Assert.That(storedCrc, Is.Not.EqualTo(0u), "di_crc must be populated");

    var inode = image.AsSpan(ioff, inodeSize).ToArray();
    inode[diCrcOffset] = 0;
    inode[diCrcOffset + 1] = 0;
    inode[diCrcOffset + 2] = 0;
    inode[diCrcOffset + 3] = 0;
    var recomputed = Crc32.Compute(inode, Crc32.Castagnoli);
    Assert.That(recomputed, Is.EqualTo(storedCrc), "di_crc does not match CRC-32C of inode with crc-field zeroed");

    // The first user-file inode lives in slot 3 (slots 0=root, 1=rbmino, 2=rsumino).
    const int firstFileSlot = 3;
    var fileIoff = ioff + firstFileSlot * inodeSize;
    Assert.That(image[fileIoff + 4], Is.EqualTo(3));
    var fileStoredCrc = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fileIoff + diCrcOffset));
    Assert.That(fileStoredCrc, Is.Not.EqualTo(0u));

    var fileInode = image.AsSpan(fileIoff, inodeSize).ToArray();
    fileInode[diCrcOffset] = 0;
    fileInode[diCrcOffset + 1] = 0;
    fileInode[diCrcOffset + 2] = 0;
    fileInode[diCrcOffset + 3] = 0;
    var fileRecomputed = Crc32.Compute(fileInode, Crc32.Castagnoli);
    Assert.That(fileRecomputed, Is.EqualTo(fileStoredCrc));
  }
}
