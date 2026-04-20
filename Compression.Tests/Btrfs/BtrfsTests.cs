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

    // Magic at offset 64
    "_BHRfS_M"u8.CopyTo(sb.Slice(64));

    // Root tree logical address at offset 80
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(80), rootTreeOffset);
    // Chunk tree logical address at offset 88
    BinaryPrimitives.WriteInt64LittleEndian(sb.Slice(88), chunkTreeOffset);
    // Sector size at offset 128
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(128), sectorSize);
    // Node size at offset 132
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(132), nodeSize);
    // Leaf size at offset 136
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(136), nodeSize);
    // sys_chunk_array size at offset 196 = 0 (we use identity mapping)
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(196), 0);

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
    // Checksum: 32 bytes (zeros for test)
    // FS UUID: 16 bytes (zeros)
    // Bytenr at offset 48
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(offset + 48), offset);
    // Flags at offset 56
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(offset + 56), 1);
    // Magic not in node header (only superblock has it)
    // Generation at offset 72
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(offset + 72), 1);
    // Owner at offset 80
    BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(offset + 80), owner);
    // nritems at offset 88
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(offset + 88), (uint)nritems);
    // Level at offset 92
    img[offset + 92] = (byte)level;
  }

  // ── Tests ──────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Read_SingleFile_InlineExtent() {
    var content = "Hello, Btrfs!"u8.ToArray();
    var img = BuildMinimalBtrfs(("test.txt", content));
    using var ms = new MemoryStream(img);

    var reader = new FileFormat.Btrfs.BtrfsReader(ms);
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

    var reader = new FileFormat.Btrfs.BtrfsReader(ms);
    var extracted = reader.Extract(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Read_MultipleFiles() {
    var file1 = "First"u8.ToArray();
    var file2 = "Second"u8.ToArray();
    var img = BuildMinimalBtrfs(("a.txt", file1), ("b.txt", file2));
    using var ms = new MemoryStream(img);

    var reader = new FileFormat.Btrfs.BtrfsReader(ms);
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

    var reader = new FileFormat.Btrfs.BtrfsReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    var extracted = reader.Extract(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Btrfs.BtrfsFormatDescriptor();
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

    var desc = new FileFormat.Btrfs.BtrfsFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("file.bin"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(content.Length));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Btrfs.BtrfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[0x10000 + 4096];
    // Write wrong magic at superblock + 64
    "XXXXXXXX"u8.CopyTo(data.AsSpan(0x10040));
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Btrfs.BtrfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var img = BuildMinimalBtrfs(("test.txt", "data"u8.ToArray()));
    using var ms = new MemoryStream(img);
    var reader = new FileFormat.Btrfs.BtrfsReader(ms);
    Assert.Throws<ArgumentNullException>(() => reader.Extract(null!));
  }

  [Test, Category("EdgeCase")]
  public void Superblock_Only_NoFiles() {
    // Minimal valid superblock with no entries
    var img = new byte[0x10000 + 4096 * 4];
    "_BHRfS_M"u8.CopyTo(img.AsSpan(0x10040));
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x10000 + 128), 512);  // sector size
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x10000 + 132), 4096); // node size
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(0x10000 + 196), 0);    // no sys_chunk_array
    // root tree / chunk tree point to invalid locations — should parse with 0 entries
    using var ms = new MemoryStream(img);
    var reader = new FileFormat.Btrfs.BtrfsReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Btrfs.BtrfsFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleFile_RoundTrips() {
    var payload = "btrfs inline data"u8.ToArray();
    var w = new FileFormat.Btrfs.BtrfsWriter();
    w.AddFile("test.txt", payload);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileFormat.Btrfs.BtrfsReader(ms);
    var entry = r.Entries.FirstOrDefault(e => e.Name == "test.txt");
    Assert.That(entry, Is.Not.Null);
    Assert.That(r.Extract(entry!), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "btrfs descriptor"u8.ToArray());
      var d = new FileFormat.Btrfs.BtrfsFormatDescriptor();
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
