using FileFormat.Zip;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipRoundTripTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void EmptyData_Store() {
    RoundTrip([], ZipCompressionMethod.Store);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void EmptyData_Deflate() {
    RoundTrip([], ZipCompressionMethod.Deflate);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void SingleByte_Store() {
    RoundTrip([42], ZipCompressionMethod.Store);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void SingleByte_Deflate() {
    RoundTrip([42], ZipCompressionMethod.Deflate);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ShortText() {
    RoundTrip("Hello, World!"u8.ToArray(), ZipCompressionMethod.Deflate);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RepeatedText() {
    var pattern = "ABCDEFGHIJ"u8.ToArray();
    var data = new byte[pattern.Length * 500];
    for (var i = 0; i < 500; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);
    RoundTrip(data, ZipCompressionMethod.Deflate);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void AllZeros_32KB() {
    var data = new byte[32768];
    RoundTrip(data, ZipCompressionMethod.Deflate);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RandomData_4KB() {
    var rng = new Random(123);
    var data = new byte[4096];
    rng.NextBytes(data);
    RoundTrip(data, ZipCompressionMethod.Deflate);
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void BoundarySize_32767() {
    var rng = new Random(1);
    var data = new byte[32767];
    rng.NextBytes(data);
    RoundTrip(data, ZipCompressionMethod.Deflate);
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void BoundarySize_32769() {
    var rng = new Random(2);
    var data = new byte[32769];
    rng.NextBytes(data);
    RoundTrip(data, ZipCompressionMethod.Deflate);
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void BoundarySize_65536() {
    var pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    var data = new byte[65536];
    for (var i = 0; i < data.Length; ++i)
      data[i] = pattern[i % pattern.Length];
    RoundTrip(data, ZipCompressionMethod.Deflate);
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void MultipleFiles_MixedMethods() {
    var text = "Compressible text that should work well with deflate compression."u8.ToArray();
    var binary = new byte[256];
    for (var i = 0; i < 256; ++i) binary[i] = (byte)i;
    byte[] empty = [];

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.AddEntry("text.txt", text, ZipCompressionMethod.Deflate);
        writer.AddEntry("binary.dat", binary, ZipCompressionMethod.Store);
        writer.AddEntry("empty.txt", empty, ZipCompressionMethod.Store);
        writer.AddDirectory("subdir");
        writer.AddEntry("subdir/nested.txt", text, ZipCompressionMethod.Deflate);
      }
      archive = ms.ToArray();
    }

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(5));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(text));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(binary));
    Assert.That(reader.ExtractEntry(reader.Entries[2]), Is.EqualTo(empty));
    Assert.That(reader.Entries[3].IsDirectory, Is.True);
    Assert.That(reader.ExtractEntry(reader.Entries[4]), Is.EqualTo(text));
  }

  private static void RoundTrip(byte[] data, ZipCompressionMethod method) {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.AddEntry("test.dat", data, method);
      }
      archive = ms.ToArray();
    }

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    var extracted = reader.ExtractEntry(reader.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }
}
