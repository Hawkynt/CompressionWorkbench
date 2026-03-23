using FileFormat.Arj;

namespace Compression.Tests.Arj;

[TestFixture]
public sealed class ArjTests {
  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  private static byte[] WriteArj(Action<ArjWriter> configure) {
    var writer = new ArjWriter();
    configure(writer);
    return writer.ToArray();
  }

  private static ArjReader OpenArj(byte[] data) =>
    new(new MemoryStream(data), leaveOpen: false);

  // -------------------------------------------------------------------------
  // Round-trip: single stored file
  // -------------------------------------------------------------------------

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void SingleFile_RoundTrip() {
    var original = "Hello, ARJ!"u8.ToArray();

    var archive = WriteArj(w => w.AddFile("hello.txt", original));
    using var reader = OpenArj(archive);

    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("hello.txt"));
    Assert.That(reader.Entries[0].OriginalSize, Is.EqualTo((uint)original.Length));
    Assert.That(reader.Entries[0].Method, Is.EqualTo(ArjConstants.MethodStore));

    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(original));
  }

  // -------------------------------------------------------------------------
  // Round-trip: multiple stored files
  // -------------------------------------------------------------------------

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void MultipleFiles_RoundTrip() {
    var file1 = "First file content."u8.ToArray();
    var file2 = "Second file has different content!"u8.ToArray();
    var file3 = new byte[512];
    new Random(42).NextBytes(file3);

    var archive = WriteArj(w => {
      w.AddFile("file1.txt", file1);
      w.AddFile("file2.txt", file2);
      w.AddFile("binary.bin", file3);
    });

    using var reader = OpenArj(archive);

    Assert.That(reader.Entries.Count, Is.EqualTo(3));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("file1.txt"));
    Assert.That(reader.Entries[1].FileName, Is.EqualTo("file2.txt"));
    Assert.That(reader.Entries[2].FileName, Is.EqualTo("binary.bin"));

    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(file1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(file2));
    Assert.That(reader.ExtractEntry(reader.Entries[2]), Is.EqualTo(file3));
  }

  // -------------------------------------------------------------------------
  // Round-trip: empty file
  // -------------------------------------------------------------------------

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void EmptyFile_RoundTrip() {
    var archive = WriteArj(w => w.AddFile("empty.txt", []));
    using var reader = OpenArj(archive);

    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].OriginalSize, Is.EqualTo(0u));
    Assert.That(reader.Entries[0].CompressedSize, Is.EqualTo(0u));

    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.Empty);
  }

  // -------------------------------------------------------------------------
  // CRC-32 integrity
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Crc32_IsStored_And_Verified() {
    var data = "CRC verification test data."u8.ToArray();
    var archive = WriteArj(w => w.AddFile("crc.txt", data));

    using var reader = OpenArj(archive);
    var entry = reader.Entries[0];

    // The stored CRC-32 must be non-zero for non-empty data.
    Assert.That(entry.Crc32, Is.Not.EqualTo(0u));

    // Extraction must succeed (internal CRC check passes).
    var extracted = reader.ExtractEntry(entry);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void Crc32_Mismatch_Throws() {
    var data = "some data"u8.ToArray();
    var archive = WriteArj(w => w.AddFile("x.txt", data));

    // The archive ends with: [data bytes] [end-of-archive: 4 bytes].
    // Corrupt the last byte of the actual file data.
    var dataEnd = archive.Length - 4; // 4 = end-of-archive marker (2-byte ID + 2-byte size 0)
    archive[dataEnd - 1] ^= 0xFF;

    // Re-open so the reader sees the intact headers but corrupted data.
    using var reader = OpenArj(archive);
    Assert.Throws<InvalidDataException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  // -------------------------------------------------------------------------
  // Directory entries
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void DirectoryEntry_RoundTrip() {
    var archive = WriteArj(w => {
      w.AddDirectory("mydir");
      w.AddFile("mydir/file.txt", "nested content"u8.ToArray());
    });

    using var reader = OpenArj(archive);

    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("mydir"));
    Assert.That(reader.Entries[0].IsDirectory, Is.True);
    Assert.That(reader.Entries[0].FileType, Is.EqualTo(ArjConstants.FileTypeDirectory));
    Assert.That(reader.Entries[1].IsDirectory, Is.False);
  }

  // -------------------------------------------------------------------------
  // Archive comment
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Test]
  public void ArchiveComment_IsWritten_AndReadable() {
    var writer = new ArjWriter { ArchiveComment = "Test archive" };
    writer.AddFile("a.txt", "data"u8.ToArray());
    var archive = writer.ToArray();

    // The archive must be parseable (main header with comment is consumed silently).
    using var reader = OpenArj(archive);
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
  }

  // -------------------------------------------------------------------------
  // Header magic validation
  // -------------------------------------------------------------------------

  [Category("Exception")]
  [Test]
  public void InvalidMagic_Throws() {
    var bad = new byte[64];
    bad[0] = 0xFF;
    bad[1] = 0xFF;

    Assert.Throws<InvalidDataException>(() => _ = OpenArj(bad));
  }

  // -------------------------------------------------------------------------
  // Metadata
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void LastModified_RoundTrips() {
    var date = new DateTime(2023, 11, 5, 14, 30, 0);
    var archive = WriteArj(w => w.AddFile("dated.txt", "x"u8.ToArray(), lastModified: date));

    using var reader = OpenArj(archive);
    var lm = reader.Entries[0].LastModified;

    Assert.That(lm.Year,   Is.EqualTo(2023));
    Assert.That(lm.Month,  Is.EqualTo(11));
    Assert.That(lm.Day,    Is.EqualTo(5));
    Assert.That(lm.Hour,   Is.EqualTo(14));
    Assert.That(lm.Minute, Is.EqualTo(30));
  }

  [Category("HappyPath")]
  [Test]
  public void HostOs_IsSetToDos() {
    var archive = WriteArj(w => w.AddFile("x.txt", "x"u8.ToArray()));
    using var reader = OpenArj(archive);

    Assert.That(reader.Entries[0].HostOs, Is.EqualTo(ArjConstants.OsDos));
  }

  // -------------------------------------------------------------------------
  // Unsupported method
  // -------------------------------------------------------------------------

  [Category("Exception")]
  [Test]
  public void UnsupportedMethod_Throws() {
    // Build a valid archive, then patch method byte to an unsupported value.
    var archive = WriteArj(w => w.AddFile("x.txt", "x"u8.ToArray()));
    using var reader = OpenArj(archive);
    var entry = reader.Entries[0];

    // Manually override the method on the parsed entry.
    entry.Method = 99; // not a valid method

    Assert.Throws<NotSupportedException>(() => reader.ExtractEntry(entry));
  }

  // -------------------------------------------------------------------------
  // Large file
  // -------------------------------------------------------------------------

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void LargeFile_RoundTrip() {
    var original = new byte[64 * 1024];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i % 251);

    var archive = WriteArj(w => w.AddFile("large.bin", original));
    using var reader = OpenArj(archive);

    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(original));
  }

  // -------------------------------------------------------------------------
  // Compressed round-trips (methods 1-3)
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compressed_Method1_RoundTrip() {
    var data = new byte[2000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 13);

    var writer = new ArjWriter(ArjConstants.MethodCompressed1);
    writer.AddFile("compressed.bin", data);
    var archive = writer.ToArray();

    using var reader = OpenArj(archive);
    Assert.That(reader.Entries[0].Method, Is.EqualTo(ArjConstants.MethodCompressed1));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compressed_Method2_RoundTrip() {
    var data = new byte[1000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 7);

    var writer = new ArjWriter(ArjConstants.MethodCompressed2);
    writer.AddFile("m2.bin", data);
    var archive = writer.ToArray();

    using var reader = OpenArj(archive);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compressed_Text_RoundTrip() {
    var data = "Hello, ARJ compression! This text should compress well with LZSS and Huffman."u8.ToArray();

    var writer = new ArjWriter(ArjConstants.MethodCompressed1);
    writer.AddFile("text.txt", data);
    var archive = writer.ToArray();

    using var reader = OpenArj(archive);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Compressed_FallsBackToStore_WhenNotSmaller() {
    var rng = new Random(42);
    var data = new byte[32];
    rng.NextBytes(data);

    var writer = new ArjWriter(ArjConstants.MethodCompressed1);
    writer.AddFile("random.bin", data);
    var archive = writer.ToArray();

    using var reader = OpenArj(archive);
    // Random data shouldn't compress, so method should fall back to Store
    Assert.That(reader.Entries[0].Method, Is.EqualTo(ArjConstants.MethodStore));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  // -------------------------------------------------------------------------
  // Garble (encryption) round-trips
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Garble_Store_RoundTrip() {
    var data = "Secret ARJ data!"u8.ToArray();
    const string password = "mypassword";

    var writer = new ArjWriter(ArjConstants.MethodStore, password);
    writer.AddFile("secret.txt", data);
    var archive = writer.ToArray();

    using var reader = new ArjReader(new MemoryStream(archive), password);
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That((reader.Entries[0].Flags & ArjConstants.FlagGarbled), Is.Not.EqualTo(0));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Garble_Compressed_RoundTrip() {
    var data = new byte[2000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 13);
    const string password = "p@ss!";

    var writer = new ArjWriter(ArjConstants.MethodCompressed1, password);
    writer.AddFile("comp.bin", data);
    var archive = writer.ToArray();

    using var reader = new ArjReader(new MemoryStream(archive), password);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void Garble_WrongPassword_ProducesBadData() {
    var data = "test data"u8.ToArray();

    var writer = new ArjWriter(ArjConstants.MethodStore, "correct");
    writer.AddFile("x.txt", data);
    var archive = writer.ToArray();

    using var reader = new ArjReader(new MemoryStream(archive), "wrong");
    // CRC-32 should fail because the wrong password produces wrong data
    Assert.Throws<InvalidDataException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  [Category("Exception")]
  [Test]
  public void Garble_NoPassword_ThrowsOnExtract() {
    var data = "encrypted"u8.ToArray();

    var writer = new ArjWriter(ArjConstants.MethodStore, "secret");
    writer.AddFile("x.txt", data);
    var archive = writer.ToArray();

    // Open without password
    using var reader = new ArjReader(new MemoryStream(archive));
    Assert.Throws<InvalidOperationException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  // -------------------------------------------------------------------------
  // End-of-archive marker
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Test]
  public void EndOfArchiveMarker_IsPresent() {
    var archive = WriteArj(w => w.AddFile("x.txt", "x"u8.ToArray()));

    // The last four bytes must be the ARJ header ID followed by 0x0000.
    var eaLo = archive[^4];
    var eaHi = archive[^3];
    var szLo = archive[^2];
    var szHi = archive[^1];

    var id = (ushort)(eaLo | (eaHi << 8));
    var sz = (ushort)(szLo | (szHi << 8));

    Assert.That(id, Is.EqualTo(ArjConstants.HeaderId));
    Assert.That(sz, Is.EqualTo(0));
  }
}
