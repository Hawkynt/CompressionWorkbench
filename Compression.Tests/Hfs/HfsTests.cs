using System.Buffers.Binary;

namespace Compression.Tests.Hfs;

[TestFixture]
public class HfsTests {

  private const int MdbOffset = 1024;
  // hfsutils libhfs hardcodes HFS_BLOCKSZ=512 and validates header-record
  // offsets at exactly 0x00e/0x078/0x0f8/0x1f8 — both B*-trees must use
  // 512-byte nodes, with index nodes added when records overflow one leaf.
  private const int ExtentsNodeSize = 512;
  private const int CatalogNodeSize = 512;

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello HFS!"u8.ToArray();
    var w = new FileSystem.Hfs.HfsWriter();
    w.AddFile("TEST.TXT", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Hfs.HfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEST.TXT"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileSystem.Hfs.HfsWriter();
    w.AddFile("A.TXT", "First"u8.ToArray());
    w.AddFile("B.TXT", "Second"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Hfs.HfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "A.TXT", "B.TXT" }));
    var a = r.Entries.Single(e => e.Name == "A.TXT");
    var b = r.Entries.Single(e => e.Name == "B.TXT");
    Assert.That(r.Extract(a), Is.EqualTo("First"u8.ToArray()));
    Assert.That(r.Extract(b), Is.EqualTo("Second"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_EmptyDisk() {
    var w = new FileSystem.Hfs.HfsWriter();
    var disk = w.Build();
    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Hfs.HfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("RoundTrip")]
  public void Writer_RoundTripsMultipleFiles() {
    var w = new FileSystem.Hfs.HfsWriter();
    var payloads = new Dictionary<string, byte[]> {
      ["ONE.BIN"] = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray(),
      ["TWO.BIN"] = Enumerable.Range(0, 1000).Select(i => (byte)(i * 3)).ToArray(),
      ["THREE.BIN"] = Enumerable.Range(0, 513).Select(i => (byte)(i ^ 0x55)).ToArray(),
      ["FOUR.BIN"] = Enumerable.Range(0, 2048).Select(i => (byte)(i >> 2)).ToArray(),
      ["FIVE.BIN"] = "x"u8.ToArray(),
    };
    foreach (var (n, d) in payloads) w.AddFile(n, d);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileSystem.Hfs.HfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(5));
    foreach (var e in r.Entries) {
      Assert.That(payloads.ContainsKey(e.Name), $"unknown entry {e.Name}");
      Assert.That(r.Extract(e), Is.EqualTo(payloads[e.Name]), $"data mismatch for {e.Name}");
    }
  }

  [Test, Category("HappyPath")]
  public void Writer_MdbFieldsAtSpecOffsets() {
    var w = new FileSystem.Hfs.HfsWriter();
    w.SetVolumeName("MyDisk");
    w.AddFile("F.TXT", "hello"u8.ToArray());
    var disk = w.Build();

    // drSigWord "BD" at MDB+0
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(MdbOffset)), Is.EqualTo(0x4244));
    // drAtrb at MDB+10 — bit 8 (unmounted-cleanly) set
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(MdbOffset + 10)), Is.EqualTo(0x0100));
    // drNmFls at MDB+12 = 1
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(MdbOffset + 12)), Is.EqualTo(1));
    // drVBMSt at MDB+14 = 3
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(MdbOffset + 14)), Is.EqualTo(3));
    // drAlBlkSiz at MDB+20 = 512
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(MdbOffset + 20)), Is.EqualTo(512u));
    // drClpSiz at MDB+24 = 4 × 512
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(MdbOffset + 24)), Is.EqualTo(2048u));
    // drNxtCNID at MDB+30 = 17 (16 + 1 file)
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(MdbOffset + 30)), Is.EqualTo(17u));
    // Volume name Pascal string at MDB+36: length byte then "MyDisk"
    Assert.That(disk[MdbOffset + 36], Is.EqualTo((byte)6));
    Assert.That(System.Text.Encoding.ASCII.GetString(disk, MdbOffset + 37, 6), Is.EqualTo("MyDisk"));
    // drCTFlSize at MDB+146 = 2 catalog nodes × CatalogNodeSize
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(MdbOffset + 146)), Is.EqualTo((uint)(2 * CatalogNodeSize)));
    // drCTExtRec[0] at MDB+150 = catalog start = allocation block 2
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(MdbOffset + 150)), Is.EqualTo(2));
    // drCTExtRec[0].blockCount at MDB+152 = (2 × 4096) / 512 = 16
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(MdbOffset + 152)), Is.EqualTo((ushort)((2 * CatalogNodeSize) / 512)));
  }

  [Test, Category("HappyPath")]
  public void Writer_AlternateMdbIsMirrored() {
    var w = new FileSystem.Hfs.HfsWriter();
    w.AddFile("A.TXT", "abc"u8.ToArray());
    var disk = w.Build();

    // Alternate MDB should be at totalSectors-2 and equal the primary.
    var totalSectors = disk.Length / 512;
    var altOffset = (totalSectors - 2) * 512;
    var primary = disk.AsSpan(MdbOffset, 162).ToArray();
    var alternate = disk.AsSpan(altOffset, 162).ToArray();
    Assert.That(alternate, Is.EqualTo(primary));
  }

  [Test, Category("HappyPath")]
  public void Writer_ExtentsBTreeValidLayout() {
    var w = new FileSystem.Hfs.HfsWriter();
    w.AddFile("X.TXT", "x"u8.ToArray());
    var disk = w.Build();

    // Extents tree lives at alloc block 0. drAlBlSt tells us where alloc space starts.
    var drAlBlSt = BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(MdbOffset + 28));
    var extentsOffset = drAlBlSt * 512;

    // Header node kind at offset 8 should be 1 (header).
    Assert.That((sbyte)disk[extentsOffset + 8], Is.EqualTo((sbyte)1));
    // BTHdrRec at offset 14: treeDepth, rootNode, leafRecords, firstLeaf, lastLeaf
    var hdr = disk.AsSpan(extentsOffset + 14);
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(hdr), Is.EqualTo(1));            // treeDepth
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(hdr[2..]), Is.EqualTo(1u));      // rootNode
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(hdr[6..]), Is.EqualTo(2u));      // leafRecords (extents+catalog)
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(hdr[10..]), Is.EqualTo(1u));     // firstLeaf
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(hdr[14..]), Is.EqualTo(1u));     // lastLeaf
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(hdr[18..]), Is.EqualTo(ExtentsNodeSize)); // nodeSize
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(hdr[20..]), Is.EqualTo(7));      // maxKeyLen
  }

  [Test, Category("HappyPath")]
  public void Writer_CatalogBTreeHasRealRecords() {
    var w = new FileSystem.Hfs.HfsWriter();
    w.AddFile("A.TXT", "1"u8.ToArray());
    w.AddFile("B.TXT", "2"u8.ToArray());
    w.AddFile("C.TXT", "3"u8.ToArray());
    var disk = w.Build();

    var drAlBlSt = BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(MdbOffset + 28));
    var catalogStart = BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(MdbOffset + 150));
    var catalogOffset = drAlBlSt * 512 + catalogStart * 512;

    // Header at node 0.
    Assert.That((sbyte)disk[catalogOffset + 8], Is.EqualTo((sbyte)1));

    // Read bthFNode (first leaf) and walk fLink chain — leaves may span
    // multiple 512-byte nodes once records overflow a single leaf.
    var firstLeaf = (int)BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(catalogOffset + 14 + 10));
    Assert.That(firstLeaf, Is.GreaterThan(0), "expected non-zero first-leaf pointer");

    int folderCount = 0, fileCount = 0, folderThreadCount = 0, fileThreadCount = 0;
    var totalRecords = 0;
    var node = firstLeaf;
    var visited = new HashSet<int>();
    while (node != 0 && visited.Add(node)) {
      var leafOffset = catalogOffset + node * CatalogNodeSize;
      Assert.That((sbyte)disk[leafOffset + 8], Is.EqualTo((sbyte)-1), $"node {node} not a leaf");
      var numRecords = BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(leafOffset + 10));
      totalRecords += numRecords;
      for (var i = 0; i < numRecords; i++) {
        var ptrPos = leafOffset + CatalogNodeSize - 2 * (i + 1);
        var recOffset = BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(ptrPos));
        var recPos = leafOffset + recOffset;
        var keyLen = disk[recPos];
        var dataPos = recPos + 1 + keyLen;
        if ((dataPos & 1) != 0) dataPos++;
        var recType = disk[dataPos];
        switch (recType) {
          case 1: folderCount++; break;
          case 2: fileCount++; break;
          case 3: folderThreadCount++; break;
          case 4: fileThreadCount++; break;
        }
      }
      node = (int)BinaryPrimitives.ReadUInt32BigEndian(disk.AsSpan(leafOffset)); // fLink
    }

    // Expected records: 1 root-dir record + 1 root thread + 3 file records + 3 file threads = 8
    Assert.That(totalRecords, Is.EqualTo(8));
    Assert.That(folderCount, Is.EqualTo(1), "expected 1 folder record (root)");
    Assert.That(folderThreadCount, Is.EqualTo(1), "expected 1 folder-thread record");
    Assert.That(fileCount, Is.EqualTo(3), "expected 3 file records");
    Assert.That(fileThreadCount, Is.EqualTo(3), "expected 3 file-thread records");
  }

  [Test, Category("HappyPath")]
  public void Writer_FileRecordAtSpecSize() {
    // File record data portion must be 102 bytes per Inside Macintosh.
    var w = new FileSystem.Hfs.HfsWriter();
    w.AddFile("AB.TXT", "hi"u8.ToArray());
    var disk = w.Build();

    var drAlBlSt = BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(MdbOffset + 28));
    var catalogStart = BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(MdbOffset + 150));
    var leafOffset = drAlBlSt * 512 + catalogStart * 512 + CatalogNodeSize;
    var numRecords = BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(leafOffset + 10));

    // Walk pointer list and find the file record.
    var pointers = new int[numRecords + 1];
    for (var i = 0; i < numRecords + 1; i++) {
      var ptrPos = leafOffset + CatalogNodeSize - 2 * (i + 1);
      pointers[i] = BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(ptrPos));
    }

    for (var i = 0; i < numRecords; i++) {
      var recPos = leafOffset + pointers[i];
      var endPos = leafOffset + pointers[i + 1];
      var keyLen = disk[recPos];
      var keyTotal = 1 + keyLen;
      if ((keyTotal & 1) != 0) keyTotal++;
      var dataLen = endPos - recPos - keyTotal;
      var dataPos = recPos + keyTotal;
      var recType = disk[dataPos];
      if (recType == 2) {
        Assert.That(dataLen, Is.EqualTo(102), "file record data size must be 102 bytes");
        return;
      }
    }
    Assert.Fail("no file record found in leaf");
  }

  [Test, Category("HappyPath")]
  public void Writer_VolumeBitmapMarksUsedBlocks() {
    var w = new FileSystem.Hfs.HfsWriter();
    w.AddFile("F.TXT", new byte[1024]); // spans 2 alloc blocks
    var disk = w.Build();

    var drVBMSt = BinaryPrimitives.ReadUInt16BigEndian(disk.AsSpan(MdbOffset + 14));
    var bitmapOffset = drVBMSt * 512;
    // With 512-byte B*-tree nodes (hfsutils-compatible layout):
    //   blocks 0..1 = extents tree (header + leaf)
    //   blocks 2..3 = catalog tree (header + 1 leaf — small enough)
    //   blocks 4..5 = our 1024-byte file
    // Total 6 used blocks → byte 0 = 0b11111100 = 0xFC.
    Assert.That(disk[bitmapOffset], Is.EqualTo((byte)0xFC));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.Hfs.HfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Hfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".hfs"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(1024));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Hfs.HfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[2048];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Hfs.HfsReader(ms));
  }
}
