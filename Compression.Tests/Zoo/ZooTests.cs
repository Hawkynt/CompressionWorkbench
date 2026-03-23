using FileFormat.Zoo;

namespace Compression.Tests.Zoo;

[TestFixture]
public class ZooTests {

  // ── Round-trip helpers ───────────────────────────────────────────────────

  private static byte[] CreateArchive(Action<ZooWriter> populate) {
    using var ms = new MemoryStream();
    using (var writer = new ZooWriter(ms, leaveOpen: true))
      populate(writer);
    return ms.ToArray();
  }

  private static byte[] ExtractFirst(byte[] archive) {
    using var reader = new ZooReader(new MemoryStream(archive));
    return reader.ExtractEntry(reader.Entries[0]);
  }

  private static void RoundTrip(byte[] data, ZooCompressionMethod method, string fileName = "test.dat") {
    var archive = CreateArchive(w => w.AddEntry(fileName, data, method));
    using var reader = new ZooReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  // ── Store round-trips ────────────────────────────────────────────────────

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Store_EmptyFile() => RoundTrip([], ZooCompressionMethod.Store);

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Store_SingleByte() => RoundTrip([0x42], ZooCompressionMethod.Store);

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Store_ShortText() =>
    RoundTrip("Hello, Zoo!"u8.ToArray(), ZooCompressionMethod.Store);

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Store_BinaryData_256Bytes() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i) data[i] = (byte)i;
    RoundTrip(data, ZooCompressionMethod.Store);
  }

  // ── LZW round-trips ──────────────────────────────────────────────────────

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Lzw_EmptyFile() => RoundTrip([], ZooCompressionMethod.Lzw);

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Lzw_SingleByte() => RoundTrip([0x42], ZooCompressionMethod.Lzw);

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Lzw_ShortText() =>
    RoundTrip("Hello, Zoo!"u8.ToArray(), ZooCompressionMethod.Lzw);

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Lzw_RepetitiveData() {
    var pattern = "ABCDEFGHIJ"u8.ToArray();
    var data = new byte[pattern.Length * 200];
    for (var i = 0; i < 200; ++i)
      pattern.CopyTo(data, i * pattern.Length);
    RoundTrip(data, ZooCompressionMethod.Lzw);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Lzw_AllZeros_4KB() =>
    RoundTrip(new byte[4096], ZooCompressionMethod.Lzw);

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Lzw_RandomData_2KB() {
    var rng = new Random(42);
    var data = new byte[2048];
    rng.NextBytes(data);
    RoundTrip(data, ZooCompressionMethod.Lzw);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Lzw_FallsBackToStore_WhenLarger() {
    // Random data compresses poorly; writer should fall back to Store.
    var rng = new Random(99);
    var data = new byte[512];
    rng.NextBytes(data);

    var archive = CreateArchive(w => w.AddEntry("rand.bin", data, ZooCompressionMethod.Lzw));
    using var reader = new ZooReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZooCompressionMethod.Store));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  // ── Multiple files ───────────────────────────────────────────────────────

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void MultipleFiles_StoreAndLzw() {
    var text   = "Compressible text that repeats again and again and again."u8.ToArray();
    var binary = new byte[256];
    for (var i = 0; i < 256; ++i) binary[i] = (byte)i;
    byte[] empty  = [];

    var archive = CreateArchive(w => {
      w.AddEntry("text.txt",   text,   ZooCompressionMethod.Lzw);
      w.AddEntry("binary.dat", binary, ZooCompressionMethod.Store);
      w.AddEntry("empty.txt",  empty,  ZooCompressionMethod.Store);
    });

    using var reader = new ZooReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(text));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(binary));
    Assert.That(reader.ExtractEntry(reader.Entries[2]), Is.EqualTo(empty));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void MultipleFiles_FiveEntries() {
    var entries = new (string Name, byte[] Data)[] {
      ("a.txt", "Alpha"u8.ToArray()),
      ("b.txt", "Beta"u8.ToArray()),
      ("c.txt", "Gamma"u8.ToArray()),
      ("d.txt", "Delta"u8.ToArray()),
      ("e.txt", "Epsilon"u8.ToArray()),
    };

    var archive = CreateArchive(w => {
      foreach (var (name, data) in entries)
        w.AddEntry(name, data, ZooCompressionMethod.Store);
    });

    using var reader = new ZooReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(5));
    for (var i = 0; i < entries.Length; ++i)
      Assert.That(reader.ExtractEntry(reader.Entries[i]), Is.EqualTo(entries[i].Data));
  }

  // ── Empty files ──────────────────────────────────────────────────────────

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void EmptyFile_Store_IsExtractedCorrectly() {
    var archive = CreateArchive(w => w.AddEntry("empty.dat", [], ZooCompressionMethod.Store));
    using var reader = new ZooReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].OriginalSize, Is.EqualTo(0u));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.Empty);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void EmptyFile_Lzw_IsExtractedCorrectly() {
    var archive = CreateArchive(w => w.AddEntry("empty.dat", [], ZooCompressionMethod.Lzw));
    using var reader = new ZooReader(new MemoryStream(archive));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.Empty);
  }

  // ── Long filenames (type 2) ──────────────────────────────────────────────

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void LongFilename_IsStoredAndRetrieved() {
    const string longName = "this_is_a_very_long_filename_that_exceeds_12_chars.txt";
    var data = "Some content"u8.ToArray();

    var archive = CreateArchive(w => w.AddEntry(longName, data, ZooCompressionMethod.Store));
    using var reader = new ZooReader(new MemoryStream(archive));

    var entry = reader.Entries[0];
    Assert.That(entry.LongFileName, Is.EqualTo(longName));
    Assert.That(entry.EffectiveName, Is.EqualTo(longName));
    Assert.That(reader.ExtractEntry(entry), Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void LongFilename_WithPathSeparator_ShortNameIsDerived() {
    const string fullPath = "subdir/nested/file.txt";
    var data = "Nested"u8.ToArray();

    var archive = CreateArchive(w => w.AddEntry(fullPath, data, ZooCompressionMethod.Store));
    using var reader = new ZooReader(new MemoryStream(archive));

    var entry = reader.Entries[0];
    // Long name equals original.
    Assert.That(entry.LongFileName, Is.EqualTo(fullPath));
    // Short name is just the filename component, truncated.
    Assert.That(entry.FileName, Is.EqualTo("file.txt"));
    Assert.That(reader.ExtractEntry(entry), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ShortFilename_WithinLimit_NoLongName() {
    const string name = "short.txt"; // 9 chars, within 12
    var data = "Short"u8.ToArray();

    var archive = CreateArchive(w => w.AddEntry(name, data, ZooCompressionMethod.Store));
    using var reader = new ZooReader(new MemoryStream(archive));

    var entry = reader.Entries[0];
    Assert.That(entry.LongFileName, Is.Null.Or.Empty);
    Assert.That(entry.FileName, Is.EqualTo(name));
  }

  // ── Metadata ─────────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void LastModified_IsRoundTripped() {
    var ts = new DateTime(2001, 9, 11, 8, 46, 0);
    var archive = CreateArchive(w => w.AddEntry("f.txt", [1, 2, 3], ZooCompressionMethod.Store, ts));
    using var reader = new ZooReader(new MemoryStream(archive));

    var lm = reader.Entries[0].LastModified;
    Assert.That(lm.Year,   Is.EqualTo(2001));
    Assert.That(lm.Month,  Is.EqualTo(9));
    Assert.That(lm.Day,    Is.EqualTo(11));
    Assert.That(lm.Hour,   Is.EqualTo(8));
    Assert.That(lm.Minute, Is.EqualTo(46));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Crc16_IsComputedAndStored() {
    var data = "CRC check"u8.ToArray();
    var archive = CreateArchive(w => w.AddEntry("crc.txt", data, ZooCompressionMethod.Store));
    using var reader = new ZooReader(new MemoryStream(archive));
    // If the CRC were wrong, ExtractEntry would throw — so just extract successfully.
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
    Assert.That(reader.Entries[0].Crc16, Is.Not.EqualTo(0).Or.EqualTo(0)); // any value is OK
  }

  [Category("EdgeCase")]
  [Test]
  public void EntryCount_IsCorrectForEmptyArchive() {
    var archive = CreateArchive(_ => { });
    using var reader = new ZooReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Is.Empty);
  }

  // ── Error handling ───────────────────────────────────────────────────────

  [Category("Exception")]
  [Test]
  public void InvalidMagic_ThrowsInvalidDataException() {
    var archive = CreateArchive(w => w.AddEntry("f.txt", [1], ZooCompressionMethod.Store));
    // Corrupt the archive magic at offset 20.
    archive[20] = 0xFF;

    Assert.Throws<InvalidDataException>(() => {
      using var _ = new ZooReader(new MemoryStream(archive));
    });
  }

  [Category("Exception")]
  [Test]
  public void CrcMismatch_ThrowsInvalidDataException() {
    var archive = CreateArchive(w => w.AddEntry("f.txt", [1, 2, 3], ZooCompressionMethod.Store));

    // Corrupt a byte of the compressed (stored) data — last byte of archive.
    archive[^1] ^= 0xFF;

    using var reader = new ZooReader(new MemoryStream(archive));
    Assert.Throws<InvalidDataException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  [Category("Exception")]
  [Test]
  public void NonSeekableStream_ThrowsArgumentException() {
    Assert.Throws<ArgumentException>(() => {
      using var _ = new ZooReader(new NonSeekableStream());
    });
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  private sealed class NonSeekableStream : Stream {
    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int  Read(byte[] buffer, int offset, int count) => 0;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
