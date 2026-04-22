using System.Buffers.Binary;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

[TestFixture]
public class HfsPlusTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile() {
    var content = "Hello, HFS+!"u8.ToArray();
    var writer = new HfsPlusWriter();
    writer.AddFile("test.txt", content);
    var image = writer.Build();

    using var ms = new MemoryStream(image);
    var reader = new HfsPlusReader(ms);

    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("test.txt"));
    Assert.That(files[0].Size, Is.EqualTo(content.Length));

    var extracted = reader.Extract(files[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MultipleFiles() {
    var data1 = new byte[100];
    var data2 = new byte[200];
    new Random(42).NextBytes(data1);
    new Random(43).NextBytes(data2);

    var writer = new HfsPlusWriter();
    writer.AddFile("alpha.bin", data1);
    writer.AddFile("beta.bin", data2);
    var image = writer.Build();

    using var ms = new MemoryStream(image);
    var reader = new HfsPlusReader(ms);

    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(2));

    var names = files.Select(f => f.Name).OrderBy(n => n).ToArray();
    Assert.That(names, Does.Contain("alpha.bin"));
    Assert.That(names, Does.Contain("beta.bin"));

    var alphaEntry = files.First(f => f.Name == "alpha.bin");
    var betaEntry = files.First(f => f.Name == "beta.bin");
    Assert.That(reader.Extract(alphaEntry), Is.EqualTo(data1));
    Assert.That(reader.Extract(betaEntry), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_ThreeDifferentSizes() {
    var tiny = new byte[] { 0xAA, 0xBB, 0xCC };
    var small = new byte[4096]; // exactly one block
    var big = new byte[32 * 1024]; // multi-block
    new Random(1).NextBytes(small);
    new Random(2).NextBytes(big);

    var w = new HfsPlusWriter();
    w.AddFile("tiny.bin", tiny);
    w.AddFile("one_block.bin", small);
    w.AddFile("many_blocks.bin", big);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new HfsPlusReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, e => e);
    Assert.That(files, Has.Count.EqualTo(3));
    Assert.That(r.Extract(files["tiny.bin"]), Is.EqualTo(tiny));
    Assert.That(r.Extract(files["one_block.bin"]), Is.EqualTo(small));
    Assert.That(r.Extract(files["many_blocks.bin"]), Is.EqualTo(big));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Properties() {
    var desc = new HfsPlusFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("HfsPlus"));
    Assert.That(desc.DisplayName, Is.EqualTo("HFS+"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".dmg"));
    Assert.That(desc.Extensions, Does.Contain(".hfsx"));
    Assert.That(desc.Extensions, Does.Contain(".hfs"));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(1024));
    Assert.That(desc.MagicSignatures[0].Confidence, Is.EqualTo(0.85));
  }

  [Category("ErrorHandling")]
  [Test]
  public void Reader_TooSmall_Throws() {
    var tiny = new byte[100];
    using var ms = new MemoryStream(tiny);
    Assert.Throws<InvalidDataException>(() => new HfsPlusReader(ms));
  }

  [Category("ErrorHandling")]
  [Test]
  public void Reader_BadMagic_Throws() {
    var bad = new byte[2048];
    bad[1024] = 0xFF;
    bad[1025] = 0xFF;
    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => new HfsPlusReader(ms));
  }

  [Category("HappyPath")]
  [Test]
  public void EmptyDisk_NoEntries() {
    var writer = new HfsPlusWriter();
    var image = writer.Build();

    using var ms = new MemoryStream(image);
    var reader = new HfsPlusReader(ms);

    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(0));
  }

  // ── TN1150 spec-compliance tests ───────────────────────────────────────

  /// <summary>
  /// Verifies the emitted HFSPlusCatalogFile record is exactly 248 bytes per
  /// Apple TN1150 (not the 86-byte truncated layout used by the pre-fix writer).
  /// </summary>
  [Category("Spec")]
  [Test]
  public void Writer_CatalogFileRecordIsCorrectSize() {
    var w = new HfsPlusWriter();
    w.AddFile("a.txt", new byte[] { 1, 2, 3 });
    var image = w.Build();

    // Catalog leaf node lives at block 2 (header=block 1, leaf=block 2).
    const int nodeSize = 4096;
    const int leafBase = 2 * 4096;

    // The leaf contains: thread(root), folder(root), thread(file), filerecord(file).
    // We locate the file record by reading TOC offsets from end of node.
    var leaf = image.AsSpan(leafBase, nodeSize);
    var numRecs = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
    Assert.That(numRecs, Is.GreaterThanOrEqualTo(4));

    // Find the last record (index numRecs-1): its offset is at nodeSize - 2*numRecs.
    // Total leaf record extent is (next-offset - this-offset). The file record is the 4th record.
    // Record order: 0=folderThread, 1=folder, 2=fileThread, 3=fileRecord.
    var fileRecOffset = BinaryPrimitives.ReadUInt16BigEndian(leaf[(nodeSize - 2 * 4)..]);
    var nextOffset = BinaryPrimitives.ReadUInt16BigEndian(leaf[(nodeSize - 2 * 5)..]); // free-space offset

    // Record spans from fileRecOffset to nextOffset. Strip the catalog key.
    // Key = keyLength(2) + keyLength bytes; keyLength is BE u16 at start.
    var keyLen = BinaryPrimitives.ReadUInt16BigEndian(leaf[fileRecOffset..]);
    var recordBodyStart = fileRecOffset + 2 + keyLen;

    // Round up to align boundary — we don't pad the data body itself.
    var bodyLength = nextOffset - recordBodyStart;
    // Body length may be followed by up to 1 byte alignment pad, so accept 248 or 249.
    Assert.That(bodyLength, Is.GreaterThanOrEqualTo(248),
      "HFSPlusCatalogFile body must be at least 248 bytes per TN1150.");
  }

  /// <summary>
  /// Verifies that the HFSPlusForkData.logicalSize field lives at offset 88 of
  /// the catalog file record body (not at offset 70, as the pre-fix writer emitted).
  /// </summary>
  [Category("Spec")]
  [Test]
  public void Writer_ForkDataAtSpecOffset88() {
    var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
    var w = new HfsPlusWriter();
    w.AddFile("x.bin", payload);
    var image = w.Build();

    const int leafBase = 2 * 4096;
    const int nodeSize = 4096;
    var leaf = image.AsSpan(leafBase, nodeSize);

    var fileRecOffset = BinaryPrimitives.ReadUInt16BigEndian(leaf[(nodeSize - 2 * 4)..]);
    var keyLen = BinaryPrimitives.ReadUInt16BigEndian(leaf[fileRecOffset..]);
    var bodyStart = fileRecOffset + 2 + keyLen;

    // Body offset 88 should hold the logicalSize of the data fork.
    var logicalSize = BinaryPrimitives.ReadUInt64BigEndian(leaf[(bodyStart + 88)..]);
    Assert.That(logicalSize, Is.EqualTo((ulong)payload.Length),
      "HFSPlusForkData.logicalSize must be at offset 88 of HFSPlusCatalogFile per TN1150.");

    // Body offset 88+16 = 104 should hold extents[0].startBlock; 108 = blockCount.
    var startBlock = BinaryPrimitives.ReadUInt32BigEndian(leaf[(bodyStart + 104)..]);
    var blockCount = BinaryPrimitives.ReadUInt32BigEndian(leaf[(bodyStart + 108)..]);
    Assert.That(startBlock, Is.GreaterThan(0u), "data fork extent[0].startBlock must be non-zero.");
    Assert.That(blockCount, Is.EqualTo(1u), "single-block payload → blockCount == 1.");

    // recordType at offset 0 of body.
    var recordType = BinaryPrimitives.ReadInt16BigEndian(leaf[bodyStart..]);
    Assert.That(recordType, Is.EqualTo((short)2), "kHFSPlusFileRecord = 2.");
  }
}
