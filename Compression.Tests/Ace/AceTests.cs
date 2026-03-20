using Compression.Core.Checksums;
using Compression.Core.Dictionary.Ace;
using Compression.Core.Entropy.GolombRice;
using FileFormat.Ace;
using CoreAceConstants = Compression.Core.Dictionary.Ace.AceConstants;

namespace Compression.Tests.Ace;

[TestFixture]
public class AceEncoderDecoderTests {
  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Store_RoundTrip() {
    var writer = new AceWriter();
    byte[] data = "Hello, ACE!"u8.ToArray();
    writer.AddFile("test.txt", data);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("test.txt"));
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compressed_RoundTrip() {
    var writer = new AceWriter();
    byte[] data = new byte[1000];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);
    writer.AddFile("pattern.bin", data);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void MultipleFiles_RoundTrip() {
    var writer = new AceWriter();
    byte[] data1 = "First file"u8.ToArray();
    byte[] data2 = new byte[500];
    for (int i = 0; i < data2.Length; ++i)
      data2[i] = (byte)(i % 13);

    writer.AddFile("file1.txt", data1);
    writer.AddFile("file2.bin", data2);

    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Ace20_LZ77Only_SameAsAce10() {
    // ACE 2.0 with no mode switches should produce same output as ACE 1.0
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    byte[] compressed = AceEncoder.EncodeBlock(data);
    byte[] decoded10 = AceDecoder.DecodeBlock(compressed, data.Length,
      AceConstants.DefaultDictBits, AceConstants.CompAce10);
    byte[] decoded20 = AceDecoder.DecodeBlock(compressed, data.Length,
      AceConstants.DefaultDictBits, AceConstants.CompAce20);

    Assert.That(decoded10, Is.EqualTo(data));
    Assert.That(decoded20, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void EmptyFile_RoundTrip() {
    var writer = new AceWriter();
    writer.AddFile("empty.txt", []);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms);

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].OriginalSize, Is.EqualTo(0));
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.Empty);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ArchiveComment_RoundTrip() {
    var writer = new AceWriter();
    writer.Comment = "Test archive";
    writer.AddFile("test.txt", "data"u8.ToArray());
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms);

    Assert.That(reader.Comment, Is.EqualTo("Test archive"));
  }
  // ── Encryption tests ──────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_Store_RoundTrip() {
    byte[] data = "Secret ACE data!"u8.ToArray();
    var writer = new AceWriter(password: "mypass");
    writer.AddFile("secret.txt", data);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms, password: "mypass");

    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].IsEncrypted, Is.True);
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_Compressed_RoundTrip() {
    byte[] data = new byte[1000];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    var writer = new AceWriter(password: "secret123");
    writer.AddFile("pattern.bin", data);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms, password: "secret123");

    Assert.That(reader.Entries[0].IsEncrypted, Is.True);
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_MultipleFiles_RoundTrip() {
    byte[] f1 = "first encrypted file"u8.ToArray();
    byte[] f2 = new byte[200];
    for (int i = 0; i < f2.Length; ++i) f2[i] = (byte)(i % 13);

    var writer = new AceWriter(password: "pw");
    writer.AddFile("f1.txt", f1);
    writer.AddFile("f2.bin", f2);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms, password: "pw");

    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(f1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(f2));
  }

  // ── Solid archive tests ─────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Solid_RoundTrip_MultipleFiles() {
    byte[] data1 = new byte[500];
    for (int i = 0; i < data1.Length; ++i) data1[i] = (byte)(i % 10);
    byte[] data2 = new byte[500];
    for (int i = 0; i < data2.Length; ++i) data2[i] = (byte)(i % 10); // same pattern — solid should help

    var writer = new AceWriter(solid: true);
    writer.AddFile("f1.bin", data1);
    writer.AddFile("f2.bin", data2);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms);

    Assert.That(reader.IsSolid, Is.True);
    Assert.That(reader.Entries.Count, Is.EqualTo(2));
    Assert.That(reader.Entries[1].IsSolid, Is.True);

    byte[] extracted1 = reader.ExtractEntry(reader.Entries[0]);
    byte[] extracted2 = reader.ExtractEntry(reader.Entries[1]);
    Assert.That(extracted1, Is.EqualTo(data1));
    Assert.That(extracted2, Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Solid_ThreeFiles_DifferentContent() {
    byte[] d1 = "AAABBBCCC"u8.ToArray();
    byte[] d2 = "DDDEEEFFF"u8.ToArray();
    byte[] d3 = "GGGHHHIII"u8.ToArray();

    var writer = new AceWriter(solid: true);
    writer.AddFile("a.txt", d1);
    writer.AddFile("b.txt", d2);
    writer.AddFile("c.txt", d3);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms);

    Assert.That(reader.IsSolid, Is.True);
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(d1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(d2));
    Assert.That(reader.ExtractEntry(reader.Entries[2]), Is.EqualTo(d3));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Solid_Encrypted_RoundTrip() {
    byte[] d1 = new byte[200];
    byte[] d2 = new byte[200];
    for (int i = 0; i < 200; ++i) { d1[i] = (byte)(i % 7); d2[i] = (byte)(i % 13); }

    var writer = new AceWriter(solid: true, password: "solidpass");
    writer.AddFile("s1.bin", d1);
    writer.AddFile("s2.bin", d2);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms, password: "solidpass");

    Assert.That(reader.IsSolid, Is.True);
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(d1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(d2));
  }

  [Category("Exception")]
  [Test]
  public void Encrypted_NoPassword_Throws() {
    byte[] data = "encrypted"u8.ToArray();
    var writer = new AceWriter(password: "pass");
    writer.AddFile("test.bin", data);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms);

    Assert.That(reader.Entries[0].IsEncrypted, Is.True);
    Assert.Throws<InvalidOperationException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RecoveryRecord_RoundTrip() {
    var writer = new AceWriter(recoveryRecord: true);
    byte[] data1 = new byte[100];
    byte[] data2 = new byte[200];
    for (int i = 0; i < data1.Length; ++i) data1[i] = (byte)(i % 10);
    for (int i = 0; i < data2.Length; ++i) data2[i] = (byte)(i % 7);
    writer.AddFile("a.txt", data1);
    writer.AddFile("b.txt", data2);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    using var reader = new AceReader(ms);

    Assert.That(reader.HasRecoveryRecord, Is.True);
    Assert.That(reader.VerifyRecoveryRecord(), Is.True);
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(data2));
  }

  [Category("EdgeCase")]
  [Test]
  public void RecoveryRecord_NotPresent() {
    var writer = new AceWriter();
    writer.AddFile("a.txt", [1, 2, 3]);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    using var reader = new AceReader(ms);

    Assert.That(reader.HasRecoveryRecord, Is.False);
    Assert.That(reader.VerifyRecoveryRecord(), Is.False);
  }

  // ── ACE 2.0 sub-mode tests ────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Ace20_Exe_RoundTrip() {
    // Simulated x86 code with E8/E9 call instructions
    byte[] data = new byte[500];
    var rng = new Random(42);
    rng.NextBytes(data);
    // Insert some E8 call instructions
    for (int i = 0; i < data.Length - 5; i += 20) {
      data[i] = 0xE8;
      data[i + 1] = (byte)(i & 0xFF);
      data[i + 2] = (byte)((i >> 8) & 0xFF);
      data[i + 3] = 0;
      data[i + 4] = 0;
    }

    var writer = new AceWriter(compressionType: CoreAceConstants.CompAce20, subMode: 1);
    writer.AddFile("code.bin", data);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms);
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Ace20_Delta_RoundTrip() {
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 3); // Smooth gradient — delta filter helps

    var writer = new AceWriter(compressionType: CoreAceConstants.CompAce20, subMode: 2);
    writer.AddFile("gradient.bin", data);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms);
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Ace20_Sound_RoundTrip() {
    // Simulated mono audio: sine wave quantized to bytes
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(128 + (int)(50 * Math.Sin(i * 0.1)));

    var writer = new AceWriter(compressionType: CoreAceConstants.CompAce20, subMode: 3);
    writer.AddFile("audio.raw", data);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms);
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Ace20_Pic_RoundTrip() {
    // Simulated 10x10 RGB image with smooth gradients
    int width = 10, bpp = 3;
    byte[] data = new byte[width * 10 * bpp];
    for (int y = 0; y < 10; ++y)
      for (int x = 0; x < width; ++x) {
        int idx = (y * width + x) * bpp;
        data[idx] = (byte)(x * 25); // R
        data[idx + 1] = (byte)(y * 25); // G
        data[idx + 2] = (byte)((x + y) * 12); // B
      }

    var writer = new AceWriter(compressionType: CoreAceConstants.CompAce20, subMode: 4);
    writer.AddFile("image.raw", data);
    byte[] archive = writer.ToArray();

    using var ms = new MemoryStream(archive);
    var reader = new AceReader(ms);
    byte[] extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Ace20_Sound_Stereo_RoundTrip() {
    // Stereo audio data — direct filter round-trip
    byte[] data = new byte[400];
    for (int i = 0; i < data.Length; i += 2) {
      data[i] = (byte)(128 + (int)(40 * Math.Sin(i * 0.05)));
      data[i + 1] = (byte)(128 + (int)(30 * Math.Cos(i * 0.07)));
    }

    byte[] encoded = AceSoundFilter.Encode(data, 2);
    byte[] decoded = AceSoundFilter.Decode(encoded, 2);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void SoundFilter_RoundTrip_Direct() {
    byte[] data = new byte[200];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(100 + (int)(30 * Math.Sin(i * 0.2)));

    byte[] encoded = AceSoundFilter.Encode(data, 1);
    byte[] decoded = AceSoundFilter.Decode(encoded, 1);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void PicFilter_RoundTrip_Direct() {
    int width = 8, bpp = 3;
    int stride = width * bpp;
    byte[] data = new byte[stride * 6];
    for (int y = 0; y < 6; ++y)
      for (int x = 0; x < width; ++x) {
        int idx = y * stride + x * bpp;
        data[idx] = (byte)(x * 30);
        data[idx + 1] = (byte)(y * 40);
        data[idx + 2] = (byte)128;
      }

    byte[] encoded = AcePicFilter.Encode(data, stride);
    byte[] decoded = AcePicFilter.Decode(encoded, stride);
    Assert.That(decoded, Is.EqualTo(data));
  }
}

[TestFixture]
public class Sha1Tests {
  [Category("ThemVsUs")]
  [Test]
  public void EmptyString() {
    byte[] hash = Sha1.Compute([]);
    // SHA-1("") = da39a3ee 5e6b4b0d 3255bfef 95601890 afd80709
    Assert.That(hash, Is.EqualTo(new byte[] {
      0xDA, 0x39, 0xA3, 0xEE, 0x5E, 0x6B, 0x4B, 0x0D,
      0x32, 0x55, 0xBF, 0xEF, 0x95, 0x60, 0x18, 0x90,
      0xAF, 0xD8, 0x07, 0x09 }));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Abc() {
    byte[] hash = Sha1.Compute("abc"u8);
    // SHA-1("abc") = a9993e36 4706816a ba3e2571 7850c26c 9cd0d89d
    Assert.That(hash, Is.EqualTo(new byte[] {
      0xA9, 0x99, 0x3E, 0x36, 0x47, 0x06, 0x81, 0x6A,
      0xBA, 0x3E, 0x25, 0x71, 0x78, 0x50, 0xC2, 0x6C,
      0x9C, 0xD0, 0xD8, 0x9D }));
  }

  [Category("ThemVsUs")]
  [Test]
  public void LongMessage() {
    // SHA-1("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq")
    byte[] hash = Sha1.Compute("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"u8);
    Assert.That(hash, Is.EqualTo(new byte[] {
      0x84, 0x98, 0x3E, 0x44, 0x1C, 0x3B, 0xD2, 0x6E,
      0xBA, 0xAE, 0x4A, 0xA1, 0xF9, 0x51, 0x29, 0xE5,
      0xE5, 0x46, 0x70, 0xF1 }));
  }
}

[TestFixture]
public class GolombRiceTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_K0() {
    var encoder = new GolombRiceEncoder(0);
    for (int i = 0; i < 10; ++i)
      encoder.Encode(i);
    byte[] encoded = encoder.ToArray();

    var decoder = new GolombRiceDecoder(encoded, 0);
    for (int i = 0; i < 10; ++i)
      Assert.That(decoder.Decode(), Is.EqualTo(i));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_K3() {
    var encoder = new GolombRiceEncoder(3);
    int[] values = [0, 1, 7, 8, 15, 16, 100];
    foreach (int v in values)
      encoder.Encode(v);
    byte[] encoded = encoder.ToArray();

    var decoder = new GolombRiceDecoder(encoded, 3);
    foreach (int v in values)
      Assert.That(decoder.Decode(), Is.EqualTo(v));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void SignedRoundTrip() {
    var encoder = new GolombRiceEncoder(2);
    int[] values = [0, -1, 1, -2, 2, -10, 10];
    foreach (int v in values)
      encoder.EncodeSigned(v);
    byte[] encoded = encoder.ToArray();

    var decoder = new GolombRiceDecoder(encoded, 2);
    foreach (int v in values)
      Assert.That(decoder.DecodeSigned(), Is.EqualTo(v));
  }
}
