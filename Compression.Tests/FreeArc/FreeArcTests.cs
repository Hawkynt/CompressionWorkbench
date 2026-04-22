using FileFormat.FreeArc;

namespace Compression.Tests.FreeArc;

[TestFixture]
public class FreeArcTests {

  // ── Helper ────────────────────────────────────────────────────────────────

  private static MemoryStream BuildArchive(Action<FreeArcWriter> populate) {
    var w = new FreeArcWriter();
    populate(w);
    return new MemoryStream(w.Build());
  }

  // ── Reader round-trip tests ───────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Read_SyntheticArchive_SingleFile() {
    var data = "Hello FreeArc!"u8.ToArray();
    using var ms = BuildArchive(w => w.AddFile("hello.txt", data));
    using var r = new FreeArcReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Entries[0].Method, Is.EqualTo("storing"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Read_SyntheticArchive_MultipleFiles() {
    var file1 = "First file data"u8.ToArray();
    var file2 = "Second file data"u8.ToArray();
    var file3 = "Third file data"u8.ToArray();

    using var ms = BuildArchive(w => {
      w.AddFile("a.txt", file1);
      w.AddFile("b.txt", file2);
      w.AddFile("c.txt", file3);
    });
    using var r = new FreeArcReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("a.txt"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("b.txt"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("c.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(file1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(file2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(file3));
  }

  [Test, Category("RoundTrip")]
  public void Read_SyntheticArchive_EmptyFile() {
    using var ms = BuildArchive(w => w.AddFile("empty.bin", []));
    using var r = new FreeArcReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("RoundTrip")]
  public void Read_SyntheticArchive_BinaryData() {
    // Deterministic non-trivial binary payload — never Random.Shared.NextBytes.
    var data = new byte[512];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 + 13);

    using var ms = BuildArchive(w => w.AddFile("binary.bin", data));
    using var r = new FreeArcReader(ms);

    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Read_SyntheticArchive_Utf8FileName() {
    var data = "content"u8.ToArray();
    using var ms = BuildArchive(w => w.AddFile("dossier/fichier.txt", data));
    using var r = new FreeArcReader(ms);

    Assert.That(r.Entries[0].Name, Is.EqualTo("dossier/fichier.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Read_SyntheticArchive_NoFiles() {
    using var ms = BuildArchive(_ => { });
    using var r = new FreeArcReader(ms);

    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("RoundTrip")]
  public void Read_SyntheticArchive_LargeFile() {
    var data = new byte[65536];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);

    using var ms = BuildArchive(w => w.AddFile("large.dat", data));
    using var r = new FreeArcReader(ms);

    Assert.That(r.Entries[0].Size, Is.EqualTo(65536));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Read_SyntheticArchive_CompressedSizeMatchesStored() {
    var data = "stored data"u8.ToArray();
    using var ms = BuildArchive(w => w.AddFile("f.txt", data));
    using var r = new FreeArcReader(ms);

    // Writer stores without compression so CompressedSize == Size.
    Assert.That(r.Entries[0].CompressedSize, Is.EqualTo(r.Entries[0].Size));
  }

  // ── Descriptor property tests ─────────────────────────────────────────────

  [Test]
  public void Descriptor_Properties() {
    var d = new FreeArcFormatDescriptor();
    Assert.That(d.Id,               Is.EqualTo("FreeArc"));
    Assert.That(d.DisplayName,      Is.EqualTo("FreeArc"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".arc"));
    Assert.That(d.Extensions,       Contains.Item(".arc"));
    Assert.That(d.Category,         Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Family,           Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Description,      Is.EqualTo("FreeArc compressed archive"));
    Assert.That(d.TarCompressionFormatId, Is.Null);
  }

  [Test]
  public void Descriptor_Id_IsDistinctFromArc() {
    // Ensure we don't accidentally share the legacy ARC format id.
    var d = new FreeArcFormatDescriptor();
    Assert.That(d.Id, Is.Not.EqualTo("Arc"));
    Assert.That(d.Id, Is.Not.EqualTo("arc"));
  }

  [Test]
  public void Descriptor_Capabilities() {
    var d = new FreeArcFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries));
  }

  [Test]
  public void Descriptor_MagicSignature_Correct() {
    var d   = new FreeArcFormatDescriptor();
    var sig = d.MagicSignatures[0];
    Assert.That(sig.Bytes, Is.EqualTo(new byte[] { (byte)'A', (byte)'r', (byte)'C', 0x01 }));
    Assert.That(sig.Confidence, Is.EqualTo(0.95).Within(0.001));
  }

  [Test]
  public void Descriptor_Create_ViaInterface() {
    var data = "create test"u8.ToArray();
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, data);
      var d = new FreeArcFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(ms,
        [new Compression.Registry.ArchiveInputInfo(tmpFile, "test.txt", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      using var r = new FreeArcReader(ms);
      Assert.That(r.Entries, Has.Count.EqualTo(1));
      Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
      Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
    } finally { File.Delete(tmpFile); }
  }

  [Test]
  public void Descriptor_List_ViaInterface() {
    var data = "descriptor list test"u8.ToArray();
    using var ms = BuildArchive(w => w.AddFile("test.txt", data));
    var d = new FreeArcFormatDescriptor();
    var entries = d.List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(data.Length));
  }

  // ── Magic distinction ──────────────────────────────────────────────────────

  [Test]
  public void MagicDistinction_FreeArcVsLegacyArc() {
    // Legacy ARC uses 0x1A as the first byte.
    // FreeArc uses 'A' (0x41), 'r', 'C', 0x01.
    var freeArcMagic = FreeArcReader.Magic;
    Assert.That(freeArcMagic[0], Is.Not.EqualTo(0x1A),
      "FreeArc magic must not collide with legacy ARC 0x1A marker.");
    Assert.That(freeArcMagic[0], Is.EqualTo((byte)'A'));
    Assert.That(freeArcMagic[1], Is.EqualTo((byte)'r'));
    Assert.That(freeArcMagic[2], Is.EqualTo((byte)'C'));
    Assert.That(freeArcMagic[3], Is.EqualTo(0x01));
  }

  // ── Error handling ────────────────────────────────────────────────────────

  [Test]
  public void BadMagic_Throws_InvalidDataException() {
    var bad = new byte[] { 0x1A, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => _ = new FreeArcReader(ms));
  }

  [Test]
  public void TooSmall_Throws_InvalidDataException() {
    var small = new byte[] { (byte)'A', (byte)'r' }; // only 2 bytes
    using var ms = new MemoryStream(small);
    Assert.Throws<InvalidDataException>(() => _ = new FreeArcReader(ms));
  }

  [Test]
  public void EmptyStream_Throws_InvalidDataException() {
    using var ms = new MemoryStream();
    Assert.Throws<InvalidDataException>(() => _ = new FreeArcReader(ms));
  }

  [Test]
  public void CorrectMagicButTruncatedBody_ProducesEmptyEntries() {
    // 4-byte valid magic + 4-byte flags but then nothing — the reader
    // treats EOF on the block-type byte as end-of-archive (no entries).
    var buf = new byte[8];
    buf[0] = (byte)'A'; buf[1] = (byte)'r'; buf[2] = (byte)'C'; buf[3] = 0x01;
    using var ms = new MemoryStream(buf);
    var r = new FreeArcReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test]
  public void WrongMagicByte4_Throws_InvalidDataException() {
    // 'A','r','C' but 0x02 instead of 0x01
    var bad = new byte[] { (byte)'A', (byte)'r', (byte)'C', 0x02, 0, 0, 0, 0, 0 };
    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => _ = new FreeArcReader(ms));
  }
}
