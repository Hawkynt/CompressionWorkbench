using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Btrfs;

[TestFixture]
public class BtrfsTests {

  // ── Minimal Btrfs image builder ────────────────────────────────────────

  /// <summary>
  /// Builds a minimal synthetic Btrfs image with a superblock, chunk map (identity),
  /// root tree, and FS tree containing the given files with inline extents.
  /// Uses nodeSize=4096, sectorSize=512, identity logical-to-physical mapping.
  /// </summary>
  private static byte[] BuildMinimalBtrfs(params (string Name, byte[] Data)[] files) {
    const int sbOffset = 0x10000;  // 65536
    const uint nodeSize = 4096;
    const uint sectorSize = 512;

    // Layout:
    // 0x00000 - 0x0FFFF : empty (padding before superblock)
    // 0x10000 - 0x10FFF : superblock (first 4096 bytes, node-sized)
    // 0x11000 - 0x11FFF : chunk tree leaf (identity mapping chunk)
    // 0x12000 - 0x12FFF : root tree leaf (contains ROOT_ITEM for FS tree)
    // 0x13000 - 0x13FFF : FS tree leaf (inodes + dir items + extent data)
    const int chunkTreeOffset = sbOffset + (int)nodeSize;     // 0x11000
    const int rootTreeOffset = chunkTreeOffset + (int)nodeSize; // 0x12000
    const int fsTreeOffset = rootTreeOffset + (int)nodeSize;    // 0x13000
    const int dataStartOffset = fsTreeOffset + (int)nodeSize;   // 0x14000

    // Calculate total image size: need room for file data after FS tree
    var totalDataSize = 0;
    foreach (var (_, data) in files)
      totalDataSize += data.Length;
    // All inline for simplicity in this builder if small, regular extents if larger
    var imageSize = dataStartOffset + Math.Max(totalDataSize + 4096, 4096);
    var img = new byte[imageSize];

    // ── Superblock ──────────────────────────────────────────────────
    var sb = img.AsSpan(sbOffset);

    // Magic at offset 0x40 (64)
    "_BHRfS_M"u8.CopyTo(sb.Slice(0x40));

    // Root tree logical address at offset 0x50 (80)
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x50), rootTreeOffset);
    // Chunk tree logical address at offset 0x58 (88)
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(0x58), chunkTreeOffset);
    // Sector size at offset 0x90 (144)
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x90), sectorSize);
    // Node size at offset 0x94 (148)
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x94), nodeSize);
    // Leaf size at offset 0x98 (152)
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x98), nodeSize);
    // sys_chunk_array size at offset 0xA0 (160) = 0 (we use identity mapping)
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0xA0), 0);

    // ── Chunk tree (identity mapping) ───────────────────────────────
    // Single chunk item mapping [0, imageSize) -> [0, imageSize) (identity)
    WriteNodeHeader(img, chunkTreeOffset, (int)nodeSize, nritems: 1, level: 0, owner: 3);

    // Leaf item 0: key(objectid=256, type=228=CHUNK_ITEM, offset=0)
    var chunkItemOff = chunkTreeOffset + 101;
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(chunkItemOff), 256); // objectid
    img[chunkItemOff + 8] = 228; // type = CHUNK_ITEM
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(chunkItemOff + 9), 0); // offset (logical addr)

    // Chunk data: length(8) + owner(8) + stripe_len(8) + type(8) + io_align(4) +
    //             io_width(4) + sector_size(4) + num_stripes(2) + sub_stripes(2) = 48
    //             + stripe: devid(8) + offset(8) + dev_uuid(16) = 32
    const int chunkDataSize = 48 + 32;
    // data_offset: byte offset from leaf data area start (nodeStart + 101)
    // Place data at end of node: dataPos = nodeEnd - chunkDataSize
    var chunkDataPos = chunkTreeOffset + (int)nodeSize - chunkDataSize;
    var chunkDataOffset = chunkDataPos - chunkTreeOffset - 101;
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(chunkItemOff + 17), (uint)chunkDataOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(chunkItemOff + 21), chunkDataSize);

    // Chunk item data
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(chunkDataPos), imageSize); // length
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(chunkDataPos + 44), 1); // num_stripes
    // Stripe: devid=1, offset=0 (identity)
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(chunkDataPos + 48), 1); // devid
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(chunkDataPos + 56), 0); // physical offset

    // ── Root tree ───────────────────────────────────────────────────
    // Contains one ROOT_ITEM for the FS tree (objectid=5)
    WriteNodeHeader(img, rootTreeOffset, (int)nodeSize, nritems: 1, level: 0, owner: 1);

    var rootItemOff = rootTreeOffset + 101;
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(rootItemOff), 5); // objectid = FS tree
    img[rootItemOff + 8] = 132; // type = ROOT_ITEM
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(rootItemOff + 9), 0); // offset

    // ROOT_ITEM data: inode(160 bytes) + generation(8) + root_dirid(8) + bytenr(8) + ...
    // Minimum we need: 184 bytes to place bytenr at offset 176
    const int rootItemDataSize = 256; // generous size
    var rootDataPos = rootTreeOffset + (int)nodeSize - rootItemDataSize;
    var rootDataOffset = rootDataPos - rootTreeOffset - 101;
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(rootItemOff + 17), (uint)rootDataOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(rootItemOff + 21), rootItemDataSize);

    // bytenr at offset 176 = logical address of FS tree root
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(rootDataPos + 176), fsTreeOffset);

    // ── FS tree ─────────────────────────────────────────────────────
    // Items: INODE_ITEM for root dir (256) + INODE_ITEMs for files +
    //        DIR_INDEX entries + EXTENT_DATA for files

    // Root dir inode (objectid 256)
    var items = new List<(long objId, byte type, long offset, byte[] data)>();

    // Inode item for root dir (objectid 256)
    var rootDirInode = new byte[160];
    // mode at offset 0: directory (S_IFDIR | 0755 = 0x41ED)
    BinaryPrimitives.WriteUInt32LittleEndian(rootDirInode.AsSpan(0), 0x41ED);
    // size at offset 16
    BinaryPrimitives.WriteInt64LittleEndian(rootDirInode.AsSpan(16), 0);
    items.Add((256, 1, 0, rootDirInode)); // INODE_ITEM

    var nextInode = 257L;
    var dataWriteOffset = dataStartOffset;

    for (var fi = 0; fi < files.Length; fi++) {
      var (name, fileData) = files[fi];
      var inode = nextInode++;

      // Inode item for file
      var fileInode = new byte[160];
      // mode: regular file (S_IFREG | 0644 = 0x81A4)
      BinaryPrimitives.WriteUInt32LittleEndian(fileInode.AsSpan(0), 0x81A4);
      BinaryPrimitives.WriteInt64LittleEndian(fileInode.AsSpan(16), fileData.Length);
      items.Add((inode, 1, 0, fileInode)); // INODE_ITEM

      // DIR_INDEX entry (on root dir, objectid=256, type=96)
      var dirIndexData = BuildDirIndexData(inode, name, false);
      items.Add((256, 96, fi + 1, dirIndexData)); // DIR_INDEX

      // EXTENT_DATA
      if (fileData.Length <= 128) {
        // Inline extent
        var extData = new byte[21 + fileData.Length];
        // generation(8)=0, ram_bytes(8)=fileData.Length, compression(1)=0,
        // encryption(1)=0, other_encoding(2)=0, type(1)=0 (inline)
        BinaryPrimitives.WriteInt64LittleEndian(extData.AsSpan(8), fileData.Length);
        extData[20] = 0; // inline
        fileData.CopyTo(extData.AsSpan(21));
        items.Add((inode, 108, 0, extData)); // EXTENT_DATA
      } else {
        // Regular extent — write data at dataWriteOffset
        fileData.CopyTo(img.AsSpan(dataWriteOffset));
        var extData = new byte[53];
        BinaryPrimitives.WriteInt64LittleEndian(extData.AsSpan(8), fileData.Length); // ram_bytes
        extData[20] = 1; // regular extent
        BinaryPrimitives.WriteInt64LittleEndian(extData.AsSpan(21), dataWriteOffset); // disk_bytenr
        BinaryPrimitives.WriteInt64LittleEndian(extData.AsSpan(29), fileData.Length); // disk_num_bytes
        BinaryPrimitives.WriteInt64LittleEndian(extData.AsSpan(37), 0); // offset within extent
        BinaryPrimitives.WriteInt64LittleEndian(extData.AsSpan(45), fileData.Length); // num_bytes
        items.Add((inode, 108, 0, extData)); // EXTENT_DATA
        dataWriteOffset += fileData.Length;
        // Align to sector
        dataWriteOffset = (dataWriteOffset + 511) & ~511;
      }
    }

    // Sort items by (objectid, type, offset) as B-tree requires
    items.Sort((a, b) => {
      var c = a.objId.CompareTo(b.objId);
      if (c != 0) return c;
      c = a.type.CompareTo(b.type);
      if (c != 0) return c;
      return a.offset.CompareTo(b.offset);
    });

    // Write FS tree node
    WriteNodeHeader(img, fsTreeOffset, (int)nodeSize, nritems: items.Count, level: 0, owner: 5);

    // Item data is written from the end of the node backwards
    var fsDataEnd = fsTreeOffset + (int)nodeSize;
    var currentDataEnd = fsDataEnd;

    for (var i = 0; i < items.Count; i++) {
      var (objId, type, offset, data) = items[i];
      currentDataEnd -= data.Length;

      // Write leaf item header (25 bytes each, starting at offset 101)
      var itemHeaderOff = fsTreeOffset + 101 + i * 25;
      BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(itemHeaderOff), objId);
      img[itemHeaderOff + 8] = type;
      BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(itemHeaderOff + 9), offset);

      // data_offset: byte offset from leaf data area start (nodeStart + 101)
      // Reader: dataPos = nodeStart + 101 + dataOffset
      // We want: dataPos = currentDataEnd
      // => dataOffset = currentDataEnd - fsTreeOffset - 101
      var dataOffsetVal = currentDataEnd - fsTreeOffset - 101;
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(itemHeaderOff + 17), (uint)dataOffsetVal);
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(itemHeaderOff + 21), (uint)data.Length);

      // Write item data
      data.CopyTo(img.AsSpan(currentDataEnd));
    }

    return img;
  }

  private static byte[] BuildDirIndexData(long childInode, string name, bool isDir) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var data = new byte[30 + nameBytes.Length];

    // Child key: objectid(8) + type(1) + offset(8)
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(0), childInode);
    data[8] = 1; // INODE_ITEM
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(9), 0);

    // transid(8) at offset 17
    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(17), 1);
    // data_len(2) at offset 25
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(25), 0);
    // name_len(2) at offset 27
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(27), (ushort)nameBytes.Length);
    // type(1) at offset 29: 1=regular, 2=directory
    data[29] = isDir ? (byte)2 : (byte)1;
    // name at offset 30
    nameBytes.CopyTo(data.AsSpan(30));

    return data;
  }

  private static void WriteNodeHeader(byte[] img, int offset, int nodeSize,
      int nritems, int level, int owner) {
    // btrfs_header layout (fs/btrfs/ctree.h):
    //   0 csum[32], 32 fsid[16], 48 bytenr(u64), 56 flags(u64),
    //   64 chunk_tree_uuid[16], 80 generation(u64), 88 owner(u64),
    //   96 nritems(u32), 100 level(u8) — total 101 bytes.
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(offset + 48), offset);
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(offset + 56), 1);       // flags = WRITTEN
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(offset + 80), 1);       // generation
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(offset + 88), owner);   // owner
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(offset + 96), (uint)nritems);
    img[offset + 100] = (byte)level;
  }

  // ── Tests ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Read_SingleFile_InlineExtent() {
    var content = "Hello, Btrfs!"u8.ToArray();
    var img = BuildMinimalBtrfs(("test.txt", content));
    using var ms = new MemoryStream(img);

    var reader = new FileSystem.Btrfs.BtrfsReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(reader.Entries[0].Size, Is.EqualTo(content.Length));
    Assert.That(reader.Entries[0].IsDirectory, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Extract_SingleFile_InlineExtent() {
    var content = "Hello, Btrfs!"u8.ToArray();
    var img = BuildMinimalBtrfs(("test.txt", content));
    using var ms = new MemoryStream(img);

    var reader = new FileSystem.Btrfs.BtrfsReader(ms);
    var extracted = reader.Extract(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Read_MultipleFiles() {
    var file1 = "First"u8.ToArray();
    var file2 = "Second"u8.ToArray();
    var img = BuildMinimalBtrfs(("a.txt", file1), ("b.txt", file2));
    using var ms = new MemoryStream(img);

    var reader = new FileSystem.Btrfs.BtrfsReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    var names = reader.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("a.txt"));
    Assert.That(names, Does.Contain("b.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_RegularExtent() {
    // Larger data to trigger regular (non-inline) extent
    var content = new byte[256];
    Random.Shared.NextBytes(content);
    var img = BuildMinimalBtrfs(("big.bin", content));
    using var ms = new MemoryStream(img);

    var reader = new FileSystem.Btrfs.BtrfsReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    var extracted = reader.Extract(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.Btrfs.BtrfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Btrfs"));
    Assert.That(desc.DisplayName, Is.EqualTo("Btrfs Filesystem Image"));
    Assert.That(desc.Extensions, Does.Contain(".btrfs"));
    Assert.That(desc.Extensions, Does.Contain(".img"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(0x10040));
    Assert.That(desc.MagicSignatures[0].Confidence, Is.EqualTo(0.90));
    Assert.That(desc.MagicSignatures[0].Bytes,
      Is.EqualTo(Encoding.ASCII.GetBytes("_BHRfS_M")));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var content = "data"u8.ToArray();
    var img = BuildMinimalBtrfs(("file.bin", content));
    using var ms = new MemoryStream(img);

    var desc = new FileSystem.Btrfs.BtrfsFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("file.bin"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(content.Length));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Btrfs.BtrfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[0x10000 + 4096];
    // Write wrong magic at superblock + 64
    "XXXXXXXX"u8.CopyTo(data.AsSpan(0x10040));
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Btrfs.BtrfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var img = BuildMinimalBtrfs(("test.txt", "data"u8.ToArray()));
    using var ms = new MemoryStream(img);
    var reader = new FileSystem.Btrfs.BtrfsReader(ms);
    Assert.Throws<ArgumentNullException>(() => reader.Extract(null!));
  }

  [Test, Category("EdgeCase")]
  public void Superblock_Only_NoFiles() {
    // Minimal valid superblock with no entries. The reader requires the
    // image to be large enough to cover sys_chunk_array (0x32B + 4 bytes
    // past the superblock base).
    var img = new byte[0x10000 + 0x32B + 16];
    "_BHRfS_M"u8.CopyTo(img.AsSpan(0x10040));
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x10000 + 0x90), 512);  // sector size
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x10000 + 0x94), 4096); // node size
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x10000 + 0xA0), 0);    // no sys_chunk_array
    // root tree / chunk tree point to invalid locations — should parse with 0 entries
    using var ms = new MemoryStream(img);
    var reader = new FileSystem.Btrfs.BtrfsReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileSystem.Btrfs.BtrfsFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleFile_RoundTrips() {
    var payload = "btrfs inline data"u8.ToArray();
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("test.txt", payload);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.Btrfs.BtrfsReader(ms);
    var entry = r.Entries.FirstOrDefault(e => e.Name == "test.txt");
    Assert.That(entry, Is.Not.Null);
    Assert.That(r.Extract(entry!), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsWriteConstraints() {
    var d = new FileSystem.Btrfs.BtrfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<Compression.Registry.IArchiveWriteConstraints>());
    var c = (Compression.Registry.IArchiveWriteConstraints)d;
    Assert.That(c.AcceptedInputsDescription, Does.Contain("Btrfs"));
    Assert.That(c.CanAccept(new Compression.Registry.ArchiveInputInfo("", "d", true), out _), Is.False);
    Assert.That(c.CanAccept(new Compression.Registry.ArchiveInputInfo("", "f.txt", false), out _), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Writer_SuperblockHasValidCrc32C() {
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("x.txt", "hello"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var image = ms.ToArray();

    const int sbOffset = 0x10000;
    const int sbSize = 4096;

    // Stored CRC is the first 4 bytes of the 32-byte csum field (little-endian).
    var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sbOffset, 4));
    // Bytes [4..32) of the csum field must be zero (unused for CRC32 type).
    for (var i = 4; i < 32; i++)
      Assert.That(image[sbOffset + i], Is.EqualTo(0),
        $"csum field byte {i} must be zero for CRC32 type");

    // CRC-32C covers bytes [sbOffset+32 .. sbOffset+4096).
    var computed = Compression.Core.Checksums.Crc32.Compute(
      image.AsSpan(sbOffset + 32, sbSize - 32),
      Compression.Core.Checksums.Crc32.Castagnoli);
    Assert.That(storedCrc, Is.EqualTo(computed),
      "superblock CRC-32C mismatch — btrfsck will reject");
  }

  [Test, Category("HappyPath")]
  public void Writer_FsTreeLeafHasValidCrc32C() {
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("data.bin", "payload"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var image = ms.ToArray();

    // FS tree leaf block is at 0x50000 (see BtrfsWriter.FsTreeOff) and has
    // block size = NodeSize = 16384.
    const int fsTreeOff = 0x50000;
    const int nodeSize = 16384;

    var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(fsTreeOff, 4));
    for (var i = 4; i < 32; i++)
      Assert.That(image[fsTreeOff + i], Is.EqualTo(0),
        $"csum field byte {i} must be zero");

    var computed = Compression.Core.Checksums.Crc32.Compute(
      image.AsSpan(fsTreeOff + 32, nodeSize - 32),
      Compression.Core.Checksums.Crc32.Castagnoli);
    Assert.That(storedCrc, Is.EqualTo(computed),
      "fs-tree leaf CRC-32C mismatch");
  }

  [Test, Category("HappyPath")]
  public void Writer_RootTreeLeafHasValidCrc32C() {
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("a.txt", "abc"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var image = ms.ToArray();

    const int rootTreeOff = 0x20000;
    const int nodeSize = 16384;

    var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(rootTreeOff, 4));
    var computed = Compression.Core.Checksums.Crc32.Compute(
      image.AsSpan(rootTreeOff + 32, nodeSize - 32),
      Compression.Core.Checksums.Crc32.Castagnoli);
    Assert.That(storedCrc, Is.EqualTo(computed),
      "root-tree leaf CRC-32C mismatch");
  }

  [Test, Category("HappyPath")]
  public void Crc32C_MatchesKnownVector() {
    // Sanity check the CRC-32C (Castagnoli) polynomial against the canonical
    // test vector: CRC32C("123456789") == 0xE3069283.
    var crc = Compression.Core.Checksums.Crc32.Compute(
      "123456789"u8,
      Compression.Core.Checksums.Crc32.Castagnoli);
    Assert.That(crc, Is.EqualTo(0xE3069283u));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "btrfs descriptor"u8.ToArray());
      var d = new FileSystem.Btrfs.BtrfsFormatDescriptor();
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

  // ── Part 2: spec-compliance tests for the real chunk tree ──────────────

  [Test, Category("RealWorld")]
  public void Writer_ChunkTreeExistsAtSpecOffset() {
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("x.txt", "hello"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var image = ms.ToArray();

    // Superblock chunk_root at spec offset 0x58 (88) must be a valid offset,
    // NOT the sentinel 0x7FFFFFFF the old obsolete writer used.
    var chunkRoot = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(0x10000 + 0x58));
    Assert.That(chunkRoot, Is.GreaterThan(0));
    Assert.That(chunkRoot, Is.LessThan(image.Length - 100));
    // Block at that offset must be inside the image and start with a
    // valid node header (nritems > 0 at offset 96 per btrfs_header spec).
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)chunkRoot + 96));
    Assert.That(nritems, Is.GreaterThanOrEqualTo(1), "chunk tree leaf must have ≥1 CHUNK_ITEM");
    Assert.That(image[(int)chunkRoot + 100], Is.EqualTo((byte)0), "chunk tree must be a leaf (level 0)");
  }

  [Test, Category("RealWorld")]
  public void Writer_SysChunkArrayPopulated() {
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("a.txt", "x"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var image = ms.ToArray();

    // sys_chunk_array_size at spec offset 0xA0 (160) per fs/btrfs/ctree.h.
    var size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x10000 + 0xA0));
    Assert.That(size, Is.EqualTo(17 + 48 + 32));
    // First 8 bytes of the array (at spec offset 0x32B) = key.objectid = 256.
    var keyObjId = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(0x10000 + 0x32B));
    Assert.That(keyObjId, Is.EqualTo(256));
    // Key.type = CHUNK_ITEM = 228.
    Assert.That(image[0x10000 + 0x32B + 8], Is.EqualTo((byte)228));
  }

  [Test, Category("RealWorld")]
  public void Writer_CsumTypeIsZero() {
    // Regression: earlier writer clobbered csum_type at 0xC4 with the sys
    // chunk array length (97), causing btrfs check to reject the image with
    // "unsupported checksum algorithm 97". Must stay 0 (CRC-32C).
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("x.txt", "hello"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var image = ms.ToArray();
    var csumType = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x10000 + 0xC4));
    Assert.That(csumType, Is.EqualTo((ushort)0),
      $"csum_type must be 0 (CRC-32C) — got {csumType}");
  }

  [Test, Category("RealWorld")]
  public void Writer_ThreeChunksMapLogicalRanges() {
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("a.txt", "payload"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var image = ms.ToArray();

    // Read chunk tree leaf at chunk_root (spec offset 0x58) and count CHUNK_ITEM entries.
    // The chunk tree also contains one DEV_ITEM (type=216) before the CHUNK_ITEM
    // entries (type=228), matching what mkfs.btrfs emits.
    var chunkRoot = (int)BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(0x10000 + 0x58));
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(chunkRoot + 96));

    var chunkItemCount = 0;
    var seenTypes = new List<ulong>();
    for (var i = 0; i < nritems; i++) {
      var itemOff = chunkRoot + 101 + i * 25;
      var keyType = image[itemOff + 8];
      if (keyType != 228) continue; // 228 = BTRFS_CHUNK_ITEM_KEY
      chunkItemCount++;
      var dataOff = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(itemOff + 17));
      var dataPos = chunkRoot + 101 + (int)dataOff;
      var type = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(dataPos + 24));
      seenTypes.Add(type);
    }
    Assert.That(chunkItemCount, Is.EqualTo(3),
      "chunk tree must have exactly three CHUNK_ITEM entries: SYSTEM, METADATA, DATA");
    Assert.That(seenTypes, Does.Contain(0x02UL), "SYSTEM chunk (0x02) missing");
    Assert.That(seenTypes, Does.Contain(0x04UL), "METADATA chunk (0x04) missing");
    Assert.That(seenTypes, Does.Contain(0x01UL), "DATA chunk (0x01) missing");
  }

  [Test, Category("RealWorld")]
  public void Writer_ReaderUsesRealChunkTree() {
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("a.txt", "hello"u8.ToArray());
    w.AddFile("b.bin", new byte[64]);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.Btrfs.BtrfsReader(ms);
    Assert.That(r.UsedRealChunkTree, Is.True,
      "writer output must populate the chunk map; reader must not fall back to identity");
    Assert.That(r.Entries.Count, Is.EqualTo(2));
  }

  [Test, Category("RealWorld")]
  public void Writer_ChunkTreeLeafHasValidCrc32C() {
    var w = new FileSystem.Btrfs.BtrfsWriter();
    w.AddFile("x", "y"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var image = ms.ToArray();

    var chunkRoot = (int)BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(0x10000 + 0x58));
    const int nodeSize = 16384;
    var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(chunkRoot, 4));
    var computed = Compression.Core.Checksums.Crc32.Compute(
      image.AsSpan(chunkRoot + 32, nodeSize - 32),
      Compression.Core.Checksums.Crc32.Castagnoli);
    Assert.That(storedCrc, Is.EqualTo(computed),
      "chunk-tree leaf CRC-32C mismatch — btrfsck will reject");
  }

  [Test, Category("RoundTrip")]
  public void Writer_ThreeFiles_VariousSizes_RoundTrip() {
    var w = new FileSystem.Btrfs.BtrfsWriter();
    var small = "small"u8.ToArray();
    var mid = new byte[200];
    for (var i = 0; i < mid.Length; i++) mid[i] = (byte)(i % 251);
    var big = new byte[500];
    Random.Shared.NextBytes(big);
    w.AddFile("small.txt", small);
    w.AddFile("mid.bin", mid);
    w.AddFile("big.bin", big);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.Btrfs.BtrfsReader(ms);
    Assert.That(r.Entries.Count, Is.EqualTo(3));
    var byName = r.Entries.ToDictionary(e => e.Name, e => r.Extract(e));
    Assert.That(byName["small.txt"], Is.EqualTo(small));
    Assert.That(byName["mid.bin"], Is.EqualTo(mid));
    Assert.That(byName["big.bin"], Is.EqualTo(big));
  }
}
