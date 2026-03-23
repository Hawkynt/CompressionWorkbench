using FileFormat.Arc;

namespace Compression.Tests.Arc;

[TestFixture]
public class ArcTests {
  // ── Helpers ─────────────────────────────────────────────────────────────────

  private static byte[] WriteAndRead(Action<ArcWriter> populate, string fileName) {
    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, leaveOpen: true))
      populate(writer);

    ms.Position = 0;
    using var reader = new ArcReader(ms, leaveOpen: true);
    var entry = reader.GetNextEntry();
    Assert.That(entry, Is.Not.Null);
    Assert.That(entry!.FileName, Is.EqualTo(fileName));
    return reader.ReadEntryData();
  }

  private static List<(string Name, byte[] Data)> WriteAndReadAll(Action<ArcWriter> populate) {
    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, leaveOpen: true))
      populate(writer);

    ms.Position = 0;
    using var reader = new ArcReader(ms, leaveOpen: true);
    var results = new List<(string, byte[])>();
    ArcEntry? entry;
    while ((entry = reader.GetNextEntry()) != null)
      results.Add((entry.FileName, reader.ReadEntryData()));

    return results;
  }

  // ── Stored (method 2) ───────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Stored_RoundTrip_SmallFile() {
    var original = "Hello, ARC!"u8.ToArray();
    var result = WriteAndRead(
      w => w.AddEntry("hello.txt", original, ArcCompressionMethod.Stored),
      "hello.txt");
    Assert.That(result, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Stored_RoundTrip_BinaryData() {
    var original = new byte[256];
    for (var i = 0; i < 256; ++i)
      original[i] = (byte)i;

    var result = WriteAndRead(
      w => w.AddEntry("bin.dat", original, ArcCompressionMethod.Stored),
      "bin.dat");
    Assert.That(result, Is.EqualTo(original));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Stored_RoundTrip_EmptyFile() {
    byte[] original = [];
    var result = WriteAndRead(
      w => w.AddEntry("empty.bin", original, ArcCompressionMethod.Stored),
      "empty.bin");
    Assert.That(result, Is.EqualTo(original));
  }

  // ── RLE / Packed (method 3) ─────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Packed_RoundTrip_RepetitiveData() {
    // Highly repetitive data should compress well with RLE.
    var original = new byte[200];
    Array.Fill<byte>(original, 0xAA, 0, 100);
    Array.Fill<byte>(original, 0xBB, 100, 100);

    var result = WriteAndRead(
      w => w.AddEntry("rle.dat", original, ArcCompressionMethod.Packed),
      "rle.dat");
    Assert.That(result, Is.EqualTo(original));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Packed_RoundTrip_ContainsRleMarkerByte() {
    // Data that contains 0x90 (the RLE escape byte) must be handled correctly.
    byte[] original = [0x90, 0x90, 0x90, 0x01, 0x02, 0x90, 0x03];

    var result = WriteAndRead(
      w => w.AddEntry("marker.bin", original, ArcCompressionMethod.Packed),
      "marker.bin");
    Assert.That(result, Is.EqualTo(original));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Packed_RoundTrip_OnlyMarkerBytes() {
    // A file consisting entirely of 0x90 bytes.
    var original = new byte[50];
    Array.Fill<byte>(original, 0x90);

    var result = WriteAndRead(
      w => w.AddEntry("markers.bin", original, ArcCompressionMethod.Packed),
      "markers.bin");
    Assert.That(result, Is.EqualTo(original));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Packed_RoundTrip_EmptyFile() {
    byte[] original = [];
    var result = WriteAndRead(
      w => w.AddEntry("empty.bin", original, ArcCompressionMethod.Packed),
      "empty.bin");
    Assert.That(result, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Packed_RoundTrip_MixedContent() {
    // Mix of runs and random bytes, including 0x90 in various positions.
    byte[] original = [
      0x01, 0x01, 0x01, 0x01,        // run of 4
      0x90,                           // escape byte
      0x02, 0x03, 0x04,               // random bytes
      0x90, 0x90,                     // two consecutive escapes
      0x05, 0x05, 0x05, 0x05, 0x05,  // run of 5
      0x90,                           // trailing escape
    ];

    var result = WriteAndRead(
      w => w.AddEntry("mixed.bin", original, ArcCompressionMethod.Packed),
      "mixed.bin");
    Assert.That(result, Is.EqualTo(original));
  }

  // ── Multiple files ──────────────────────────────────────────────────────────

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void MultipleFiles_RoundTrip() {
    var file1 = "First file content."u8.ToArray();
    var file2 = "Second file content."u8.ToArray();
    var file3 = new byte[100];
    Array.Fill<byte>(file3, 0xFF);

    var entries = WriteAndReadAll(w => {
      w.AddEntry("file1.txt", file1, ArcCompressionMethod.Stored);
      w.AddEntry("file2.txt", file2, ArcCompressionMethod.Stored);
      w.AddEntry("file3.bin", file3, ArcCompressionMethod.Packed);
    });

    Assert.That(entries, Has.Count.EqualTo(3));
    Assert.That(entries[0].Name, Is.EqualTo("file1.txt"));
    Assert.That(entries[0].Data, Is.EqualTo(file1));
    Assert.That(entries[1].Name, Is.EqualTo("file2.txt"));
    Assert.That(entries[1].Data, Is.EqualTo(file2));
    Assert.That(entries[2].Name, Is.EqualTo("file3.bin"));
    Assert.That(entries[2].Data, Is.EqualTo(file3));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void MultipleFiles_MixedMethods() {
    var stored = "No compression."u8.ToArray();
    var packed = new byte[60];
    Array.Fill<byte>(packed, 0xCC);

    var entries = WriteAndReadAll(w => {
      w.AddEntry("stored.txt", stored, ArcCompressionMethod.Stored);
      w.AddEntry("packed.bin", packed, ArcCompressionMethod.Packed);
    });

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Data, Is.EqualTo(stored));
    Assert.That(entries[1].Data, Is.EqualTo(packed));
  }

  // ── Squeezed (method 4) ────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Squeezed_RoundTrip_SmallText() {
    var original = "Hello, Squeezed ARC!"u8.ToArray();
    var result = WriteAndRead(
      w => w.AddEntry("sq.txt", original, ArcCompressionMethod.Squeezed),
      "sq.txt");
    Assert.That(result, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Squeezed_RoundTrip_RepetitiveData() {
    var original = new byte[200];
    Array.Fill<byte>(original, 0xAA, 0, 100);
    Array.Fill<byte>(original, 0xBB, 100, 100);
    var result = WriteAndRead(
      w => w.AddEntry("sq.dat", original, ArcCompressionMethod.Squeezed),
      "sq.dat");
    Assert.That(result, Is.EqualTo(original));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void Squeezed_RoundTrip_AllByteValues() {
    var original = new byte[256];
    for (var i = 0; i < 256; ++i)
      original[i] = (byte)i;
    var result = WriteAndRead(
      w => w.AddEntry("allbytes", original, ArcCompressionMethod.Squeezed),
      "allbytes");
    Assert.That(result, Is.EqualTo(original));
  }

  // ── Crunched5 (method 5) — LZW 9-12 bit + RLE ─────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Crunched5_RoundTrip_RepetitiveData() {
    var original = new byte[500];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i % 10);
    var result = WriteAndRead(
      w => w.AddEntry("c5.dat", original, ArcCompressionMethod.Crunched5),
      "c5.dat");
    Assert.That(result, Is.EqualTo(original));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Crunched5_RoundTrip_HighlyRepetitive() {
    var original = new byte[300];
    Array.Fill<byte>(original, 0xAA);
    var result = WriteAndRead(
      w => w.AddEntry("c5r.dat", original, ArcCompressionMethod.Crunched5),
      "c5r.dat");
    Assert.That(result, Is.EqualTo(original));
  }

  // ── Crunched6 (method 6) — LZW 9-12 bit, no clear code ─────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Crunched6_RoundTrip_RepetitiveData() {
    var original = new byte[500];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i % 10);
    var result = WriteAndRead(
      w => w.AddEntry("c6.dat", original, ArcCompressionMethod.Crunched6),
      "c6.dat");
    Assert.That(result, Is.EqualTo(original));
  }

  // ── Crunched7 (method 7) — LZW 9-12 bit + clear code ───────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Crunched7_RoundTrip_RepetitiveData() {
    var original = new byte[500];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i % 10);
    var result = WriteAndRead(
      w => w.AddEntry("c7.dat", original, ArcCompressionMethod.Crunched7),
      "c7.dat");
    Assert.That(result, Is.EqualTo(original));
  }

  // ── Crunched (method 8) — LZW ────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Crunched_RoundTrip_RepetitiveData() {
    var original = new byte[500];
    for (var i = 0; i < original.Length; ++i)
      original[i] = (byte)(i % 10);
    var result = WriteAndRead(
      w => w.AddEntry("lzw.dat", original, ArcCompressionMethod.Crunched),
      "lzw.dat");
    Assert.That(result, Is.EqualTo(original));
  }

  // ── ARC RLE unit tests ──────────────────────────────────────────────────────

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void ArcRle_Encode_Decode_RoundTrip_AllBytes() {
    // All 256 byte values, each appearing once.
    var original = new byte[256];
    for (var i = 0; i < 256; ++i)
      original[i] = (byte)i;

    var encoded = ArcRle.Encode(original);
    var decoded = ArcRle.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(original));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void ArcRle_Encode_Decode_LongRun() {
    // A run of 255 identical bytes (max per RLE code).
    var original = new byte[255];
    Array.Fill<byte>(original, 0x42);

    var encoded = ArcRle.Encode(original);
    var decoded = ArcRle.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(original));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void ArcRle_Encode_Decode_RunOf256() {
    // A run longer than 255 must be split into multiple RLE codes.
    var original = new byte[256];
    Array.Fill<byte>(original, 0x42);

    var encoded = ArcRle.Encode(original);
    var decoded = ArcRle.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(original));
  }

  [Category("EdgeCase")]
  [Test]
  public void ArcRle_Decode_LiteralMarker() {
    // 0x90 0x00 should decode to a single 0x90 byte.
    byte[] encoded = [0x90, 0x00];
    var decoded = ArcRle.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(new byte[] { 0x90 }));
  }

  [Category("HappyPath")]
  [Test]
  public void ArcRle_Decode_RunAfterNonMarker() {
    // byte, 0x90, count — run of 'count' copies of byte.
    byte[] encoded = [0x41, 0x90, 0x04]; // 'A' repeated 4 times
    var decoded = ArcRle.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(new byte[] { 0x41, 0x41, 0x41, 0x41 }));
  }

  // ── Header metadata ─────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Entry_StoresMethod_Correctly() {
    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, leaveOpen: true))
      writer.AddEntry("test.bin", [0x01, 0x02, 0x03], ArcCompressionMethod.Stored);

    ms.Position = 0;
    using var reader = new ArcReader(ms, leaveOpen: true);
    var entry = reader.GetNextEntry();
    Assert.That(entry, Is.Not.Null);
    Assert.That(entry!.Method, Is.EqualTo(ArcConstants.MethodStored));
  }

  [Category("HappyPath")]
  [Test]
  public void Entry_StoresOriginalSize_Correctly() {
    var data = new byte[42];
    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, leaveOpen: true))
      writer.AddEntry("size.bin", data, ArcCompressionMethod.Stored);

    ms.Position = 0;
    using var reader = new ArcReader(ms, leaveOpen: true);
    var entry = reader.GetNextEntry();
    Assert.That(entry, Is.Not.Null);
    Assert.That(entry!.OriginalSize, Is.EqualTo(42u));
  }

  [Category("Boundary")]
  [Test]
  public void Entry_FileNameTruncatedTo12Chars() {
    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, leaveOpen: true))
      writer.AddEntry("verylongfilename.txt", [], ArcCompressionMethod.Stored);

    ms.Position = 0;
    using var reader = new ArcReader(ms, leaveOpen: true);
    var entry = reader.GetNextEntry();
    Assert.That(entry, Is.Not.Null);
    Assert.That(entry!.FileName.Length, Is.LessThanOrEqualTo(12));
  }

  [Category("Boundary")]
  [Test]
  public void EndOfArchive_ReturnsNull() {
    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, leaveOpen: true))
      writer.AddEntry("a.txt", "a"u8.ToArray(), ArcCompressionMethod.Stored);

    ms.Position = 0;
    using var reader = new ArcReader(ms, leaveOpen: true);
    _ = reader.GetNextEntry();
    _ = reader.ReadEntryData();
    var next = reader.GetNextEntry();
    Assert.That(next, Is.Null);
  }

  // ── Default method ──────────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void DefaultMethod_Stored_UsedWhenNoMethodSpecified() {
    var data = "default"u8.ToArray();
    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, ArcCompressionMethod.Stored, leaveOpen: true))
      writer.AddEntry("d.txt", data);

    ms.Position = 0;
    using var reader = new ArcReader(ms, leaveOpen: true);
    var entry = reader.GetNextEntry();
    Assert.That(entry, Is.Not.Null);
    Assert.That(entry!.Method, Is.EqualTo(ArcConstants.MethodStored));
    Assert.That(reader.ReadEntryData(), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void DefaultMethod_Packed_UsedWhenNoMethodSpecified() {
    var data = new byte[80];
    Array.Fill<byte>(data, 0xDD);

    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, ArcCompressionMethod.Packed, leaveOpen: true))
      writer.AddEntry("p.bin", data);

    ms.Position = 0;
    using var reader = new ArcReader(ms, leaveOpen: true);
    var entry = reader.GetNextEntry();
    Assert.That(entry, Is.Not.Null);
    // RLE should have compressed this; method stays Packed.
    Assert.That(entry!.Method, Is.EqualTo(ArcConstants.MethodPacked));
    Assert.That(reader.ReadEntryData(), Is.EqualTo(data));
  }

  // ── CRC validation ──────────────────────────────────────────────────────────

  [Category("Exception")]
  [Test]
  public void Reader_ThrowsOnCrcMismatch() {
    var data = "CRC test"u8.ToArray();
    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, leaveOpen: true))
      writer.AddEntry("crc.txt", data, ArcCompressionMethod.Stored);

    // Corrupt one byte in the data section (after the 29-byte header).
    var archive = ms.ToArray();
    archive[29] ^= 0xFF;

    using var corruptMs = new MemoryStream(archive);
    using var reader = new ArcReader(corruptMs, leaveOpen: true);
    _ = reader.GetNextEntry();
    Assert.That(() => reader.ReadEntryData(), Throws.InstanceOf<InvalidDataException>());
  }
}
