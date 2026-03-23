using Compression.Core.Dictionary.Sqx;
using FileFormat.Sqx;

namespace Compression.Tests.Sqx;

[TestFixture]
public class SqxTests {
  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Store_RoundTrip() {
    var writer = new SqxWriter();
    var data = "Hello, SQX!"u8.ToArray();
    writer.AddFile("test.txt", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("test.txt"));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compressed_RoundTrip() {
    var writer = new SqxWriter();
    var data = new byte[1000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);
    writer.AddFile("pattern.bin", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void MultipleFiles_RoundTrip() {
    var writer = new SqxWriter();
    var data1 = "First file"u8.ToArray();
    var data2 = new byte[500];
    for (var i = 0; i < data2.Length; ++i)
      data2[i] = (byte)(i % 7);

    writer.AddFile("file1.txt", data1);
    writer.AddFile("file2.bin", data2);

    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(data2));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void EmptyFile_RoundTrip() {
    var writer = new SqxWriter();
    writer.AddFile("empty.txt", []);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].OriginalSize, Is.EqualTo(0));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.Empty);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_Store_RoundTrip() {
    var writer = new SqxWriter(password: "secret123");
    var data = "Hello, encrypted SQX!"u8.ToArray();
    writer.AddFile("test.txt", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms, password: "secret123");

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].IsEncrypted, Is.True);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_Compressed_RoundTrip() {
    var writer = new SqxWriter(password: "p@ssw0rd");
    var data = new byte[1000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);
    writer.AddFile("pattern.bin", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms, password: "p@ssw0rd");

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].IsEncrypted, Is.True);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_MultipleFiles_RoundTrip() {
    var writer = new SqxWriter(password: "test");
    var data1 = "First file"u8.ToArray();
    var data2 = new byte[500];
    for (var i = 0; i < data2.Length; ++i)
      data2[i] = (byte)(i % 7);

    writer.AddFile("file1.txt", data1);
    writer.AddFile("file2.bin", data2);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms, password: "test");

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(data2));
  }

  [Category("Exception")]
  [Test]
  public void Encrypted_NoPassword_Throws() {
    var writer = new SqxWriter(password: "secret");
    var data = "encrypted data"u8.ToArray();
    writer.AddFile("test.txt", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);

    Assert.That(reader.Entries[0].IsEncrypted, Is.True);
    Assert.Throws<InvalidOperationException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  [Category("EdgeCase")]
  [Test]
  public void Encrypted_EmptyFile_NotEncrypted() {
    var writer = new SqxWriter(password: "secret");
    writer.AddFile("empty.txt", []);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms, password: "secret");

    Assert.That(reader.Entries[0].IsEncrypted, Is.False);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.Empty);
  }

  // ── SQX Extended Mode Tests ────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Multimedia_RoundTrip() {
    // Smooth gradient data — benefits from delta coding
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 3);

    var writer = new SqxWriter(method: 0x80);
    writer.AddFile("gradient.bin", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Audio_RoundTrip() {
    // Simulated 8-bit audio: sine wave
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(128 + (int)(50 * Math.Sin(i * 0.1)));

    var writer = new SqxWriter(method: 0x05);
    writer.AddFile("audio.raw", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void LzhBcj_RoundTrip() {
    // Data with E8/E9 patterns
    var data = new byte[500];
    var rng = new Random(42);
    rng.NextBytes(data);
    for (var i = 0; i < data.Length - 5; i += 25) {
      data[i] = 0xE8;
      data[i + 1] = (byte)(i & 0xFF);
    }

    var writer = new SqxWriter(method: 0x81);
    writer.AddFile("code.bin", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void LzhDelta_RoundTrip() {
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 50);

    var writer = new SqxWriter(method: 0x82);
    writer.AddFile("delta.bin", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void MultimediaCodec_Direct_RoundTrip() {
    var data = new byte[200];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 7);

    var compressed = SqxMultimediaCodec.Encode(data);
    var decoded = SqxMultimediaCodec.Decode(compressed, data.Length);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void AudioCodec_Direct_RoundTrip() {
    var data = new byte[200];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(128 + (int)(40 * Math.Sin(i * 0.15)));

    var compressed = SqxAudioCodec.Encode(data);
    var decoded = SqxAudioCodec.Decode(compressed, data.Length);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Codec_DirectRoundTrip() {
    var data = new byte[100];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    var encoder = new SqxEncoder();
    var compressed = encoder.Encode(data);
    Assert.That(compressed.Length, Is.GreaterThan(0), "Compressed should not be empty");

    var decoder = new SqxDecoder();
    var decoded = decoder.Decode(compressed, data.Length);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Codec_Repetitive_RoundTrip() {
    // Same data as Compressed_RoundTrip but test codec directly
    var data = new byte[1000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    var compressed = SqxEncoder.Encode(data, SqxConstants.DefaultDictSize);
    Assert.That(compressed.Length, Is.GreaterThan(0), "Should produce output");

    var decoded = SqxDecoder.Decode(compressed, data.Length, SqxConstants.DefaultDictSize);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Codec_MediumRepetitive_RoundTrip() {
    // 200 bytes — between 100 (passing) and 1000 (failing)
    var data = new byte[200];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    var compressed = SqxEncoder.Encode(data, SqxConstants.DefaultDictSize);
    var decoded = SqxDecoder.Decode(compressed, data.Length, SqxConstants.DefaultDictSize);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Codec_LiteralsOnly_RoundTrip() {
    // Random data should mostly produce literals
    var data = new byte[50];
    new Random(42).NextBytes(data);

    var encoder = new SqxEncoder();
    var compressed = encoder.Encode(data);

    var decoder = new SqxDecoder();
    var decoded = decoder.Decode(compressed, data.Length);
    Assert.That(decoded, Is.EqualTo(data));
  }

  // ── Solid Mode Tests ─────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Solid_RoundTrip() {
    var writer = new SqxWriter(solid: true);
    var data1 = new byte[500];
    var data2 = new byte[500];
    for (var i = 0; i < 500; ++i) {
      data1[i] = (byte)(i % 13);
      data2[i] = (byte)(i % 13); // same pattern — solid should benefit
    }

    writer.AddFile("file1.bin", data1);
    writer.AddFile("file2.bin", data2);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);

    Assert.That(reader.IsSolid, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));

    var results = reader.ExtractAll();
    Assert.That(results[0], Is.EqualTo(data1));
    Assert.That(results[1], Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Solid_ThreeFiles_RoundTrip() {
    var writer = new SqxWriter(solid: true);
    var data1 = "Hello solid world"u8.ToArray();
    var data2 = new byte[300];
    var data3 = new byte[200];
    for (var i = 0; i < data2.Length; ++i) data2[i] = (byte)(i % 7);
    for (var i = 0; i < data3.Length; ++i) data3[i] = (byte)(i % 11);

    writer.AddFile("a.txt", data1);
    writer.AddFile("b.bin", data2);
    writer.AddFile("c.bin", data3);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);

    Assert.That(reader.IsSolid, Is.True);
    var results = reader.ExtractAll();
    Assert.That(results[0], Is.EqualTo(data1));
    Assert.That(results[1], Is.EqualTo(data2));
    Assert.That(results[2], Is.EqualTo(data3));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Solid_Encrypted_RoundTrip() {
    var writer = new SqxWriter(password: "solidpass", solid: true);
    var data1 = new byte[200];
    var data2 = new byte[200];
    for (var i = 0; i < 200; ++i) {
      data1[i] = (byte)(i % 5);
      data2[i] = (byte)(i % 9);
    }

    writer.AddFile("enc1.bin", data1);
    writer.AddFile("enc2.bin", data2);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms, password: "solidpass");

    Assert.That(reader.IsSolid, Is.True);
    var results = reader.ExtractAll();
    Assert.That(results[0], Is.EqualTo(data1));
    Assert.That(results[1], Is.EqualTo(data2));
  }

  // ── Recovery Record Tests ────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Recovery_VerifyIntact() {
    var writer = new SqxWriter(recoveryPercent: 10);
    var data = new byte[500];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 17);
    writer.AddFile("rec.bin", data);
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);

    Assert.That(reader.HasRecoveryRecord, Is.True);
    Assert.That(reader.VerifyRecoveryRecord(), Is.True);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Test]
  public void Recovery_DetectsCorruption() {
    var writer = new SqxWriter(recoveryPercent: 5);
    var data = new byte[300];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 23);
    writer.AddFile("corrupt.bin", data);
    var archive = writer.ToArray();

    // Corrupt a byte in the archive data
    archive[20] ^= 0xFF;

    using var ms = new MemoryStream(archive);
    // Re-parsing may fail due to CRC; just test VerifyRecoveryRecord detects corruption
    try {
      var reader = new SqxReader(ms);
      Assert.That(reader.HasRecoveryRecord, Is.True);
      Assert.That(reader.VerifyRecoveryRecord(), Is.False);
    }
    catch (InvalidDataException) {
      // Corruption in header area may prevent parsing — that's acceptable
      Assert.Pass("Archive corruption detected during parsing.");
    }
  }

  [Category("HappyPath")]
  [Test]
  public void NoRecovery_VerifyReturnsTrue() {
    var writer = new SqxWriter();
    writer.AddFile("norec.txt", "no recovery"u8.ToArray());
    var archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new SqxReader(ms);

    Assert.That(reader.HasRecoveryRecord, Is.False);
    Assert.That(reader.VerifyRecoveryRecord(), Is.True);
  }
}
