using System.Buffers.Binary;
using System.Text;
using FileFormat.DiskDoubler;

namespace Compression.Tests.DiskDoubler;

[TestFixture]
public class DiskDoublerTests {

  // ── Archive builder helper ────────────────────────────────────────────────────

  /// <summary>
  /// Builds a minimal DiskDoubler file with the given data and resource fork parameters.
  /// </summary>
  private static byte[] BuildDdFile(
      string name,
      byte   dataMethod,
      byte[] dataBytes,
      uint   dataOrigSize,
      byte   rsrcMethod,
      byte[] rsrcBytes,
      uint   rsrcOrigSize) {

    var hdr = new byte[DiskDoublerReader.HeaderSize];
    // [4..8]  file type "TEXT"
    Encoding.ASCII.GetBytes("TEXT").CopyTo(hdr, 4);
    // [8..12] creator "CWIE"
    Encoding.ASCII.GetBytes("CWIE").CopyTo(hdr, 8);
    // [16..20] data fork original size
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(16, 4), dataOrigSize);
    // [20..24] data fork compressed size
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(20, 4), (uint)dataBytes.Length);
    // [24..28] resource fork original size
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(24, 4), rsrcOrigSize);
    // [28..32] resource fork compressed size
    BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(28, 4), (uint)rsrcBytes.Length);
    // [32] data method, [33] rsrc method
    hdr[32] = dataMethod;
    hdr[33] = rsrcMethod;
    // [34] Pascal name length, [35..] name
    var nameBytes = Encoding.Latin1.GetBytes(name);
    var nameLen   = Math.Min(nameBytes.Length, 47);
    hdr[34] = (byte)nameLen;
    Array.Copy(nameBytes, 0, hdr, 35, nameLen);

    using var ms = new MemoryStream();
    ms.Write(hdr);
    ms.Write(dataBytes);
    ms.Write(rsrcBytes);
    return ms.ToArray();
  }

  /// <summary>Builds a DiskDoubler file with a stored data fork only.</summary>
  private static byte[] BuildStoredFile(string name, byte[] data) =>
    BuildDdFile(name, 0, data, (uint)data.Length, 0, [], 0);

  // ── Header parsing ────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Parse_StoredDataFork_OneEntry() {
    var archive = BuildStoredFile("hello.txt", "Hello!"u8.ToArray());
    using var ms = new MemoryStream(archive);
    var r = new DiskDoublerReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Parse_Entry_IsDataFork() {
    var archive = BuildStoredFile("readme.txt", [0x41]);
    using var ms = new MemoryStream(archive);
    var r = new DiskDoublerReader(ms);
    Assert.That(r.Entries[0].IsDataFork, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Parse_Entry_Name() {
    var archive = BuildStoredFile("document.txt", [0x41]);
    using var ms = new MemoryStream(archive);
    var r = new DiskDoublerReader(ms);
    Assert.That(r.Entries[0].Name, Is.EqualTo("document.txt"));
  }

  [Test, Category("HappyPath")]
  public void Parse_Entry_OriginalSize() {
    var data    = new byte[512];
    var archive = BuildStoredFile("data.bin", data);
    using var ms = new MemoryStream(archive);
    var r = new DiskDoublerReader(ms);
    Assert.That(r.Entries[0].OriginalSize, Is.EqualTo(512));
  }

  [Test, Category("HappyPath")]
  public void Parse_Entry_CompressedSize() {
    var data    = new byte[100];
    var archive = BuildStoredFile("data.bin", data);
    using var ms = new MemoryStream(archive);
    var r = new DiskDoublerReader(ms);
    Assert.That(r.Entries[0].CompressedSize, Is.EqualTo(100));
  }

  [Test, Category("HappyPath")]
  public void Parse_WithResourceFork_TwoEntries() {
    var data = "data fork"u8.ToArray();
    var rsrc = "rsrc fork"u8.ToArray();
    var archive = BuildDdFile("mac.txt", 0, data, (uint)data.Length,
                              0, rsrc, (uint)rsrc.Length);
    using var ms = new MemoryStream(archive);
    var r = new DiskDoublerReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].IsDataFork, Is.True);
    Assert.That(r.Entries[1].IsDataFork, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Parse_ResourceForkEntry_NameSuffix() {
    byte[] data = [0x41];
    byte[] rsrc = [0x42];
    var archive = BuildDdFile("file", 0, data, 1u, 0, rsrc, 1u);
    using var ms = new MemoryStream(archive);
    var r = new DiskDoublerReader(ms);
    Assert.That(r.Entries[1].Name, Does.EndWith(".rsrc"));
  }

  [Test, Category("EdgeCase")]
  public void Parse_EmptyName_NoException() {
    var archive = BuildStoredFile("", [0x41]);
    using var ms = new MemoryStream(archive);
    var r = new DiskDoublerReader(ms);
    Assert.That(r.Entries[0].Name, Is.EqualTo(string.Empty));
  }

  // ── Extraction ────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_StoredEntry_ReturnsOriginalData() {
    var original = "Hello, DiskDoubler!"u8.ToArray();
    var archive  = BuildStoredFile("hello.txt", original);
    using var ms = new MemoryStream(archive);
    var r      = new DiskDoublerReader(ms);
    var result = r.Extract(r.Entries[0]);
    Assert.That(result, Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Extract_BinaryData_RoundTrips() {
    var original = new byte[256];
    for (var i = 0; i < 256; ++i) original[i] = (byte)i;
    var archive = BuildStoredFile("all-bytes.bin", original);
    using var ms = new MemoryStream(archive);
    var r = new DiskDoublerReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void Extract_EmptyDataFork_ReturnsEmpty() {
    var archive = BuildDdFile("empty", 0, [], 0u, 0, [], 0u);
    using var ms = new MemoryStream(archive);
    var r = new DiskDoublerReader(ms);
    // No entries expected (both forks are zero-sized).
    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Extract_UnsupportedMethod_ReturnsRawBytes() {
    // Method 3 (LZC) is proprietary — reader returns raw compressed bytes.
    var compressed = new byte[] { 0xAB, 0xCD, 0xEF };
    var archive = BuildDdFile("file.bin", 3, compressed, 100u, 0, [], 0u);
    using var ms = new MemoryStream(archive);
    var r      = new DiskDoublerReader(ms);
    var result = r.Extract(r.Entries[0]);
    Assert.That(result, Is.EqualTo(compressed));
  }

  // ── Error handling ────────────────────────────────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void Ctor_StreamTooShort_Throws() {
    var tooShort = new byte[10];
    using var ms = new MemoryStream(tooShort);
    Assert.Throws<InvalidDataException>(() => _ = new DiskDoublerReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var archive = BuildStoredFile("x", [0x41]);
    using var ms = new MemoryStream(archive);
    var r = new DiskDoublerReader(ms);
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }

  [Test, Category("ErrorHandling")]
  public void Dispose_ThenExtract_Throws() {
    var archive = BuildStoredFile("f.txt", [0x41]);
    using var ms = new MemoryStream(archive);
    var r = new DiskDoublerReader(ms);
    var entry = r.Entries[0];
    r.Dispose();
    Assert.Throws<ObjectDisposedException>(() => r.Extract(entry));
  }

  // ── leaveOpen ─────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void LeaveOpen_True_StreamRemainsUsable() {
    var archive = BuildStoredFile("f.txt", [0x41]);
    using var ms = new MemoryStream(archive);
    using (var r = new DiskDoublerReader(ms, leaveOpen: true)) {
      _ = r.Entries;
    }
    Assert.That(ms.CanRead, Is.True);
  }
}
