using Compression.Core.Streams;
using FileFormat.Bzip2;
using FileFormat.Gzip;
using FileFormat.Tar;
using FileFormat.Xz;

namespace Compression.Tests.Tar;

[TestFixture]
public class TarCompoundFormatTests {
  private static readonly byte[] TestData = "Hello from a tar compound format test! This is some sample content."u8.ToArray();

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void CompoundFormat_TarGz() {
    // Create tar
    var tarMs = new MemoryStream();
    using (var tw = new TarWriter(tarMs, leaveOpen: true)) {
      var entry = new TarEntry { Name = "test.txt" };
      tw.AddEntry(entry, TestData);
    }

    tarMs.Position = 0;

    // Compress with gzip
    var compressedMs = new MemoryStream();
    using (var compressor = new GzipStream(compressedMs, CompressionStreamMode.Compress, leaveOpen: true)) {
      tarMs.CopyTo(compressor);
    }

    compressedMs.Position = 0;

    // Decompress
    var decompressedMs = new MemoryStream();
    using (var decompressor = new GzipStream(compressedMs, CompressionStreamMode.Decompress)) {
      decompressor.CopyTo(decompressedMs);
    }

    decompressedMs.Position = 0;

    // Read tar
    using var tr = new TarReader(decompressedMs);
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    Assert.That(readEntry!.Name, Is.EqualTo("test.txt"));
    Assert.That(readEntry.Size, Is.EqualTo(TestData.Length));

    using var entryStream = tr.GetEntryStream();
    var readData = new byte[readEntry.Size];
    _ = entryStream.Read(readData, 0, readData.Length);
    Assert.That(readData, Is.EqualTo(TestData));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void CompoundFormat_TarBz2() {
    // Create tar
    var tarMs = new MemoryStream();
    using (var tw = new TarWriter(tarMs, leaveOpen: true)) {
      var entry = new TarEntry { Name = "test.txt" };
      tw.AddEntry(entry, TestData);
    }

    tarMs.Position = 0;

    // Compress with bzip2
    var compressedMs = new MemoryStream();
    using (var compressor = new Bzip2Stream(compressedMs, CompressionStreamMode.Compress, leaveOpen: true)) {
      tarMs.CopyTo(compressor);
    }

    compressedMs.Position = 0;

    // Decompress
    var decompressedMs = new MemoryStream();
    using (var decompressor = new Bzip2Stream(compressedMs, CompressionStreamMode.Decompress)) {
      decompressor.CopyTo(decompressedMs);
    }

    decompressedMs.Position = 0;

    // Read tar
    using var tr = new TarReader(decompressedMs);
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    Assert.That(readEntry!.Name, Is.EqualTo("test.txt"));
    Assert.That(readEntry.Size, Is.EqualTo(TestData.Length));

    using var entryStream = tr.GetEntryStream();
    var readData = new byte[readEntry.Size];
    _ = entryStream.Read(readData, 0, readData.Length);
    Assert.That(readData, Is.EqualTo(TestData));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void CompoundFormat_TarXz() {
    // Create tar
    var tarMs = new MemoryStream();
    using (var tw = new TarWriter(tarMs, leaveOpen: true)) {
      var entry = new TarEntry { Name = "test.txt" };
      tw.AddEntry(entry, TestData);
    }

    tarMs.Position = 0;

    // Compress with xz
    var compressedMs = new MemoryStream();
    using (var compressor = new XzStream(compressedMs, CompressionStreamMode.Compress, leaveOpen: true)) {
      tarMs.CopyTo(compressor);
    }

    compressedMs.Position = 0;

    // Decompress
    var decompressedMs = new MemoryStream();
    using (var decompressor = new XzStream(compressedMs, CompressionStreamMode.Decompress)) {
      decompressor.CopyTo(decompressedMs);
    }

    decompressedMs.Position = 0;

    // Read tar
    using var tr = new TarReader(decompressedMs);
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    Assert.That(readEntry!.Name, Is.EqualTo("test.txt"));
    Assert.That(readEntry.Size, Is.EqualTo(TestData.Length));

    using var entryStream = tr.GetEntryStream();
    var readData = new byte[readEntry.Size];
    _ = entryStream.Read(readData, 0, readData.Length);
    Assert.That(readData, Is.EqualTo(TestData));
  }
}
