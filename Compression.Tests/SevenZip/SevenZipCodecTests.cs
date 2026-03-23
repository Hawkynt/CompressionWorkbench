using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

[TestFixture]
public class SevenZipCodecTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Lzma2_StillWorks() {
    // Basic sanity — existing LZMA2 round-trip shouldn't break
    var data = "Hello, 7z multi-coder world!"u8.ToArray();
    var archive = CreateArchive(data, SevenZipCodec.Lzma2);
    var extracted = ExtractFirst(archive);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MultipleFiles_Lzma2() {
    var data1 = "First file content."u8.ToArray();
    var data2 = "Second file content, a bit longer."u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
        writer.AddEntry(new SevenZipEntry { Name = "file1.txt" }, data1);
        writer.AddEntry(new SevenZipEntry { Name = "file2.txt" }, data2);
      }
      archive = ms.ToArray();
    }

    using var reader = new SevenZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(data1));
    Assert.That(reader.Extract(1), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Lzma_SingleFile() {
    var data = "Hello from LZMA codec in 7z!"u8.ToArray();
    var archive = CreateArchive(data, SevenZipCodec.Lzma);
    var extracted = ExtractFirst(archive);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Lzma_MultipleFiles() {
    var data1 = "LZMA file one."u8.ToArray();
    var data2 = "LZMA file two with more content."u8.ToArray();

    var archive = CreateMultiFileArchive(SevenZipCodec.Lzma, data1, data2);
    using var reader = new SevenZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(data1));
    Assert.That(reader.Extract(1), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Deflate_SingleFile() {
    var data = "Deflate compressed inside 7z!"u8.ToArray();
    var archive = CreateArchive(data, SevenZipCodec.Deflate);
    var extracted = ExtractFirst(archive);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Deflate_MultipleFiles() {
    var data1 = "Deflate file one."u8.ToArray();
    var data2 = "Deflate file two content."u8.ToArray();

    var archive = CreateMultiFileArchive(SevenZipCodec.Deflate, data1, data2);
    using var reader = new SevenZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(data1));
    Assert.That(reader.Extract(1), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_BZip2_SingleFile() {
    var data = "BZip2 compressed inside 7z!"u8.ToArray();
    var archive = CreateArchive(data, SevenZipCodec.BZip2);
    var extracted = ExtractFirst(archive);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_BZip2_MultipleFiles() {
    var data1 = "BZip2 file one."u8.ToArray();
    var data2 = "BZip2 file two with more data."u8.ToArray();

    var archive = CreateMultiFileArchive(SevenZipCodec.BZip2, data1, data2);
    using var reader = new SevenZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(data1));
    Assert.That(reader.Extract(1), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_PPMd_SingleFile() {
    var data = "PPMd compressed inside 7z!"u8.ToArray();
    var archive = CreateArchive(data, SevenZipCodec.PPMd);
    var extracted = ExtractFirst(archive);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_PPMd_MultipleFiles() {
    var data1 = "PPMd file one."u8.ToArray();
    var data2 = "PPMd file two with more data."u8.ToArray();

    var archive = CreateMultiFileArchive(SevenZipCodec.PPMd, data1, data2);
    using var reader = new SevenZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Extract(0), Is.EqualTo(data1));
    Assert.That(reader.Extract(1), Is.EqualTo(data2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Lzma_LargeRepetitiveData() {
    var data = new byte[10_000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 7);

    var archive = CreateArchive(data, SevenZipCodec.Lzma);
    var extracted = ExtractFirst(archive);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Deflate_LargeRepetitiveData() {
    var data = new byte[10_000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 7);

    var archive = CreateArchive(data, SevenZipCodec.Deflate);
    var extracted = ExtractFirst(archive);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_BZip2_LargeRepetitiveData() {
    var data = new byte[10_000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 7);

    var archive = CreateArchive(data, SevenZipCodec.BZip2);
    var extracted = ExtractFirst(archive);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_PPMd_LargeRepetitiveData() {
    var data = new byte[10_000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 7);

    var archive = CreateArchive(data, SevenZipCodec.PPMd);
    var extracted = ExtractFirst(archive);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void DefaultConstructor_UsesLzma2() {
    // The default constructor (no codec parameter) should still produce valid LZMA2 archives
    var data = "default codec test"u8.ToArray();
    using var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "test.dat" }, data);
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(new MemoryStream(ms.ToArray()));
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Lzma2_WithDirectory() {
    byte[] data = [1, 2, 3];
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true)) {
        writer.AddDirectory("mydir");
        writer.AddEntry(new SevenZipEntry { Name = "mydir/file.bin" }, data);
      }
      archive = ms.ToArray();
    }

    using var reader = new SevenZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    var fileIndex = -1;
    for (var i = 0; i < reader.Entries.Count; ++i)
      if (!reader.Entries[i].IsDirectory) { fileIndex = i; break; }
    Assert.That(fileIndex, Is.GreaterThanOrEqualTo(0));
    Assert.That(reader.Extract(fileIndex), Is.EqualTo(data));
  }

  private static byte[] CreateArchive(byte[] data, SevenZipCodec codec) {
    using var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, codec, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "test.dat" }, data);
    }
    return ms.ToArray();
  }

  private static byte[] CreateMultiFileArchive(SevenZipCodec codec, byte[] data1, byte[] data2) {
    using var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, codec, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "file1.txt" }, data1);
      writer.AddEntry(new SevenZipEntry { Name = "file2.txt" }, data2);
    }
    return ms.ToArray();
  }

  private static byte[] ExtractFirst(byte[] archive) {
    using var reader = new SevenZipReader(new MemoryStream(archive));
    return reader.Extract(0);
  }
}
