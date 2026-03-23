using System.Text;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipEncryptionTests {

  // ── WinZip AES-256 ──────────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Aes256_Store() {
    var data = Encoding.UTF8.GetBytes("Hello, encrypted world!");
    const string password = "testpassword123";

    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true, password: password,
        encryptionMethod: ZipEncryptionMethod.Aes256))
      writer.AddEntry("hello.txt", data, ZipCompressionMethod.Store);

    ms.Position = 0;
    using var reader = new ZipReader(ms, password: password);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.WinZipAes));

    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Aes256_Deflate() {
    var data = Encoding.UTF8.GetBytes(new string('A', 1000) + "unique tail");
    const string password = "s3cureP@ss!";

    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true, password: password))
      writer.AddEntry("compressed.txt", data, ZipCompressionMethod.Deflate);

    ms.Position = 0;
    using var reader = new ZipReader(ms, password: password);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Aes256_MultipleEntries() {
    var file1 = Encoding.UTF8.GetBytes("First file content");
    var file2 = Encoding.UTF8.GetBytes("Second file with more data: " + new string('X', 500));
    const string password = "multi-file-pw";

    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true, password: password)) {
      writer.AddEntry("file1.txt", file1, ZipCompressionMethod.Store);
      writer.AddEntry("file2.txt", file2, ZipCompressionMethod.Deflate);
    }

    ms.Position = 0;
    using var reader = new ZipReader(ms, password: password);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));

    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(file1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(file2));
  }

  [Category("Exception")]
  [Test]
  public void Aes256_WrongPassword_Throws() {
    var data = "secret data"u8.ToArray();
    const string password = "correct";

    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true, password: password))
      writer.AddEntry("secret.txt", data, ZipCompressionMethod.Store);

    ms.Position = 0;
    using var reader = new ZipReader(ms, password: "wrong");
    Assert.Throws<InvalidDataException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  [Category("Exception")]
  [Test]
  public void Aes256_NoPassword_Throws() {
    var data = "encrypted"u8.ToArray();
    const string password = "mypassword";

    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true, password: password))
      writer.AddEntry("file.bin", data, ZipCompressionMethod.Store);

    ms.Position = 0;
    using var reader = new ZipReader(ms);
    Assert.Throws<InvalidOperationException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Aes256_EmptyFile() {
    byte[] data = [];
    const string password = "emptypass";

    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true, password: password))
      writer.AddEntry("empty.bin", data, ZipCompressionMethod.Store);

    ms.Position = 0;
    using var reader = new ZipReader(ms, password: password);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.Empty);
  }

  // ── Traditional PKZIP ──────────────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_PkzipTraditional_Store() {
    var data = Encoding.UTF8.GetBytes("Traditional encryption test");
    const string password = "oldschool";

    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true, password: password,
        encryptionMethod: ZipEncryptionMethod.PkzipTraditional))
      writer.AddEntry("trad.txt", data, ZipCompressionMethod.Store);

    ms.Position = 0;
    using var reader = new ZipReader(ms, password: password);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_PkzipTraditional_Deflate() {
    var data = Encoding.UTF8.GetBytes(new string('B', 500) + "deflated and encrypted");
    const string password = "trad-deflate";

    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true, password: password,
        encryptionMethod: ZipEncryptionMethod.PkzipTraditional))
      writer.AddEntry("comp.txt", data, ZipCompressionMethod.Deflate);

    ms.Position = 0;
    using var reader = new ZipReader(ms, password: password);
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void PkzipTraditional_WrongPassword_Throws() {
    var data = "secret"u8.ToArray();

    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true, password: "right",
        encryptionMethod: ZipEncryptionMethod.PkzipTraditional))
      writer.AddEntry("s.txt", data, ZipCompressionMethod.Store);

    ms.Position = 0;
    using var reader = new ZipReader(ms, password: "wrong");
    Assert.Throws<InvalidDataException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  // ── No encryption (unaffected) ─────────────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void NoPassword_WriterProducesUnencryptedArchive() {
    var data = "plain text"u8.ToArray();

    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true))
      writer.AddEntry("plain.txt", data, ZipCompressionMethod.Store);

    ms.Position = 0;
    using var reader = new ZipReader(ms);
    Assert.That(reader.Entries[0].IsEncrypted, Is.False);
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Store));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }
}
