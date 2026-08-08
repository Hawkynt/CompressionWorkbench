namespace Compression.Tests.Ext;

[TestFixture]
public class ExtTests {

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var content = "Hello ext2!"u8.ToArray();
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("test.txt", content);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Ext.ExtReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(content.Length));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "First file content"u8.ToArray();
    var data2 = "Second file content"u8.ToArray();
    var data3 = new byte[100];
    for (var i = 0; i < data3.Length; i++) data3[i] = (byte)(i & 0xFF);

    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("alpha.txt", data1);
    w.AddFile("beta.bin", data2);
    w.AddFile("gamma.dat", data3);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Ext.ExtReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_LargeFile() {
    // Test a file that spans multiple blocks (>1024 bytes with default 1K block size)
    var data = new byte[5000];
    var rng = new Random(42);
    rng.NextBytes(data);

    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("large.bin", data);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Ext.ExtReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(5000));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.Ext.ExtFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Ext"));
    Assert.That(desc.DisplayName, Is.EqualTo("ext2/3/4"));
    Assert.That(desc.Extensions, Does.Contain(".ext2"));
    Assert.That(desc.Extensions, Does.Contain(".ext3"));
    Assert.That(desc.Extensions, Does.Contain(".ext4"));
    Assert.That(desc.Extensions, Does.Contain(".img"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(1080));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileSystem.Ext.ExtFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST.TXT", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally { File.Delete(tmpFile); }
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Ext.ExtReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[2048];
    // Write wrong magic at offset 1080
    data[1080] = 0x00;
    data[1081] = 0x00;
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Ext.ExtReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var w = new FileSystem.Ext.ExtWriter();
    var image = w.Build();
    using var ms = new MemoryStream(image);
    var r = new FileSystem.Ext.ExtReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  // These pin the fsck-critical bookkeeping that the first revision of the writer
  // left at zero. dumpe2fs / e2fsck use these to judge whether a freshly-created
  // image is consistent; at zero, every user-visible cell of accounting is wrong.
  [Test, Category("RealWorld")]
  public void Superblock_FreeCountsMatchContent() {
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("a.txt", new byte[100]);
    w.AddFile("b.txt", new byte[100]);
    var img = w.Build();

    var sb = img.AsSpan(1024);
    var inodesCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sb);
    var blocksCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sb[4..]);
    var freeBlocks = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sb[12..]);
    var freeInodes = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sb[16..]);

    // 10 reserved (inodes 1..10 per EXT2_GOOD_OLD_FIRST_INO), then lost+found and
    // the two files. Reserving 1..10 matches what mkfs.ext4 emits; without it fsck
    // rejects user inodes at 3..10.
    Assert.That(freeInodes, Is.EqualTo(inodesCount - 13));
    // freeBlocks must be strictly between 0 and totalBlocks, never zero for a mostly-empty disk.
    Assert.That(freeBlocks, Is.GreaterThan(0));
    Assert.That(freeBlocks, Is.LessThan(blocksCount));
  }

  [Test, Category("RealWorld")]
  public void Bgd_MatchesSuperblockCounts() {
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("x.txt", new byte[50]);
    var img = w.Build();

    // With 1K blocks, BGD sits in block 2 (firstDataBlock=1 + 1).
    var bgd = img.AsSpan(2 * 1024);
    var bgdFreeBlocks = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bgd[12..]);
    var bgdFreeInodes = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bgd[14..]);
    var usedDirs = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bgd[16..]);

    // One group, so the group's counts are the volume's. How many inodes there are
    // in the first place is a matter of the volume's size, not of what was written
    // into it, so the expectation comes from the superblock.
    var inodesPerGroup = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(1024 + 40));
    Assert.That(bgdFreeBlocks, Is.GreaterThan(0));
    // One group, so the group's free count is the volume's.
    var superblockFreeInodes = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(1024 + 16));
    Assert.That(bgdFreeInodes, Is.EqualTo(superblockFreeInodes));
    Assert.That(usedDirs, Is.EqualTo(2));         // root and lost+found
  }

  // The inode a named entry in the root directory points at.
  private static uint RootEntryInode(byte[] image, string name) {
    var inodeSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(1024 + 88));
    var inodeTableBlock = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(2 * 1024 + 8));
    var rootInode = (int)inodeTableBlock * 1024 + inodeSize;
    var dirBlock = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(rootInode + 40));
    var wanted = System.Text.Encoding.UTF8.GetBytes(name);

    for (var offset = dirBlock * 1024; offset < (dirBlock + 1) * 1024 - 8;) {
      var inode = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset));
      var recLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 4));
      if (recLen == 0) break;

      var nameLen = image[offset + 6];
      if (inode != 0 && nameLen == wanted.Length && image.AsSpan(offset + 8, nameLen).SequenceEqual(wanted))
        return inode;

      offset += recLen;
    }

    throw new AssertionException($"'{name}' is not in the root directory.");
  }

  [Test, Category("RealWorld")]
  public void Inodes_HaveLinksCountAndBlockTally() {
    var w = new FileSystem.Ext.ExtWriter();
    var content = new byte[2500]; // 3 blocks @ 1K
    w.AddFile("multi.bin", content);
    var img = w.Build();

    // Where the inode table starts is the group descriptor's to say — the reserved
    // descriptor blocks a growable volume keeps push it along.
    var inodeTableBlock = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(2 * 1024 + 8));
    var inodeTable = img.AsSpan((int)inodeTableBlock * 1024);
    // Root inode (#2) = index 1, first file inode (#11) = index 10.
    // Inodes 3..10 are reserved per EXT2_GOOD_OLD_FIRST_INO.
    var root = inodeTable.Slice(1 * System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(1024 + 88)), 128);
    // Which inode a file got depends on what the volume reserved ahead of it —
    // lost+found always, the orphan file on an ext4 — so it is looked up by name.
    var inodeSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(1024 + 88));
    var fileInode = RootEntryInode(img, "multi.bin");
    var file = inodeTable.Slice((int)(fileInode - 1) * inodeSize, inodeSize);

    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(root[26..]),
      Is.EqualTo(3), "root dir i_links_count is 2 for itself plus one for lost+found's '..'");
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(file[26..]),
      Is.EqualTo(1), "regular file i_links_count should be 1");
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file[28..]),
      Is.EqualTo(6u), "3 blocks × 2 sectors/block = 6 (i_blocks in 512-byte sectors)");

    // All three timestamps non-zero (we wrote DateTimeOffset.UtcNow).
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file[8..]), Is.GreaterThan(0u));
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file[12..]), Is.GreaterThan(0u));
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file[16..]), Is.GreaterThan(0u));
  }
}
