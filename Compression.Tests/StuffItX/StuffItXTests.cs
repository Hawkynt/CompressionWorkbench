using System.IO.Compression;
using System.Text;
using FileFormat.StuffItX;

namespace Compression.Tests.StuffItX;

[TestFixture]
public class StuffItXTests {
  // ── Helpers ────────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a minimal synthetic StuffIt X archive with the given files.
  /// Uses our simplified element stream format with stored entries.
  /// </summary>
  private static byte[] BuildSitx(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();

    // Header: "StuffIt!" + padding to 0x60
    var header = new byte[0x60];
    Encoding.ASCII.GetBytes("StuffIt!").CopyTo(header, 0);
    // Element stream offset at 0x28 (common location) = 0x60
    header[0x28] = 0x00;
    header[0x29] = 0x00;
    header[0x2A] = 0x00;
    header[0x2B] = 0x60;
    ms.Write(header);

    // Write file elements
    foreach (var (name, data) in files) {
      var nameBytes = Encoding.UTF8.GetBytes(name);

      // Tag: 0x30 (file element)
      WriteP2(ms, 0x30);

      // Element data: P2(nameLen) + name + P2(method=0) + P2(origSize) + P2(compSize) + data
      var elementData = new MemoryStream();
      WriteP2(elementData, nameBytes.Length);
      elementData.Write(nameBytes);
      WriteP2(elementData, 0); // method = stored
      WriteP2(elementData, data.Length); // original size
      WriteP2(elementData, data.Length); // compressed size
      elementData.Write(data);

      WriteP2(ms, elementData.Length);
      ms.Write(elementData.ToArray());
    }

    // End of stream marker
    WriteP2(ms, 0x00); // tag = end
    WriteP2(ms, 0x00); // size = 0

    return ms.ToArray();
  }

  private static void WriteP2(Stream s, long value) {
    if (value < 0x80) {
      s.WriteByte((byte)value);
      return;
    }
    // Multi-byte P2
    var bytes = new List<byte>();
    var v = value;
    while (v > 0) {
      bytes.Add((byte)(v & 0x7F));
      v >>= 7;
    }
    bytes.Reverse();
    for (var i = 0; i < bytes.Count; i++) {
      if (i < bytes.Count - 1)
        s.WriteByte((byte)(bytes[i] | 0x80));
      else
        s.WriteByte(bytes[i]);
    }
  }

  // ── Tests ──────────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Magic_IsStuffIt() {
    var data = BuildSitx(("test.txt", "Hello"u8.ToArray()));
    Assert.That(Encoding.ASCII.GetString(data, 0, 8), Is.EqualTo("StuffIt!"));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_ListsStoredEntries() {
    var data = BuildSitx(
      ("file1.txt", "Hello"u8.ToArray()),
      ("file2.bin", new byte[] { 1, 2, 3 }));

    using var ms = new MemoryStream(data);
    var reader = new StuffItXReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("file1.txt"));
    Assert.That(reader.Entries[1].Name, Is.EqualTo("file2.bin"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Reader_ExtractsStoredData() {
    var content = "Hello, StuffIt X!"u8.ToArray();
    var data = BuildSitx(("test.txt", content));

    using var ms = new MemoryStream(data);
    var reader = new StuffItXReader(ms);

    var extracted = reader.Extract(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_EntrySizes_AreCorrect() {
    var content = new byte[512];
    new Random(42).NextBytes(content);
    var data = BuildSitx(("big.bin", content));

    using var ms = new MemoryStream(data);
    var reader = new StuffItXReader(ms);

    Assert.That(reader.Entries[0].OriginalSize, Is.EqualTo(512));
    Assert.That(reader.Entries[0].CompressedSize, Is.EqualTo(512));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_MethodIsStored() {
    var data = BuildSitx(("test.txt", [1, 2, 3]));

    using var ms = new MemoryStream(data);
    var reader = new StuffItXReader(ms);

    Assert.That(reader.Entries[0].Method, Is.EqualTo("Stored"));
  }

  [Category("EdgeCase")]
  [Test]
  public void InvalidMagic_Throws() {
    var data = new byte[0x60];
    Encoding.ASCII.GetBytes("NotStuff").CopyTo(data, 0);

    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => new StuffItXReader(ms));
  }

  // ── Descriptor ────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Properties() {
    var desc = new StuffItXFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("StuffItX"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".sitx"));
    Assert.That(desc.MagicSignatures.Count, Is.EqualTo(1));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_List_ReturnsEntries() {
    var data = BuildSitx(("a.txt", [1, 2, 3]), ("b.bin", [4, 5]));
    using var ms = new MemoryStream(data);
    var desc = new StuffItXFormatDescriptor();
    var entries = desc.List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("a.txt"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_Extract_WritesFiles() {
    var content = "StuffIt X test"u8.ToArray();
    var data = BuildSitx(("test.txt", content));

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      var desc = new StuffItXFormatDescriptor();
      desc.Extract(ms, tmp, null, null);

      var extracted = File.ReadAllBytes(Path.Combine(tmp, "test.txt"));
      Assert.That(extracted, Is.EqualTo(content));
    } finally {
      Directory.Delete(tmp, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new StuffItXFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Writer_HasStuffItMagic() {
    var w = new StuffItXWriter();
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();
    Assert.That(Encoding.ASCII.GetString(bytes, 0, 8), Is.EqualTo("StuffIt!"));
  }
}
