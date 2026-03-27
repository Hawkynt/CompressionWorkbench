using System.Buffers.Binary;
using System.Text;
using FileFormat.PackIt;

namespace Compression.Tests.PackIt;

[TestFixture]
public class PackItTests {

  // ── Archive builder helper ────────────────────────────────────────────────────

  /// <summary>
  /// Manually assembles a single PackIt entry in a stream, using the given magic,
  /// name, file type, creator, data fork, and resource fork.
  /// </summary>
  private static void WriteRawEntry(
      MemoryStream ms,
      byte[]       magic,
      string       name,
      string       fileType,
      string       creator,
      byte[]       dataFork,
      byte[]       rsrcFork) {

    ms.Write(magic);

    // 63-byte Pascal filename field
    var nameBytes = Encoding.Latin1.GetBytes(name);
    var nameLen   = Math.Min(nameBytes.Length, 62);
    var nameField = new byte[63];
    nameField[0]  = (byte)nameLen;
    Array.Copy(nameBytes, 0, nameField, 1, nameLen);
    ms.Write(nameField);

    // file type (4) + creator (4)
    var typeBytes    = PadOrTrunc(Encoding.ASCII.GetBytes(fileType), 4);
    var creatorBytes = PadOrTrunc(Encoding.ASCII.GetBytes(creator),  4);
    ms.Write(typeBytes);
    ms.Write(creatorBytes);

    // Finder flags (2) + locked (1) + pad (1)
    ms.Write(new byte[4]);

    // data fork size (uint32 BE) + resource fork size (uint32 BE)
    var sizes = new byte[8];
    BinaryPrimitives.WriteUInt32BigEndian(sizes.AsSpan(0, 4), (uint)dataFork.Length);
    BinaryPrimitives.WriteUInt32BigEndian(sizes.AsSpan(4, 4), (uint)rsrcFork.Length);
    ms.Write(sizes);

    ms.Write(dataFork);
    ms.Write(rsrcFork);
  }

  private static byte[] PadOrTrunc(byte[] src, int len) {
    var dest = new byte[len];
    Array.Copy(src, dest, Math.Min(src.Length, len));
    for (var i = src.Length; i < len; ++i)
      dest[i] = (byte)' ';
    return dest;
  }

  /// <summary>Builds a single-entry stored archive.</summary>
  private static byte[] BuildStoredArchive(string name, byte[] data) {
    using var ms = new MemoryStream();
    WriteRawEntry(ms, "PMag"u8.ToArray(), name, "TEXT", "CWIE", data, []);
    return ms.ToArray();
  }

  // ── Header / entry parsing ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Parse_SingleStoredEntry_OneEntry() {
    var archive = BuildStoredArchive("hello.txt", "hello"u8.ToArray());
    using var ms = new MemoryStream(archive);
    var r = new PackItReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Parse_Entry_Name() {
    var archive = BuildStoredArchive("readme.txt", [0x41]);
    using var ms = new MemoryStream(archive);
    var r = new PackItReader(ms);
    Assert.That(r.Entries[0].Name, Is.EqualTo("readme.txt"));
  }

  [Test, Category("HappyPath")]
  public void Parse_Entry_DataForkSize() {
    var data    = new byte[128];
    var archive = BuildStoredArchive("data.bin", data);
    using var ms = new MemoryStream(archive);
    var r = new PackItReader(ms);
    Assert.That(r.Entries[0].DataForkSize, Is.EqualTo(128));
  }

  [Test, Category("HappyPath")]
  public void Parse_Entry_FileTypeAndCreator() {
    var archive = BuildStoredArchive("doc.txt", [0x41]);
    using var ms = new MemoryStream(archive);
    var r = new PackItReader(ms);
    Assert.That(r.Entries[0].FileType, Is.EqualTo("TEXT"));
    Assert.That(r.Entries[0].Creator,  Is.EqualTo("CWIE"));
  }

  [Test, Category("HappyPath")]
  public void Parse_StoredEntry_IsCompressedFalse() {
    var archive = BuildStoredArchive("f.txt", [0x41]);
    using var ms = new MemoryStream(archive);
    var r = new PackItReader(ms);
    Assert.That(r.Entries[0].IsCompressed, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Parse_CompressedEntry_IsCompressedTrue() {
    // Build an entry with "PMa4" magic.
    using var ms = new MemoryStream();
    WriteRawEntry(ms, "PMa4"u8.ToArray(), "compressed.bin", "TEXT", "CWIE", [0xAB, 0xCD], []);
    ms.Position = 0;
    var r = new PackItReader(ms);
    Assert.That(r.Entries[0].IsCompressed, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Parse_MultipleEntries_AllFound() {
    using var ms = new MemoryStream();
    WriteRawEntry(ms, "PMag"u8.ToArray(), "first.txt",  "TEXT", "CWIE", "first"u8.ToArray(),  []);
    WriteRawEntry(ms, "PMag"u8.ToArray(), "second.txt", "TEXT", "CWIE", "second"u8.ToArray(), []);
    WriteRawEntry(ms, "PMag"u8.ToArray(), "third.txt",  "TEXT", "CWIE", "third"u8.ToArray(),  []);
    ms.Position = 0;
    var r = new PackItReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("first.txt"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("second.txt"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("third.txt"));
  }

  [Test, Category("EdgeCase")]
  public void Parse_UnknownMagic_StopsEarly() {
    using var ms = new MemoryStream();
    WriteRawEntry(ms, "PMag"u8.ToArray(), "valid.txt", "TEXT", "CWIE", [0x41], []);
    ms.Write("XXXX"u8);  // garbage magic — should stop parsing
    ms.Position = 0;
    var r = new PackItReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
  }

  [Test, Category("EdgeCase")]
  public void Parse_EmptyStream_NoEntries() {
    using var ms = new MemoryStream();
    var r = new PackItReader(ms);
    Assert.That(r.Entries, Is.Empty);
  }

  // ── Extraction ────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_StoredEntry_ReturnsOriginalData() {
    var original = "Hello, PackIt!"u8.ToArray();
    var archive  = BuildStoredArchive("hello.txt", original);
    using var ms = new MemoryStream(archive);
    var r      = new PackItReader(ms);
    var result = r.Extract(r.Entries[0]);
    Assert.That(result, Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_BinaryData_RoundTrips() {
    var original = new byte[256];
    for (var i = 0; i < 256; ++i) original[i] = (byte)i;
    var archive = BuildStoredArchive("allbytes.bin", original);
    using var ms = new MemoryStream(archive);
    var r = new PackItReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void Extract_EmptyDataFork_ReturnsEmpty() {
    var archive = BuildStoredArchive("empty.txt", []);
    using var ms = new MemoryStream(archive);
    var r = new PackItReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_MultipleEntries_CorrectData() {
    var data1 = "Hello"u8.ToArray();
    var data2 = "World"u8.ToArray();
    using var ms = new MemoryStream();
    WriteRawEntry(ms, "PMag"u8.ToArray(), "a.txt", "TEXT", "CWIE", data1, []);
    WriteRawEntry(ms, "PMag"u8.ToArray(), "b.txt", "TEXT", "CWIE", data2, []);
    ms.Position = 0;
    var r = new PackItReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  // ── Error handling ────────────────────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var archive = BuildStoredArchive("x.txt", [0x41]);
    using var ms = new MemoryStream(archive);
    var r = new PackItReader(ms);
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }

  [Test, Category("ErrorHandling")]
  public void Dispose_ThenExtract_Throws() {
    var archive = BuildStoredArchive("f.txt", [0x41]);
    using var ms = new MemoryStream(archive);
    var r = new PackItReader(ms);
    var entry = r.Entries[0];
    r.Dispose();
    Assert.Throws<ObjectDisposedException>(() => r.Extract(entry));
  }

  // ── Writer round-trip ─────────────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_RoundTrip_SingleFile() {
    var data = "Hello, PackIt Writer!"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new PackItWriter(ms, leaveOpen: true))
      w.AddFile("hello.txt", data);

    ms.Position = 0;
    var r = new PackItReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_RoundTrip_MultipleFiles() {
    var data1 = new byte[256];
    var data2 = new byte[128];
    Random.Shared.NextBytes(data1);
    Random.Shared.NextBytes(data2);

    using var ms = new MemoryStream();
    using (var w = new PackItWriter(ms, leaveOpen: true)) {
      w.AddFile("file1.bin", data1);
      w.AddFile("file2.bin", data2);
    }

    ms.Position = 0;
    var r = new PackItReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_RoundTrip_EmptyFile() {
    using var ms = new MemoryStream();
    using (var w = new PackItWriter(ms, leaveOpen: true))
      w.AddFile("empty.txt", []);

    ms.Position = 0;
    var r = new PackItReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Writer_Magic_IsPMag() {
    using var ms = new MemoryStream();
    using (var w = new PackItWriter(ms, leaveOpen: true))
      w.AddFile("x", [1, 2, 3]);

    ms.Position = 0;
    Span<byte> magic = stackalloc byte[4];
    ms.ReadExactly(magic);
    Assert.That(magic.ToArray(), Is.EqualTo("PMag"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Writer_StoredEntry_IsCompressedFalse() {
    var data = "test"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new PackItWriter(ms, leaveOpen: true))
      w.AddFile("t.txt", data);

    ms.Position = 0;
    var r = new PackItReader(ms);
    Assert.That(r.Entries[0].IsCompressed, Is.False);
  }

  // ── leaveOpen ─────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Reader_LeaveOpen_True_StreamRemainsUsable() {
    var archive = BuildStoredArchive("f.txt", [0x41]);
    using var ms = new MemoryStream(archive);
    using (var r = new PackItReader(ms, leaveOpen: true)) {
      _ = r.Entries;
    }
    Assert.That(ms.CanRead, Is.True);
  }
}
