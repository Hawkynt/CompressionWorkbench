using Compression.Core.Deflate;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipWriterTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void SingleEntry_Store_RoundTrips() {
    var data = "Hello, ZIP!"u8.ToArray();
    var archive = CreateArchive(("hello.txt", data, ZipCompressionMethod.Store));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("hello.txt"));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Store));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void SingleEntry_Deflate_RoundTrips() {
    var data = "Hello, ZIP! This is some text that should compress well with Deflate."u8.ToArray();
    var archive = CreateArchive(("test.txt", data, ZipCompressionMethod.Deflate));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void MultipleEntries_RoundTrip() {
    var data1 = "First file"u8.ToArray();
    var data2 = "Second file with more content for better compression."u8.ToArray();
    var data3 = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };

    var archive = CreateArchive(
      ("file1.txt", data1, ZipCompressionMethod.Store),
      ("subdir/file2.txt", data2, ZipCompressionMethod.Deflate),
      ("binary.dat", data3, ZipCompressionMethod.Store));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("file1.txt"));
    Assert.That(reader.Entries[1].FileName, Is.EqualTo("subdir/file2.txt"));
    Assert.That(reader.Entries[2].FileName, Is.EqualTo("binary.dat"));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(data2));
    Assert.That(reader.ExtractEntry(reader.Entries[2]), Is.EqualTo(data3));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void DirectoryEntry_RoundTrips() {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.AddDirectory("mydir/");
        writer.AddEntry("mydir/file.txt", "content"u8.ToArray());
      }
      archive = ms.ToArray();
    }

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("mydir/"));
    Assert.That(reader.Entries[0].IsDirectory, Is.True);
    Assert.That(reader.Entries[1].FileName, Is.EqualTo("mydir/file.txt"));
    Assert.That(reader.Entries[1].IsDirectory, Is.False);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void DirectoryEntry_AddsTrailingSlash() {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.AddDirectory("notrailingslash");
      }
      archive = ms.ToArray();
    }

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("notrailingslash/"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ArchiveComment_RoundTrips() {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.Comment = "Test archive comment";
        writer.AddEntry("file.txt", "data"u8.ToArray());
      }
      archive = ms.ToArray();
    }

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Comment, Is.EqualTo("Test archive comment"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Deflate_FallsBackToStore_WhenLarger() {
    // Random data doesn't compress well
    var rng = new Random(42);
    var data = new byte[64];
    rng.NextBytes(data);

    var archive = CreateArchive(("random.bin", data, ZipCompressionMethod.Deflate));

    using var reader = new ZipReader(new MemoryStream(archive));
    // Should have fallen back to Store since random data won't compress
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Store));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void LastModified_RoundTrips() {
    // MS-DOS time has 2-second resolution
    var dt = new DateTime(2024, 6, 15, 14, 30, 22);
    var archive = CreateArchive(("file.txt", "data"u8.ToArray(), ZipCompressionMethod.Store, dt));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].LastModified, Is.EqualTo(dt));
  }

  [Category("Exception")]
  [Test]
  public void Finish_CannotAddMoreEntries() {
    using var ms = new MemoryStream();
    var writer = new ZipWriter(ms, leaveOpen: true);
    writer.Finish();

    Assert.Throws<InvalidOperationException>(() =>
      writer.AddEntry("file.txt", "data"u8.ToArray()));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Dispose_AutoFinishes() {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.AddEntry("file.txt", "hello"u8.ToArray());
        // Don't call Finish() explicitly
      }
      archive = ms.ToArray();
    }

    // Should still be a valid archive
    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo("hello"u8.ToArray()));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void EmptyArchive_RoundTrips() {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        // No entries
      }
      archive = ms.ToArray();
    }

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(0));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Utf8FileName_RoundTrips() {
    var data = "content"u8.ToArray();
    var archive = CreateArchive(("\u00e9\u00e8\u00ea/caf\u00e9.txt", data, ZipCompressionMethod.Store));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("\u00e9\u00e8\u00ea/caf\u00e9.txt"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void OpenEntry_ReturnsReadableStream() {
    var data = "stream test"u8.ToArray();
    var archive = CreateArchive(("file.txt", data, ZipCompressionMethod.Store));

    using var reader = new ZipReader(new MemoryStream(archive));
    using var stream = reader.OpenEntry(reader.Entries[0]);
    using var output = new MemoryStream();
    stream.CopyTo(output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  private static byte[] CreateArchive(params (string name, byte[] data, ZipCompressionMethod method, DateTime? lastModified)[] entries) {
    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true)) {
      foreach (var (name, data, method, lastModified) in entries)
        writer.AddEntry(name, data, method, lastModified);
    }
    return ms.ToArray();
  }

  private static byte[] CreateArchive(params (string name, byte[] data, ZipCompressionMethod method)[] entries) {
    return CreateArchive(entries.Select(e => (e.name, e.data, e.method, (DateTime?)null)).ToArray());
  }

  // ── BZip2, LZMA, PPMd writer round-trips ────────────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void BZip2_Writer_RoundTrips() {
    var data = "Hello BZip2 in ZIP! This data should compress with BZip2."u8.ToArray();
    var archive = CreateArchive(("test.txt", data, ZipCompressionMethod.BZip2));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Lzma_Writer_RoundTrips() {
    var data = "Hello LZMA in ZIP! This data should compress with LZMA compression."u8.ToArray();
    var archive = CreateArchive(("test.txt", data, ZipCompressionMethod.Lzma));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Ppmd_Writer_RoundTrips() {
    var data = "Hello PPMd in ZIP! This data should compress with PPMd version I."u8.ToArray();
    var archive = CreateArchive(("test.txt", data, ZipCompressionMethod.Ppmd));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void UnsupportedWriteMethod_Throws() {
    using var ms = new MemoryStream();
    using var writer = new ZipWriter(ms, leaveOpen: true);
    Assert.Throws<NotSupportedException>(() =>
      writer.AddEntry("file.txt", "data"u8.ToArray(), (ZipCompressionMethod)99));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Deflate64_RoundTrip() {
    var data = new byte[2048];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 53);

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true))
        writer.AddEntry("test.bin", data, ZipCompressionMethod.Deflate64);
      archive = ms.ToArray();
    }

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Deflate64));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  [TestCase(DeflateCompressionLevel.Best)]
  [TestCase(DeflateCompressionLevel.Maximum)]
  public void CompressionLevel_RoundTrips(DeflateCompressionLevel level) {
    var data = "Hello, ZIP with compression level!"u8.ToArray();
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true, compressionLevel: level)) {
        writer.AddEntry("file.txt", data);
      }
      archive = ms.ToArray();
    }

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void CompressionLevel_Maximum_SmallerThanDefault() {
    var pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    var data = new byte[pattern.Length * 100];
    for (var i = 0; i < 100; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] archiveDefault;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true, compressionLevel: DeflateCompressionLevel.Default))
        writer.AddEntry("file.txt", data);
      archiveDefault = ms.ToArray();
    }

    byte[] archiveMaximum;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true, compressionLevel: DeflateCompressionLevel.Maximum))
        writer.AddEntry("file.txt", data);
      archiveMaximum = ms.ToArray();
    }

    Assert.That(archiveMaximum.Length, Is.LessThanOrEqualTo(archiveDefault.Length));
  }
}
