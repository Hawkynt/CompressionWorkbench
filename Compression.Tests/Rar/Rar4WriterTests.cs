using Compression.Core.Dictionary.Rar;
using FileFormat.Rar;

namespace Compression.Tests.Rar;

[TestFixture]
public class Rar4WriterTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Rar3Codec_Direct_RoundTrip() {
    byte[] data = new byte[1000];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    var encoder = new Rar3Encoder(20);
    byte[] compressed = encoder.Compress(data);

    var decoder = new Rar3Decoder();
    byte[] decompressed = decoder.Decompress(compressed, data.Length, 20);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Store_RoundTrip() {
    byte[] data = "Hello, RAR4 Store!"u8.ToArray();

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true, method: RarConstants.Rar4MethodStore))
      writer.AddFile("test.txt", data);

    ms.Position = 0;
    using var reader = new RarReader(ms, leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("test.txt"));
    byte[] extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Compressed_RoundTrip() {
    byte[] data = new byte[1000];
    for (int i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true, method: RarConstants.Rar4MethodNormal))
      writer.AddFile("pattern.bin", data);

    ms.Position = 0;
    using var reader = new RarReader(ms, leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    byte[] extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void MultipleFiles_RoundTrip() {
    byte[] d1 = "First file data"u8.ToArray();
    byte[] d2 = new byte[500];
    for (int i = 0; i < d2.Length; ++i) d2[i] = (byte)(i % 13);

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true)) {
      writer.AddFile("f1.txt", d1);
      writer.AddFile("f2.bin", d2);
    }

    ms.Position = 0;
    using var reader = new RarReader(ms, leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void EmptyFile_RoundTrip() {
    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true))
      writer.AddFile("empty.txt", []);

    ms.Position = 0;
    using var reader = new RarReader(ms, leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Extract(0), Is.Empty);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Solid_RoundTrip() {
    byte[] d1 = new byte[300];
    byte[] d2 = new byte[300];
    for (int i = 0; i < 300; ++i) { d1[i] = (byte)(i % 10); d2[i] = (byte)(i % 10); }

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true, solid: true)) {
      writer.AddFile("f1.bin", d1);
      writer.AddFile("f2.bin", d2);
    }

    ms.Position = 0;
    using var reader = new RarReader(ms, leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_Store_RoundTrip() {
    byte[] data = "RAR4 encrypted store test"u8.ToArray();

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true,
        method: RarConstants.Rar4MethodStore, password: "secret"))
      writer.AddFile("enc.txt", data);

    ms.Position = 0;
    using var reader = new RarReader(ms, password: "secret", leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].IsEncrypted, Is.True);
    byte[] extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_Compressed_RoundTrip() {
    byte[] data = new byte[500];
    for (int i = 0; i < data.Length; ++i) data[i] = (byte)(i % 10);

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true,
        method: RarConstants.Rar4MethodNormal, password: "pass123"))
      writer.AddFile("pattern.bin", data);

    ms.Position = 0;
    using var reader = new RarReader(ms, password: "pass123", leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].IsEncrypted, Is.True);
    byte[] extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Encrypted_MultipleFiles_RoundTrip() {
    byte[] d1 = "File one"u8.ToArray();
    byte[] d2 = new byte[300];
    for (int i = 0; i < d2.Length; ++i) d2[i] = (byte)(i % 7);

    using var ms = new MemoryStream();
    using (var writer = new Rar4Writer(ms, leaveOpen: true, password: "test")) {
      writer.AddFile("a.txt", d1);
      writer.AddFile("b.bin", d2);
    }

    ms.Position = 0;
    using var reader = new RarReader(ms, password: "test", leaveOpen: true);

    Assert.That(reader.IsRar4, Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void CreateSplit_RoundTrip() {
    byte[] d1 = new byte[100];
    byte[] d2 = new byte[200];
    for (int i = 0; i < d1.Length; ++i) d1[i] = (byte)(i % 10);
    for (int i = 0; i < d2.Length; ++i) d2[i] = (byte)(i % 7);

    byte[][] volumes = Rar4Writer.CreateSplit(
      maxVolumeSize: 150,
      entries: [("a.bin", d1), ("b.bin", d2)],
      method: RarConstants.Rar4MethodStore);

    Assert.That(volumes.Length, Is.GreaterThan(1));

    var streams = volumes.Select(v => (Stream)new MemoryStream(v)).ToArray();
    using var cs = new Compression.Core.Streams.ConcatenatedStream(streams);
    using var reader = new RarReader(cs, leaveOpen: true);

    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }
}
