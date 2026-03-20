using FileFormat.Zip;
using SysZipArchive = System.IO.Compression.ZipArchive;
using SysZipArchiveMode = System.IO.Compression.ZipArchiveMode;
using SysCompressionLevel = System.IO.Compression.CompressionLevel;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipReaderTests {
  [Category("ThemVsUs")]
  [Test]
  public void ReadsSystemArchive_SingleEntry_Store() {
    byte[] data = "Stored content"u8.ToArray();
    byte[] archive = CreateSystemArchive(("file.txt", data, SysCompressionLevel.NoCompression));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("file.txt"));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Store));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Test]
  public void ReadsSystemArchive_SingleEntry_Deflate() {
    byte[] data = "Deflated content for testing the reader implementation."u8.ToArray();
    byte[] archive = CreateSystemArchive(("file.txt", data, SysCompressionLevel.Optimal));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Deflate));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Test]
  public void ReadsSystemArchive_MultipleEntries() {
    byte[] d1 = "Alpha"u8.ToArray();
    byte[] d2 = "Bravo"u8.ToArray();
    byte[] d3 = "Charlie"u8.ToArray();
    byte[] archive = CreateSystemArchive(
      ("alpha.txt", d1, SysCompressionLevel.NoCompression),
      ("bravo.txt", d2, SysCompressionLevel.Optimal),
      ("charlie.txt", d3, SysCompressionLevel.Fastest));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(d1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(d2));
    Assert.That(reader.ExtractEntry(reader.Entries[2]), Is.EqualTo(d3));
  }

  [Category("ThemVsUs")]
  [Test]
  public void ReadsSystemArchive_Utf8FileNames() {
    byte[] data = "unicode content"u8.ToArray();
    byte[] archive = CreateSystemArchive(
      ("\u00fc\u00f6\u00e4/\u00df.txt", data, SysCompressionLevel.NoCompression));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("\u00fc\u00f6\u00e4/\u00df.txt"));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Test]
  public void ReadsSystemArchive_EmptyFile() {
    byte[] archive = CreateSystemArchive(("empty.txt", [], SysCompressionLevel.NoCompression));

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(Array.Empty<byte>()));
  }

  [Category("Exception")]
  [Test]
  public void CrcMismatch_ThrowsInvalidDataException() {
    byte[] data = "Valid data"u8.ToArray();
    byte[] archive = CreateSystemArchive(("file.txt", data, SysCompressionLevel.NoCompression));

    // Corrupt the stored data (it starts after the local file header)
    // Find the data by looking for "Valid data" in the archive
    int dataPos = FindPattern(archive, data);
    Assert.That(dataPos, Is.GreaterThan(0), "Could not find data in archive");
    archive[dataPos] ^= 0xFF; // Corrupt first byte

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.Throws<InvalidDataException>(() => reader.ExtractEntry(reader.Entries[0]));
  }

  [Category("Exception")]
  [Test]
  public void NonSeekableStream_ThrowsArgumentException() {
    var nonSeekable = new NonSeekableStream();
    Assert.Throws<ArgumentException>(() => new ZipReader(nonSeekable));
  }

  [Category("Exception")]
  [Test]
  public void NullStream_ThrowsArgumentNullException() {
    Assert.Throws<ArgumentNullException>(() => new ZipReader(null!));
  }

  [Category("HappyPath")]
  [Test]
  public void Dispose_ClosesStream() {
    byte[] archive = CreateSystemArchive(("file.txt", "data"u8.ToArray(), SysCompressionLevel.NoCompression));
    var ms = new MemoryStream(archive);

    var reader = new ZipReader(ms);
    reader.Dispose();

    Assert.Throws<ObjectDisposedException>(() => ms.ReadByte());
  }

  [Category("HappyPath")]
  [Test]
  public void Dispose_LeaveOpen_KeepsStreamOpen() {
    byte[] archive = CreateSystemArchive(("file.txt", "data"u8.ToArray(), SysCompressionLevel.NoCompression));
    var ms = new MemoryStream(archive);

    var reader = new ZipReader(ms, leaveOpen: true);
    reader.Dispose();

    // Stream should still be accessible
    ms.Position = 0;
    Assert.That(ms.ReadByte(), Is.GreaterThanOrEqualTo(0));
  }

  [Category("HappyPath")]
  [Test]
  public void OpenEntry_ReturnsNonWritableStream() {
    byte[] data = "test"u8.ToArray();
    byte[] archive = CreateSystemArchive(("file.txt", data, SysCompressionLevel.NoCompression));

    using var reader = new ZipReader(new MemoryStream(archive));
    using var stream = reader.OpenEntry(reader.Entries[0]);
    Assert.That(stream.CanWrite, Is.False);
    Assert.That(stream.CanRead, Is.True);
  }

  private static byte[] CreateSystemArchive(params (string name, byte[] data, SysCompressionLevel level)[] entries) {
    using var ms = new MemoryStream();
    using (var sysZip = new SysZipArchive(ms, SysZipArchiveMode.Create, leaveOpen: true)) {
      foreach (var (name, data, level) in entries) {
        var entry = sysZip.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
      }
    }
    return ms.ToArray();
  }

  private static int FindPattern(byte[] haystack, byte[] needle) {
    for (int i = 0; i <= haystack.Length - needle.Length; ++i) {
      var match = true;
      for (int j = 0; j < needle.Length; ++j) {
        if (haystack[i + j] != needle[j]) {
          match = false;
          break;
        }
      }
      if (match) return i;
    }
    return -1;
  }

  private sealed class NonSeekableStream : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => 0;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
