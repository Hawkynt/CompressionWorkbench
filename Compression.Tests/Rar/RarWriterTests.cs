using FileFormat.Rar;

namespace Compression.Tests.Rar;

[TestFixture]
public class RarWriterTests {
  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Store_RoundTrip_SingleFile() {
    byte[] data = "Hello, RAR writer!"u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodStore))
        writer.AddFile("hello.txt", data);
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive));
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Store_RoundTrip_MultipleFiles() {
    byte[] d1 = "First file"u8.ToArray();
    byte[] d2 = "Second file content"u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodStore)) {
        writer.AddFile("a.txt", d1);
        writer.AddFile("b.txt", d2);
      }
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive));
    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Store_EmptyFile() {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodStore))
        writer.AddFile("empty.txt", ReadOnlySpan<byte>.Empty);
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive));
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].Size, Is.EqualTo(0));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encoder_Decoder_DirectRoundTrip() {
    // Test encoder/decoder at the core level, bypassing archive format
    byte[] data = new byte[200];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    var encoder = new Compression.Core.Dictionary.Rar.Rar5Encoder();
    byte[] compressed = encoder.Compress(data);

    var decoder = new Compression.Core.Dictionary.Rar.Rar5Decoder(128 * 1024);
    byte[] decompressed = decoder.Decompress(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Encoder_Decoder_LiteralsOnly() {
    // Very short data — all literals, no matches
    byte[] data = [1, 2, 3, 4, 5];

    var encoder = new Compression.Core.Dictionary.Rar.Rar5Encoder();
    byte[] compressed = encoder.Compress(data);

    var decoder = new Compression.Core.Dictionary.Rar.Rar5Decoder(128 * 1024);
    byte[] decompressed = decoder.Decompress(compressed, data.Length);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Compressed_RoundTrip() {
    // Repetitive data that compresses well
    byte[] data = new byte[4096];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodNormal))
        writer.AddFile("pattern.bin", data);
      archive = ms.ToArray();
    }

    // Verify the reader parses header correctly
    using var reader = new RarReader(new MemoryStream(archive));
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("pattern.bin"));
    Assert.That(reader.Entries[0].Size, Is.EqualTo(data.Length));

    // Also verify direct decompression of the compressed payload
    var encoder = new Compression.Core.Dictionary.Rar.Rar5Encoder();
    byte[] compressed = encoder.Compress(data);
    var decoder = new Compression.Core.Dictionary.Rar.Rar5Decoder(128 * 1024);
    byte[] directDecoded = decoder.Decompress(compressed, data.Length);
    Assert.That(directDecoded, Is.EqualTo(data), "Direct encode/decode failed");

    byte[] extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Compressed_FallsBackToStoreForIncompressible() {
    // Random data won't compress
    byte[] data = new byte[256];
    new Random(42).NextBytes(data);

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodNormal))
        writer.AddFile("random.bin", data);
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(RarConstants.MethodStore));
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Timestamp_Preserved() {
    var time = new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero);
    byte[] data = "timestamped"u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodStore))
        writer.AddFile("dated.txt", data, modifiedTime: time);
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].ModifiedTime, Is.Not.Null);
    Assert.That(reader.Entries[0].ModifiedTime!.Value.ToUnixTimeSeconds(),
      Is.EqualTo(time.ToUnixTimeSeconds()));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(50000)]
  [TestCase(100000)]
  public void Encoder_Decoder_LargeRepetitive(int size) {
    byte[] data = new byte[size];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 26 + 'A');

    var encoder = new Compression.Core.Dictionary.Rar.Rar5Encoder(1 << 18);
    byte[] compressed = encoder.Compress(data);

    var decoder = new Compression.Core.Dictionary.Rar.Rar5Decoder(1 << 18);
    byte[] decompressed = decoder.Decompress(compressed, data.Length);

    for (int i = 0; i < data.Length; ++i) {
      if (decompressed[i] != data[i]) {
        Assert.Fail($"Mismatch at index {i}/{size}: expected {data[i]}, got {decompressed[i]}");
        break;
      }
    }
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encoder_Decoder_LargeRandom() {
    // Random data — mostly literals, no matches. Tests Huffman table encoding for large data.
    byte[] data = new byte[50000];
    new Random(42).NextBytes(data);

    var encoder = new Compression.Core.Dictionary.Rar.Rar5Encoder(1 << 18);
    byte[] compressed = encoder.Compress(data);

    var decoder = new Compression.Core.Dictionary.Rar.Rar5Decoder(1 << 18);
    byte[] decompressed = decoder.Decompress(compressed, data.Length);

    for (int i = 0; i < data.Length; ++i) {
      if (decompressed[i] != data[i]) {
        Assert.Fail($"Mismatch at index {i}: expected {data[i]}, got {decompressed[i]}");
        break;
      }
    }
  }

  // ── Solid archive tests ─────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encoder_Decoder_Solid_DirectRoundTrip() {
    // Test solid encoder/decoder directly, bypassing the archive format
    byte[] d1 = new byte[200];
    byte[] d2 = new byte[200];
    for (int i = 0; i < 200; ++i) { d1[i] = (byte)(i % 10); d2[i] = (byte)(i % 10); }

    var encoder = new Compression.Core.Dictionary.Rar.Rar5Encoder(128 * 1024);
    byte[] c1 = encoder.Compress(d1);
    byte[] c2 = encoder.Compress(d2); // solid: encoder preserves window

    // Decoder: first file fresh, second file reuses decoder
    var decoder = new Compression.Core.Dictionary.Rar.Rar5Decoder(128 * 1024);
    byte[] r1 = decoder.Decompress(c1, d1.Length);
    byte[] r2 = decoder.Decompress(c2, d2.Length);

    Assert.That(r1, Is.EqualTo(d1), "First file mismatch");
    Assert.That(r2, Is.EqualTo(d2), "Second file mismatch (solid)");
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Solid_Store_RoundTrip() {
    byte[] d1 = "First solid file"u8.ToArray();
    byte[] d2 = "Second solid file"u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodStore, solid: true)) {
        writer.AddFile("a.txt", d1);
        writer.AddFile("b.txt", d2);
      }
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive));
    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Solid_Compressed_RoundTrip() {
    byte[] d1 = new byte[2000];
    byte[] d2 = new byte[2000];
    for (int i = 0; i < d1.Length; ++i) d1[i] = (byte)(i % 10);
    for (int i = 0; i < d2.Length; ++i) d2[i] = (byte)(i % 10);

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodNormal, solid: true)) {
        writer.AddFile("f1.bin", d1);
        writer.AddFile("f2.bin", d2);
      }
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive));
    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.Entries[1].IsSolid, Is.True);
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Solid_ThreeFiles_RoundTrip() {
    byte[] d1 = new byte[1000];
    byte[] d2 = new byte[1000];
    byte[] d3 = new byte[1000];
    for (int i = 0; i < 1000; ++i) {
      d1[i] = (byte)(i % 26 + 'A');
      d2[i] = (byte)(i % 26 + 'A'); // same pattern — solid benefits
      d3[i] = (byte)(i % 10 + '0');
    }

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodNormal, solid: true)) {
        writer.AddFile("a.txt", d1);
        writer.AddFile("b.txt", d2);
        writer.AddFile("c.txt", d3);
      }
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive));
    Assert.That(reader.Entries.Count, Is.EqualTo(3));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
    Assert.That(reader.Extract(2), Is.EqualTo(d3));
  }

  // ── Encryption tests ────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_Store_RoundTrip() {
    byte[] data = "Secret RAR data!"u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodStore, password: "testpass"))
        writer.AddFile("secret.txt", data);
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive), "testpass");
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].IsEncrypted, Is.True);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_Compressed_RoundTrip() {
    byte[] data = new byte[1000];
    for (int i = 0; i < data.Length; ++i) data[i] = (byte)(i % 10);

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodNormal, password: "pw123"))
        writer.AddFile("data.bin", data);
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive), "pw123");
    Assert.That(reader.Entries[0].IsEncrypted, Is.True);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_MultipleFiles_RoundTrip() {
    byte[] d1 = "first"u8.ToArray();
    byte[] d2 = "second file content"u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodStore, password: "multi")) {
        writer.AddFile("a.txt", d1);
        writer.AddFile("b.txt", d2);
      }
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive), "multi");
    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compressed_LargerFile() {
    // Test with a file large enough to exercise the dictionary well
    // Use dictionarySizeLog=18 (256KB) for the 200KB data
    byte[] data = new byte[200_000];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 26 + 'A');

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodNormal,
          dictionarySizeLog: 18))
        writer.AddFile("large.txt", data);
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive));
    byte[] extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RecoveryRecord_RoundTrip() {
    byte[] data1 = new byte[100];
    byte[] data2 = new byte[200];
    for (int i = 0; i < data1.Length; ++i) data1[i] = (byte)(i % 10);
    for (int i = 0; i < data2.Length; ++i) data2[i] = (byte)(i % 7);

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodStore,
        recoveryPercent: 10);
      writer.AddFile("a.txt", data1);
      writer.AddFile("b.txt", data2);
      writer.Finish();
      archive = ms.ToArray();
    }

    using var ms2 = new MemoryStream(archive);
    using var reader = new RarReader(ms2);
    Assert.That(reader.HasRecoveryRecord, Is.True, "Recovery record should be detected");
    Assert.That(reader.Extract(0), Is.EqualTo(data1));
    Assert.That(reader.Extract(1), Is.EqualTo(data2));
    Assert.That(reader.VerifyRecoveryRecord(), Is.True, "Recovery record should verify");
  }

  [Category("EdgeCase")]
  [Test]
  public void RecoveryRecord_NotPresent() {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using var writer = new RarWriter(ms, leaveOpen: true, method: RarConstants.MethodStore);
      writer.AddFile("a.txt", [1, 2, 3]);
      writer.Finish();
      archive = ms.ToArray();
    }

    using var reader = new RarReader(new MemoryStream(archive));
    Assert.That(reader.HasRecoveryRecord, Is.False);
  }
}
