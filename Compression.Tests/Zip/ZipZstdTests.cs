using FileFormat.Zip;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipZstdTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Zstd_SmallData() {
    // Use repetitive data so Zstd can actually compress it below the original size
    var data = new byte[256];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 4);

    var archive = CreateArchive("test.txt", data);

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Zstd));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Zstd_LargeRepetitive() {
    var pattern = "ABCDEFGHIJKLMNOP"u8.ToArray();
    var data = new byte[8192];
    for (var i = 0; i < data.Length; i++)
      data[i] = pattern[i % pattern.Length];

    var archive = CreateArchive("large.bin", data);

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Zstd));
    Assert.That(reader.Entries[0].CompressedSize, Is.LessThan(data.Length));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Zstd_FallsBackToStore() {
    byte[] data = [0x42];
    var archive = CreateArchive("tiny.bin", data);

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Store));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  private static byte[] CreateArchive(string name, byte[] data) {
    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true))
      writer.AddEntry(name, data, ZipCompressionMethod.Zstd);
    return ms.ToArray();
  }
}
