using System.Buffers.Binary;
using System.Text;
using FileFormat.StuffIt;

namespace Compression.Tests.StuffIt;

[TestFixture]
public class StuffItTests {
  // ── Archive builder helpers ──────────────────────────────────────────────────

  /// <summary>
  /// Builds a minimal "SIT!" archive with one entry whose data fork uses
  /// the specified method. The resource fork is always stored empty.
  /// </summary>
  private static byte[] BuildSitArchive(
    string fileName,
    byte dataMethod,
    byte[] dataForkCompressed,
    uint dataForkOriginalSize,
    byte[] resourceForkCompressed,
    uint resourceForkOriginalSize,
    uint macModDate = 0) {

    byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
    if (nameBytes.Length > 63)
      Array.Resize(ref nameBytes, 63);

    // ── Entry header (112 bytes) ──────────────────────────────────────────────
    byte[] entryHeader = new byte[112];
    entryHeader[0] = 0;                          // resource method = Store
    entryHeader[1] = dataMethod;                 // data method
    entryHeader[2] = (byte)nameBytes.Length;     // name length
    Array.Copy(nameBytes, 0, entryHeader, 3, nameBytes.Length);

    // file type = "TEXT", file creator = "ttxt"
    Encoding.ASCII.GetBytes("TEXT").CopyTo(entryHeader, 66);
    Encoding.ASCII.GetBytes("ttxt").CopyTo(entryHeader, 70);

    // Finder flags = 0  [74..76]
    // creation date = 0 [76..80]
    BinaryPrimitives.WriteUInt32BigEndian(entryHeader.AsSpan(80, 4), macModDate); // mod date

    BinaryPrimitives.WriteUInt32BigEndian(entryHeader.AsSpan(84, 4), resourceForkOriginalSize);
    BinaryPrimitives.WriteUInt32BigEndian(entryHeader.AsSpan(88, 4), dataForkOriginalSize);
    BinaryPrimitives.WriteUInt32BigEndian(entryHeader.AsSpan(92, 4), (uint)resourceForkCompressed.Length);
    BinaryPrimitives.WriteUInt32BigEndian(entryHeader.AsSpan(96, 4), (uint)dataForkCompressed.Length);

    // CRC-16 = 0 (no checksum stored — reader skips verification when 0)
    // [100..102] resource fork CRC-16 = 0
    // [102..104] data fork CRC-16 = 0
    // [104..110] reserved
    // [110..112] header CRC-16 = 0 (not verified by reader)

    // ── Archive header (22 bytes) ─────────────────────────────────────────────
    byte[] archiveHeader = new byte[22];
    BinaryPrimitives.WriteUInt32BigEndian(archiveHeader.AsSpan(0, 4), 0x53495421); // "SIT!"
    BinaryPrimitives.WriteUInt16BigEndian(archiveHeader.AsSpan(4, 2), 1);           // file count = 1
    uint totalSize = (uint)(22 + 112 + resourceForkCompressed.Length + dataForkCompressed.Length);
    BinaryPrimitives.WriteUInt32BigEndian(archiveHeader.AsSpan(6, 4), totalSize);
    BinaryPrimitives.WriteUInt32BigEndian(archiveHeader.AsSpan(10, 4), 0x724C6175); // "rLau"
    archiveHeader[14] = 1; // version 1

    // ── Assemble ──────────────────────────────────────────────────────────────
    using var ms = new MemoryStream();
    ms.Write(archiveHeader);
    ms.Write(entryHeader);
    ms.Write(resourceForkCompressed);
    ms.Write(dataForkCompressed);
    return ms.ToArray();
  }

  /// <summary>Builds an archive with a single stored (method 0) data fork entry.</summary>
  private static byte[] BuildStoredArchive(string fileName, byte[] data) =>
    BuildSitArchive(fileName, 0, data, (uint)data.Length, [], 0);

  // ── Header parsing ───────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Parse_ValidHeader_ReadsOneEntry() {
    byte[] data = "hello"u8.ToArray();
    byte[] archive = BuildStoredArchive("hello.txt", data);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
  }

  [Category("HappyPath")]
  [Test]
  public void Parse_Entry_FileName() {
    byte[] archive = BuildStoredArchive("readme.txt", "x"u8.ToArray());

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);

    Assert.That(reader.Entries[0].FileName, Is.EqualTo("readme.txt"));
  }

  [Category("HappyPath")]
  [Test]
  public void Parse_Entry_FileSizes() {
    byte[] original = new byte[100];
    byte[] archive = BuildStoredArchive("data.bin", original);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);
    var entry = reader.Entries[0];

    Assert.That(entry.DataForkSize,       Is.EqualTo(100));
    Assert.That(entry.CompressedDataSize, Is.EqualTo(100));
  }

  [Category("HappyPath")]
  [Test]
  public void Parse_Entry_FileTypeAndCreator() {
    byte[] archive = BuildStoredArchive("doc.txt", [0x41]);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);
    var entry = reader.Entries[0];

    Assert.That(entry.FileType,    Is.EqualTo("TEXT"));
    Assert.That(entry.FileCreator, Is.EqualTo("ttxt"));
  }

  [Category("HappyPath")]
  [Test]
  public void Parse_Entry_ModificationDate() {
    // Mac epoch seconds for 1970-01-01 = 66 years (some leap years) past 1904-01-01
    // Offset = (1970-1904) years in seconds ≈ 2082844800
    const uint macDate = 2082844800u;
    byte[] archive = BuildSitArchive("f.txt", 0, [0x41], 1, [], 0, macDate);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);
    var entry = reader.Entries[0];

    // Just confirm it is a valid DateTime well after the Mac epoch.
    Assert.That(entry.LastModified, Is.GreaterThan(new DateTime(1904, 1, 1)));
  }

  [Category("EdgeCase")]
  [Test]
  public void Parse_Entry_ZeroModDate_GivesMinValue() {
    byte[] archive = BuildSitArchive("f.txt", 0, [0x41], 1, [], 0, 0);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);

    Assert.That(reader.Entries[0].LastModified, Is.EqualTo(DateTime.MinValue));
  }

  [Category("ErrorHandling")]
  [Test]
  public void Ctor_WrongMagic_Throws() {
    byte[] bad = new byte[22];
    bad[0] = 0x00; bad[1] = 0x01; bad[2] = 0x02; bad[3] = 0x03;

    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => _ = new StuffItReader(ms));
  }

  [Category("ErrorHandling")]
  [Test]
  public void Ctor_MissingRlauSignature_Throws() {
    byte[] archive = BuildStoredArchive("f.txt", [0x41]);
    // Corrupt the "rLau" signature at offset 10.
    archive[10] = 0xFF;

    using var ms = new MemoryStream(archive);
    Assert.Throws<InvalidDataException>(() => _ = new StuffItReader(ms));
  }

  // ── Store (method 0) ─────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Store_Extract_SmallText() {
    byte[] original = "Hello, StuffIt!"u8.ToArray();
    byte[] archive = BuildStoredArchive("hello.txt", original);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);
    byte[] result = reader.Extract(reader.Entries[0]);

    Assert.That(result, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Store_Extract_BinaryData() {
    byte[] original = new byte[256];
    for (int i = 0; i < 256; ++i)
      original[i] = (byte)i;

    byte[] archive = BuildStoredArchive("bin.dat", original);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);
    byte[] result = reader.Extract(reader.Entries[0]);

    Assert.That(result, Is.EqualTo(original));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Store_Extract_EmptyDataFork() {
    byte[] archive = BuildStoredArchive("empty.txt", []);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);
    byte[] result = reader.Extract(reader.Entries[0]);

    Assert.That(result, Is.Empty);
  }

  // ── RLE (method 1) ───────────────────────────────────────────────────────────

  /// <summary>
  /// Encodes raw bytes using StuffIt RLE (method 1) for use in test archives.
  /// Only the minimum needed for tests: runs and literals.
  /// </summary>
  private static byte[] RleEncode(byte[] data) {
    var output = new List<byte>(data.Length);
    int i = 0;
    while (i < data.Length) {
      byte value = data[i];
      if (value == 0x90) {
        output.Add(0x90);
        output.Add(0x00);
        ++i;
        continue;
      }
      int runEnd = i + 1;
      while (runEnd < data.Length && data[runEnd] == value && runEnd - i < 255)
        ++runEnd;
      int runLength = runEnd - i;
      output.Add(value);
      if (runLength >= 2) {
        output.Add(0x90);
        output.Add((byte)(runLength - 1));
      }
      i += runLength;
    }
    return [.. output];
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Rle_Extract_RepetitiveData() {
    byte[] original = new byte[100];
    Array.Fill(original, (byte)'A');
    byte[] compressed = RleEncode(original);

    byte[] archive = BuildSitArchive("rle.txt", 1, compressed, (uint)original.Length, [], 0);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);
    byte[] result = reader.Extract(reader.Entries[0]);

    Assert.That(result, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Rle_Extract_LiteralEscapeMarker() {
    // Data containing 0x90 bytes (must be escaped as 0x90 0x00)
    byte[] original = [0x90, 0x41, 0x90];
    byte[] compressed = [0x90, 0x00, 0x41, 0x90, 0x00];

    byte[] archive = BuildSitArchive("esc.bin", 1, compressed, (uint)original.Length, [], 0);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);
    byte[] result = reader.Extract(reader.Entries[0]);

    Assert.That(result, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Rle_Extract_MixedRunsAndLiterals() {
    byte[] original = [0x41, 0x41, 0x41, 0x42, 0x43, 0x43];
    byte[] compressed = RleEncode(original);

    byte[] archive = BuildSitArchive("mixed.bin", 1, compressed, (uint)original.Length, [], 0);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);
    byte[] result = reader.Extract(reader.Entries[0]);

    Assert.That(result, Is.EqualTo(original));
  }

  // ── Resource fork ─────────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void ExtractResourceFork_Stored_ReturnsData() {
    byte[] resource = "resource data"u8.ToArray();
    byte[] dataFork = "data fork"u8.ToArray();

    byte[] archive = BuildSitArchive("mac.txt", 0, dataFork, (uint)dataFork.Length,
                                     resource, (uint)resource.Length);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);
    byte[] result = reader.ExtractResourceFork(reader.Entries[0]);

    Assert.That(result, Is.EqualTo(resource));
  }

  [Category("EdgeCase")]
  [Test]
  public void ExtractResourceFork_Empty_ReturnsEmpty() {
    byte[] archive = BuildStoredArchive("norc.txt", "data"u8.ToArray());

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);
    byte[] result = reader.ExtractResourceFork(reader.Entries[0]);

    Assert.That(result, Is.Empty);
  }

  // ── Multi-entry archive ───────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void MultiEntry_AllEntriesListed() {
    byte[] data1 = "first"u8.ToArray();
    byte[] data2 = "second"u8.ToArray();
    byte[] data3 = "third"u8.ToArray();

    // Build the headers and data sequentially into one archive with fileCount=3.
    var ms = new MemoryStream();

    // Reserve space for the archive header; we'll write it last.
    long headerPos = ms.Position;
    ms.Write(new byte[22]);

    long entriesStart = ms.Position;
    WriteEntry(ms, "first.txt",  0, data1, [], data1.Length,  0);
    WriteEntry(ms, "second.txt", 0, data2, [], data2.Length,  0);
    WriteEntry(ms, "third.txt",  0, data3, [], data3.Length,  0);

    long totalSize = ms.Length;

    // Write archive header.
    ms.Position = headerPos;
    Span<byte> ah = stackalloc byte[22];
    BinaryPrimitives.WriteUInt32BigEndian(ah,       0x53495421); // "SIT!"
    BinaryPrimitives.WriteUInt16BigEndian(ah[4..],  3);          // 3 files
    BinaryPrimitives.WriteUInt32BigEndian(ah[6..],  (uint)totalSize);
    BinaryPrimitives.WriteUInt32BigEndian(ah[10..], 0x724C6175); // "rLau"
    ah[14] = 1;
    ms.Write(ah);

    ms.Position = 0;
    using var reader = new StuffItReader(ms, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("first.txt"));
    Assert.That(reader.Entries[1].FileName, Is.EqualTo("second.txt"));
    Assert.That(reader.Entries[2].FileName, Is.EqualTo("third.txt"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void MultiEntry_ExtractEachEntry() {
    byte[] data1 = "Hello"u8.ToArray();
    byte[] data2 = "World"u8.ToArray();

    var ms = new MemoryStream();
    ms.Write(new byte[22]);
    WriteEntry(ms, "a.txt", 0, data1, [], data1.Length, 0);
    WriteEntry(ms, "b.txt", 0, data2, [], data2.Length, 0);
    long totalSize = ms.Length;

    ms.Position = 0;
    Span<byte> ah = stackalloc byte[22];
    BinaryPrimitives.WriteUInt32BigEndian(ah,       0x53495421);
    BinaryPrimitives.WriteUInt16BigEndian(ah[4..],  2);
    BinaryPrimitives.WriteUInt32BigEndian(ah[6..],  (uint)totalSize);
    BinaryPrimitives.WriteUInt32BigEndian(ah[10..], 0x724C6175);
    ah[14] = 1;
    ms.Write(ah);
    ms.Position = 0;

    using var reader = new StuffItReader(ms, leaveOpen: true);

    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.Extract(reader.Entries[1]), Is.EqualTo(data2));
  }

  // ── Error handling ────────────────────────────────────────────────────────────

  [Category("ErrorHandling")]
  [Test]
  public void Extract_UnsupportedMethod_Throws() {
    // Method 2 = LZC — not supported.
    byte[] archive = BuildSitArchive("f.bin", 2, [0xAB, 0xCD], 100, [], 0);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);

    Assert.Throws<NotSupportedException>(() => reader.Extract(reader.Entries[0]));
  }

  [Category("ErrorHandling")]
  [Test]
  public void Extract_NullEntry_Throws() {
    byte[] archive = BuildStoredArchive("f.txt", [0x41]);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);

    Assert.Throws<ArgumentNullException>(() => reader.Extract(null!));
  }

  [Category("ErrorHandling")]
  [Test]
  public void Rle_TruncatedAfterMarker_Throws() {
    // Compressed data ends immediately after the 0x90 escape marker.
    byte[] badRle = [0x41, 0x90]; // 0x90 with no following count byte
    byte[] archive = BuildSitArchive("bad.bin", 1, badRle, 10, [], 0);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);

    Assert.Throws<InvalidDataException>(() => reader.Extract(reader.Entries[0]));
  }

  // ── IsDirectory ──────────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Entry_IsDirectory_AlwaysFalse() {
    byte[] archive = BuildStoredArchive("myfile.txt", [0x41]);

    using var ms = new MemoryStream(archive);
    using var reader = new StuffItReader(ms, leaveOpen: true);

    Assert.That(reader.Entries[0].IsDirectory, Is.False);
  }

  // ── Writer round-trip tests ──────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_SingleFile() {
    byte[] data = "Hello, StuffIt Writer!"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new StuffItWriter(ms, leaveOpen: true))
      w.AddFile("hello.txt", data);

    ms.Position = 0;
    using var reader = new StuffItReader(ms, leaveOpen: true);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("hello.txt"));
    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_MultipleFiles() {
    byte[] data1 = new byte[256];
    byte[] data2 = new byte[128];
    Random.Shared.NextBytes(data1);
    Random.Shared.NextBytes(data2);

    using var ms = new MemoryStream();
    using (var w = new StuffItWriter(ms, leaveOpen: true)) {
      w.AddFile("file1.bin", data1);
      w.AddFile("file2.bin", data2);
    }

    ms.Position = 0;
    using var reader = new StuffItReader(ms, leaveOpen: true);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.Extract(reader.Entries[1]), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_EmptyFile() {
    using var ms = new MemoryStream();
    using (var w = new StuffItWriter(ms, leaveOpen: true))
      w.AddFile("empty.txt", []);

    ms.Position = 0;
    using var reader = new StuffItReader(ms, leaveOpen: true);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Extract(reader.Entries[0]), Is.Empty);
  }

  [Category("HappyPath")]
  [Test]
  public void Writer_Magic_IsSIT() {
    using var ms = new MemoryStream();
    using (var w = new StuffItWriter(ms, leaveOpen: true))
      w.AddFile("x", [1, 2, 3]);

    ms.Position = 0;
    Span<byte> magic = stackalloc byte[4];
    ms.ReadExactly(magic);
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(magic), Is.EqualTo(0x53495421u));
  }

  [Category("HappyPath")]
  [Test]
  public void Writer_FileTypeAndCreator_CustomValues() {
    using var ms = new MemoryStream();
    using (var w = new StuffItWriter(ms, leaveOpen: true))
      w.AddFile("doc.pdf", [0x25, 0x50, 0x44, 0x46], fileType: "PDF ", fileCreator: "CARO");

    ms.Position = 0;
    using var reader = new StuffItReader(ms, leaveOpen: true);
    Assert.That(reader.Entries[0].FileType, Is.EqualTo("PDF "));
    Assert.That(reader.Entries[0].FileCreator, Is.EqualTo("CARO"));
  }

  // ── Helper: write a single entry into a MemoryStream ─────────────────────────

  private static void WriteEntry(
    MemoryStream ms,
    string fileName,
    byte dataMethod,
    byte[] dataFork,
    byte[] resourceFork,
    int dataForkOrigSize,
    uint macModDate) {

    byte[] nameBytes = Encoding.ASCII.GetBytes(fileName);
    if (nameBytes.Length > 63) Array.Resize(ref nameBytes, 63);

    byte[] hdr = new byte[112];
    hdr[0] = 0;                       // resource method = Store
    hdr[1] = dataMethod;
    hdr[2] = (byte)nameBytes.Length;
    Array.Copy(nameBytes, 0, hdr, 3, nameBytes.Length);

    Encoding.ASCII.GetBytes("TEXT").CopyTo(hdr, 66);
    Encoding.ASCII.GetBytes("ttxt").CopyTo(hdr, 70);

    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(80, 4), macModDate);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(84, 4), (uint)resourceFork.Length); // rsrc uncompressed
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(88, 4), (uint)dataForkOrigSize);
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(92, 4), (uint)resourceFork.Length); // rsrc compressed
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(96, 4), (uint)dataFork.Length);     // data compressed

    ms.Write(hdr);
    ms.Write(resourceFork);
    ms.Write(dataFork);
  }
}
