using System.Text;
using FileFormat.Sar;

namespace Compression.Tests.Sar;

[TestFixture]
public class SarTests {
  // ── Helpers ────────────────────────────────────────────────────────────

  /// <summary>Builds a minimal SAR archive with the given files.</summary>
  private static byte[] BuildSar(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();

    // Calculate header size
    var headerSize = 2 + 4; // file count (uint16 BE) + data offset (uint32 BE)
    foreach (var f in files)
      headerSize += f.Name.Length + 1 + 4 + 4; // name+null, offset, size

    var dataOffset = (uint)headerSize;

    // Write file count (uint16 BE)
    WriteUInt16BE(ms, (ushort)files.Length);
    // Write data offset (uint32 BE)
    WriteUInt32BE(ms, dataOffset);

    // Write entry headers
    uint currentOffset = 0;
    foreach (var f in files) {
      ms.Write(Encoding.ASCII.GetBytes(f.Name));
      ms.WriteByte(0);
      WriteUInt32BE(ms, currentOffset);
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
    var sar = BuildSar(("test.txt", "Hello"u8.ToArray()));
    using var ms = new MemoryStream(sar);
    var reader = new SarReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("test.txt"));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_MultipleEntries() {
    var sar = BuildSar(
      ("script.txt", "Hello"u8.ToArray()),
      ("image.bmp", new byte[] { 1, 2, 3, 4 }),
      ("sound.wav", new byte[] { 5, 6, 7 }));

    using var ms = new MemoryStream(sar);
    var reader = new SarReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("script.txt"));
    Assert.That(reader.Entries[1].Name, Is.EqualTo("image.bmp"));
    Assert.That(reader.Entries[2].Name, Is.EqualTo("sound.wav"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Reader_ExtractsData() {
    var data1 = "First file"u8.ToArray();
    var data2 = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };

    var sar = BuildSar(("first.txt", data1), ("second.bin", data2));
    using var ms = new MemoryStream(sar);
    var reader = new SarReader(ms);

    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.Extract(reader.Entries[1]), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_EntrySize_IsCorrect() {
    var data = new byte[256];
    new Random(99).NextBytes(data);
    var sar = BuildSar(("data.bin", data));

    using var ms = new MemoryStream(sar);
    var reader = new SarReader(ms);

    Assert.That(reader.Entries[0].Size, Is.EqualTo(256));
  }

  [Category("EdgeCase")]
  [Test]
  public void Reader_EmptyFile_ExtractsEmpty() {
    var sar = BuildSar(("empty.txt", []));
    using var ms = new MemoryStream(sar);
    var reader = new SarReader(ms);

    Assert.That(reader.Extract(reader.Entries[0]), Is.Empty);
  }

  // ── Descriptor ────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Properties() {
    var desc = new SarFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Sar"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".sar"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_List_ReturnsEntries() {
    var sar = BuildSar(("a.txt", [1, 2]), ("b.bin", [3, 4, 5]));
    using var ms = new MemoryStream(sar);
    var desc = new SarFormatDescriptor();
    var entries = desc.List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("a.txt"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Extract_WritesFiles() {
    var data = "SAR extract test"u8.ToArray();
    var sar = BuildSar(("test.txt", data));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(sar);
      var desc = new SarFormatDescriptor();
      desc.Extract(ms, tmp, null, null);

      var extracted = File.ReadAllBytes(Path.Combine(tmp, "test.txt"));
      Assert.That(extracted, Is.EqualTo(data));
    } finally {
      Directory.Delete(tmp, true);
    }
  }
}
