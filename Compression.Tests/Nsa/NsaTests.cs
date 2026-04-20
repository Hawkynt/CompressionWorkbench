using System.Text;
using FileFormat.Nsa;

namespace Compression.Tests.Nsa;

[TestFixture]
public class NsaTests {
  // ── Helpers ────────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a minimal NSA archive with uncompressed (method 0) entries.
  /// </summary>
  private static byte[] BuildNsa(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();

    // Calculate header size
    var headerSize = 2 + 4; // file count (uint16 BE) + data offset (uint32 BE)
    foreach (var f in files)
      headerSize += f.Name.Length + 1 + 1 + 4 + 4 + 4; // name+null, comp type, offset, comp size, orig size

    var dataOffset = (uint)headerSize;

    // Write file count (uint16 BE)
    WriteUInt16BE(ms, (ushort)files.Length);
    // Write data offset (uint32 BE)
    WriteUInt32BE(ms, dataOffset);

    // Write entry headers
    uint currentOffset = 0;
    foreach (var f in files) {
      // null-terminated filename
      ms.Write(Encoding.ASCII.GetBytes(f.Name));
      ms.WriteByte(0);
      // compression type: 0 = none
      ms.WriteByte(0);
      // offset relative to data start
      WriteUInt32BE(ms, currentOffset);
      // compressed size = original size (stored)
      WriteUInt32BE(ms, (uint)f.Data.Length);
      // original size
      WriteUInt32BE(ms, (uint)f.Data.Length);
      currentOffset += (uint)f.Data.Length;
    }

    // Write data
    foreach (var f in files)
      ms.Write(f.Data);

    return ms.ToArray();
  }

  private static void WriteUInt16BE(Stream s, ushort value) {
    s.WriteByte((byte)(value >> 8));
    s.WriteByte((byte)value);
  }

  private static void WriteUInt32BE(Stream s, uint value) {
    s.WriteByte((byte)(value >> 24));
    s.WriteByte((byte)(value >> 16));
    s.WriteByte((byte)(value >> 8));
    s.WriteByte((byte)value);
  }

  // ── Tests ──────────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Reader_ParsesEntries() {
    var nsa = BuildNsa(("test.txt", "Hello"u8.ToArray()));
    using var ms = new MemoryStream(nsa);
    var reader = new NsaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(reader.Entries[0].CompressionType, Is.EqualTo(NsaCompressionType.None));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_MultipleEntries() {
    var nsa = BuildNsa(
      ("file1.bmp", new byte[] { 1, 2, 3 }),
      ("file2.wav", new byte[] { 4, 5, 6, 7 }),
      ("file3.txt", "Hello world"u8.ToArray()));

    using var ms = new MemoryStream(nsa);
    var reader = new NsaReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("file1.bmp"));
    Assert.That(reader.Entries[1].Name, Is.EqualTo("file2.wav"));
    Assert.That(reader.Entries[2].Name, Is.EqualTo("file3.txt"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Reader_ExtractsStoredData() {
    var data1 = "First file content"u8.ToArray();
    var data2 = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };

    var nsa = BuildNsa(("first.txt", data1), ("second.bin", data2));
    using var ms = new MemoryStream(nsa);
    var reader = new NsaReader(ms);

    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.Extract(reader.Entries[1]), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_EntrySizes_AreCorrect() {
    var data = new byte[512];
    new Random(42).NextBytes(data);
    var nsa = BuildNsa(("big.dat", data));

    using var ms = new MemoryStream(nsa);
    var reader = new NsaReader(ms);

    Assert.That(reader.Entries[0].OriginalSize, Is.EqualTo(512));
    Assert.That(reader.Entries[0].CompressedSize, Is.EqualTo(512));
  }

  // ── Descriptor ────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Properties() {
    var desc = new NsaFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Nsa"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".nsa"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_List_ReturnsEntries() {
    var nsa = BuildNsa(("a.bmp", [1, 2]), ("b.wav", [3, 4, 5]));
    using var ms = new MemoryStream(nsa);
    var desc = new NsaFormatDescriptor();
    var entries = desc.List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("a.bmp"));
    Assert.That(entries[1].Name, Is.EqualTo("b.wav"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Extract_WritesFiles() {
    var data = "NSA extract test"u8.ToArray();
    var nsa = BuildNsa(("test.txt", data));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(nsa);
      var desc = new NsaFormatDescriptor();
      desc.Extract(ms, tmp, null, null);

      var extracted = File.ReadAllBytes(Path.Combine(tmp, "test.txt"));
      Assert.That(extracted, Is.EqualTo(data));
    } finally {
      Directory.Delete(tmp, true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_ReportsWormCapability() {
    var d = new NsaFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Category("HappyPath"), Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_TwoFiles_StoredCompression() {
    var data1 = "first file payload"u8.ToArray();
    var data2 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

    var w = new NsaWriter();
    w.AddFile("a.txt", data1);
    w.AddFile("b.bin", data2);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new NsaReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].CompressionType, Is.EqualTo(NsaCompressionType.None));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }
}
