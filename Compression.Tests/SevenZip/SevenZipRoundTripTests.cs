using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

[TestFixture]
public class SevenZipRoundTripTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile() {
    var data = System.Text.Encoding.UTF8.GetBytes("Hello, 7z World!");
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "test.txt", Size = data.Length }, data);
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].Name, Is.EqualTo("test.txt"));
    var extracted = reader.Extract(0);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MultipleFiles() {
    var data1 = System.Text.Encoding.UTF8.GetBytes("File one content");
    var data2 = System.Text.Encoding.UTF8.GetBytes("File two has different content");
    var data3 = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "a.txt", Size = data1.Length }, data1);
      writer.AddEntry(new SevenZipEntry { Name = "b.txt", Size = data2.Length }, data2);
      writer.AddEntry(new SevenZipEntry { Name = "c.bin", Size = data3.Length }, data3);
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(3));
    Assert.That(reader.Extract(0), Is.EqualTo(data1));
    Assert.That(reader.Extract(1), Is.EqualTo(data2));
    Assert.That(reader.Extract(2), Is.EqualTo(data3));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyFile() {
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "empty.txt", Size = 0 }, ReadOnlySpan<byte>.Empty);
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].Size, Is.EqualTo(0));
    var extracted = reader.Extract(0);
    Assert.That(extracted, Is.Empty);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeFile() {
    var data = new byte[50_000];
    var rng = new Random(42);
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(rng.Next(26) + 'a');

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "large.txt", Size = data.Length }, data);
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Extract(0), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Directory() {
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddDirectory("mydir");
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
    Assert.That(reader.Entries[0].IsDirectory, Is.True);
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MixedFilesAndDirectories() {
    byte[] data = [1, 2, 3];
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddDirectory("dir1");
      writer.AddEntry(new SevenZipEntry { Name = "dir1/file.txt", Size = data.Length }, data);
      writer.AddDirectory("dir2");
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(3));
    // Find the file entry and extract
    var fileIndex = -1;
    for (var i = 0; i < reader.Entries.Count; ++i)
      if (!reader.Entries[i].IsDirectory) {
        fileIndex = i;
        break;
      }

    Assert.That(fileIndex, Is.GreaterThanOrEqualTo(0));
    Assert.That(reader.Extract(fileIndex), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Timestamps() {
    var now = new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc);
    byte[] data = [42];
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry {
        Name = "timed.txt",
        Size = 1,
        LastWriteTime = now,
        CreationTime = now,
      }, data);
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    var entry = reader.Entries[0];
    Assert.That(entry.LastWriteTime, Is.Not.Null);
    // Allow 1-second tolerance for timestamp rounding
    Assert.That(entry.LastWriteTime!.Value, Is.EqualTo(now).Within(TimeSpan.FromSeconds(1)));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SolidArchive() {
    // Multiple files packed in single solid folder
    var d1 = new byte[1000];
    var d2 = new byte[2000];
    Array.Fill(d1, (byte)'A');
    Array.Fill(d2, (byte)'B');

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "f1.txt", Size = d1.Length }, d1);
      writer.AddEntry(new SevenZipEntry { Name = "f2.txt", Size = d2.Length }, d2);
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Extract(0), Is.EqualTo(d1));
    Assert.That(reader.Extract(1), Is.EqualTo(d2));
  }

  [Category("HappyPath")]
  [Test]
  public void Header_SignatureBytes() {
    byte[] data = [1, 2, 3];
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "t.bin", Size = 3 }, data);
      writer.Finish();
    }

    ms.Position = 0;
    var sig = new byte[6];
    _ = ms.Read(sig, 0, 6);
    Assert.That(sig.AsSpan().SequenceEqual(SevenZipConstants.Signature), Is.True);
  }

  [Category("HappyPath")]
  [Test]
  public void Header_CrcIsCorrect() {
    byte[] data = [42];
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "t.bin", Size = 1 }, data);
      writer.Finish();
    }

    // Just verify it can be read back (CRC validation happens in reader)
    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries.Count, Is.EqualTo(1));
  }

  [Category("HappyPath")]
  [Test]
  public void Compress_RepetitiveData_CompressesWell() {
    var data = new byte[10000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 4);

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "rep.bin", Size = data.Length }, data);
      writer.Finish();
    }

    var ratio = (double)ms.Length / data.Length;
    Assert.That(ratio, Is.LessThan(0.5));
  }

  [Category("HappyPath")]
  [Test]
  public void Entries_ReportCorrectSizes() {
    var data = new byte[12345];
    new Random(42).NextBytes(data);

    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "sized.bin", Size = data.Length }, data);
      writer.Finish();
    }

    ms.Position = 0;
    using var reader = new SevenZipReader(ms);
    Assert.That(reader.Entries[0].Size, Is.EqualTo(12345));
  }
}
