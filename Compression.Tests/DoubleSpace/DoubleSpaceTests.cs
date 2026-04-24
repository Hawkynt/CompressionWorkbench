using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.DoubleSpace;

[TestFixture]
public class DoubleSpaceTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello DoubleSpace!"u8.ToArray();
    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter();
    w.AddFile("TEST.TXT", data);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileSystem.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEST.TXT"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter();
    w.AddFile("A.TXT", "First file"u8.ToArray());
    w.AddFile("B.TXT", "Second file"u8.ToArray());
    w.AddFile("C.BIN", new byte[200]);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileSystem.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("First file"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("Second file"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(new byte[200]));
  }

  [Test, Category("HappyPath")]
  public void DsCompression_RoundTrip() {
    var original = "The quick brown fox jumps over the lazy dog. The quick brown fox."u8.ToArray();
    var compressed = FileSystem.DoubleSpace.DsCompression.Compress(original);
    var decompressed = FileSystem.DoubleSpace.DsCompression.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void DsCompression_RoundTrip_AllZeros() {
    var original = new byte[512];
    var compressed = FileSystem.DoubleSpace.DsCompression.Compress(original);
    var decompressed = FileSystem.DoubleSpace.DsCompression.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void DsCompression_RoundTrip_Random() {
    var original = new byte[512];
    new Random(42).NextBytes(original);
    var compressed = FileSystem.DoubleSpace.DsCompression.Compress(original);
    var decompressed = FileSystem.DoubleSpace.DsCompression.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void DoubleSpace_Descriptor_Properties() {
    var desc = new FileSystem.DoubleSpace.DoubleSpaceFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("DoubleSpace"));
    Assert.That(desc.DisplayName, Is.EqualTo("DoubleSpace CVF"));
    Assert.That(desc.Extensions, Does.Contain(".cvf"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(3));
    Assert.That(desc.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
  }

  [Test, Category("HappyPath")]
  public void DriveSpace_Descriptor_Properties() {
    var desc = new FileSystem.DoubleSpace.DriveSpaceFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("DriveSpace"));
    Assert.That(desc.DisplayName, Is.EqualTo("DriveSpace CVF"));
    Assert.That(desc.Extensions, Does.Contain(".cvf"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.Description, Does.Contain("DriveSpace"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.DoubleSpace.DoubleSpaceReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[1024];
    data[0] = 0xEB; data[1] = 0x3C; data[2] = 0x90;
    System.Text.Encoding.ASCII.GetBytes("BADMAGIC").CopyTo(data, 3);
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.DoubleSpace.DoubleSpaceReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter();
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileSystem.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ViaInterface() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileSystem.DoubleSpace.DoubleSpaceFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST.TXT", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmpFile);
    }
  }

  [Test, Category("RoundTrip")]
  public void DriveSpace_RoundTrip() {
    var data = "DriveSpace test data"u8.ToArray();
    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter { DriveSpace = true };
    w.AddFile("DS.TXT", data);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileSystem.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.IsDriveSpace, Is.True);
    Assert.That(r.Signature, Is.EqualTo("MSDSP6.2"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void DoubleSpace_Signature() {
    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter { DriveSpace = false };
    w.AddFile("X.TXT", new byte[1]);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileSystem.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.IsDriveSpace, Is.False);
    Assert.That(r.Signature, Is.EqualTo("MSDSP6.0"));
  }

  // =========================================================================
  //                       New spec-compliance tests
  // =========================================================================

  [Test, Category("Spec")]
  public void Writer_MdbpbHasDoubleSpaceSignature() {
    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter();
    w.AddFile("X.TXT", "hi"u8.ToArray());
    var cvf = w.Build();
    // CvfSignature at offset 36..39 should be "DBLS" for DoubleSpace.
    var sig = Encoding.ASCII.GetString(cvf, 36, 4);
    Assert.That(sig, Is.EqualTo("DBLS"));
    // And "DVRS" for DriveSpace.
    var w2 = new FileSystem.DoubleSpace.DoubleSpaceWriter { DriveSpace = true };
    w2.AddFile("X.TXT", "hi"u8.ToArray());
    var cvf2 = w2.Build();
    Assert.That(Encoding.ASCII.GetString(cvf2, 36, 4), Is.EqualTo("DVRS"));
  }

  [Test, Category("Spec")]
  public void Writer_MdfatAtSpecOffset() {
    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter();
    w.AddFile("A.TXT", new byte[100]);
    var cvf = w.Build();
    var mdfatStart = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(44));
    var mdfatLen = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(48));
    var bitFatStart = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(52));
    var bitFatLen = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(56));
    var dataStart = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(60));
    // Non-overlapping, in-order, inside the image.
    Assert.That(mdfatStart, Is.GreaterThan(0u));
    Assert.That(mdfatLen, Is.GreaterThan(0u));
    Assert.That(bitFatStart, Is.GreaterThanOrEqualTo(mdfatStart + mdfatLen));
    Assert.That(bitFatLen, Is.GreaterThan(0u));
    Assert.That(dataStart, Is.GreaterThanOrEqualTo(bitFatStart + bitFatLen));
    Assert.That((long)dataStart * 512, Is.LessThan(cvf.Length));
    // Entry for the file's first cluster (cluster 2) has flags=1 (stored)
    // or flags=2 (compressed) — either is valid; the writer picks whichever
    // produces the smaller physical run.
    var entry = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan((int)(mdfatStart * 512 + 2 * 4)));
    var runSectors = (entry >> 21) & 0x7Fu;
    var flags = (entry >> 28) & 0xFu;
    Assert.That(flags, Is.AnyOf(1u, 2u), "cluster 2 should be stored or compressed");
    Assert.That(runSectors, Is.GreaterThan(0u));
  }

  [Test, Category("Spec")]
  public void Writer_BitFatTracksUsedSectors() {
    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter();
    w.AddFile("A.TXT", new byte[100]);
    w.AddFile("B.TXT", new byte[100]);
    var cvf = w.Build();
    var bitFatStart = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(52));
    var bitFatLen = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(56));
    Assert.That(bitFatLen, Is.GreaterThan(0u));
    // First bit of the BitFAT must be set (the first 8 KB of the DATA area
    // holds our stored runs for cluster 2, so region 0 is in use).
    var firstByte = cvf[(int)(bitFatStart * 512)];
    Assert.That(firstByte & 1, Is.EqualTo(1), "region 0 must be marked used");
    // Check cross-consistency: every MDFAT entry with flags!=0 must have
    // its region bit set in the BitFAT.
    var mdfatStart = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(44));
    var mdfatLen = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(48));
    var entries = (int)(mdfatLen * 512 / 4);
    for (var i = 0; i < entries; i++) {
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan((int)(mdfatStart * 512 + i * 4)));
      var flags = (entry >> 28) & 0xFu;
      if (flags == 0) continue;
      var physSector = (int)(entry & 0x1FFFFFu);
      var runSectors = (int)((entry >> 21) & 0x7Fu);
      var firstRegion = physSector * 512 / 8192;
      var lastRegion = (physSector * 512 + runSectors * 512 - 1) / 8192;
      for (var r = firstRegion; r <= lastRegion; r++) {
        var bytePos = (int)(bitFatStart * 512) + r / 8;
        Assert.That((cvf[bytePos] & (1 << (r & 7))) != 0, Is.True,
          $"BitFAT region {r} must be marked used for MDFAT entry {i}");
      }
    }
  }

  [Test, Category("RoundTrip")]
  public void Writer_VfatLfnRoundTrip() {
    var data = "Long filename content"u8.ToArray();
    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter();
    w.AddFile("SomeLongNameWithSpaces.txt", data);
    var cvf = w.Build();
    using var ms = new MemoryStream(cvf);
    var r = new FileSystem.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("SomeLongNameWithSpaces.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Writer_StoredRunUncompressedRoundTrip() {
    var data = new byte[10 * 1024];
    new Random(1234).NextBytes(data);
    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter();
    w.AddFile("RANDOM.BIN", data);
    var cvf = w.Build();
    using var ms = new MemoryStream(cvf);
    var r = new FileSystem.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void Writer_DriveSpace30_Variant() {
    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter {
      Variant = FileSystem.DoubleSpace.CvfVariant.DriveSpace30
    };
    w.AddFile("X.TXT", "hi"u8.ToArray());
    var cvf = w.Build();
    Assert.That(Encoding.ASCII.GetString(cvf, 3, 8), Is.EqualTo("DRVSPACE"));
    Assert.That(Encoding.ASCII.GetString(cvf, 36, 4), Is.EqualTo("DVRS"));
    using var ms = new MemoryStream(cvf);
    var r = new FileSystem.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.Signature, Is.EqualTo("DRVSPACE"));
    Assert.That(r.IsDriveSpace, Is.True);
  }

  [Test, Category("Spec")]
  public void Writer_BootSignature_55AA() {
    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter();
    var cvf = w.Build();
    Assert.That(cvf[510], Is.EqualTo((byte)0x55));
    Assert.That(cvf[511], Is.EqualTo((byte)0xAA));
  }

  // =========================================================================
  //                     Phase-level compressed-cluster tests
  // =========================================================================

  [Test, Category("RoundTrip")]
  public void Writer_CompressedCluster_RoundTripThroughReader() {
    // Highly compressible payload → writer must opt-in compressed run
    // (MDFAT flag = 2) and reader must round-trip byte-exactly.
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "The quick brown fox jumps over the lazy dog. ", 500)));

    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter();
    w.AddFile("BIG.TXT", text);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileSystem.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(text));

    // Verify at least one cluster was actually emitted as compressed.
    var mdfatStart = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(44));
    var mdfatLen = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(48));
    var entries = (int)(mdfatLen * 512 / 4);
    var seenCompressedFlag = false;
    for (var i = 0; i < entries; i++) {
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan((int)(mdfatStart * 512 + i * 4)));
      if (((entry >> 28) & 0xFu) == 2u) { seenCompressedFlag = true; break; }
    }
    Assert.That(seenCompressedFlag, Is.True,
      "highly compressible content must produce at least one MDFAT entry with flags=2 (compressed)");
  }

  [Test, Category("RoundTrip")]
  public void Writer_MixedCompressedAndStored() {
    // Two files: one opts-in, one forces stored. Both must round-trip.
    var compressible = new byte[4096];
    Array.Fill(compressible, (byte)0x42);

    var incompressible = new byte[4096];
    new Random(314).NextBytes(incompressible);

    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter();
    w.AddFile("PACKED.BIN", compressible, compress: true);
    w.AddFile("STORED.BIN", incompressible, compress: false);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileSystem.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(compressible));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(incompressible));
  }

  [Test, Category("RoundTrip")]
  public void Writer_EnableCompressionFlag_Off() {
    // When EnableCompression is disabled globally, no MDFAT entry should
    // carry the compressed flag, even for highly compressible payloads.
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "aaaaaaaaaaaaaaaa", 1000)));

    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter { EnableCompression = false };
    w.AddFile("RAW.TXT", text);
    var cvf = w.Build();

    var mdfatStart = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(44));
    var mdfatLen = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(48));
    var entries = (int)(mdfatLen * 512 / 4);
    for (var i = 0; i < entries; i++) {
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan((int)(mdfatStart * 512 + i * 4)));
      var flags = (entry >> 28) & 0xFu;
      Assert.That(flags, Is.Not.EqualTo(2u),
        $"cluster {i} should not be compressed when EnableCompression=false");
    }

    // And round-trip still works.
    using var ms = new MemoryStream(cvf);
    var r = new FileSystem.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(text));
  }

  [Test, Category("RoundTrip")]
  public void DriveSpace_CompressedCluster_RoundTrip() {
    // DriveSpace variant with an 8 KiB JM-derived window — must also
    // round-trip through the same reader.
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "DriveSpace compressed volume file payload. ", 300)));

    var w = new FileSystem.DoubleSpace.DoubleSpaceWriter { DriveSpace = true };
    w.AddFile("DS.TXT", text);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileSystem.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.IsDriveSpace, Is.True);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(text));
  }
}
