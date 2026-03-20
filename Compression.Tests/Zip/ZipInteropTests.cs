using FileFormat.Zip;
using SysZipArchive = System.IO.Compression.ZipArchive;
using SysZipArchiveMode = System.IO.Compression.ZipArchiveMode;
using SysCompressionLevel = System.IO.Compression.CompressionLevel;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipInteropTests {
  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void SystemReads_OurArchive_Store() {
    byte[] data = "Hello from our ZIP writer!"u8.ToArray();
    byte[] archive = CreateOurArchive("test.txt", data, ZipCompressionMethod.Store);

    using var ms = new MemoryStream(archive);
    using var sysZip = new SysZipArchive(ms, SysZipArchiveMode.Read);
    Assert.That(sysZip.Entries, Has.Count.EqualTo(1));
    Assert.That(sysZip.Entries[0].Name, Is.EqualTo("test.txt"));

    using var entryStream = sysZip.Entries[0].Open();
    using var output = new MemoryStream();
    entryStream.CopyTo(output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void SystemReads_OurArchive_Deflate() {
    byte[] data = "Compressible text for interop testing with system ZIP implementation."u8.ToArray();
    byte[] archive = CreateOurArchive("test.txt", data, ZipCompressionMethod.Deflate);

    using var ms = new MemoryStream(archive);
    using var sysZip = new SysZipArchive(ms, SysZipArchiveMode.Read);
    Assert.That(sysZip.Entries, Has.Count.EqualTo(1));

    using var entryStream = sysZip.Entries[0].Open();
    using var output = new MemoryStream();
    entryStream.CopyTo(output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void WeRead_SystemArchive_Store() {
    byte[] data = "Hello from System.IO.Compression!"u8.ToArray();
    byte[] archive = CreateSystemArchive("test.txt", data, SysCompressionLevel.NoCompression);

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("test.txt"));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void WeRead_SystemArchive_Deflate() {
    byte[] data = "Hello from System.IO.Compression with deflate!"u8.ToArray();
    byte[] archive = CreateSystemArchive("test.txt", data, SysCompressionLevel.Optimal);

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void SystemReads_OurArchive_MultipleEntries() {
    byte[] data1 = "First file content"u8.ToArray();
    byte[] data2 = "Second file with more content for testing multiple entries."u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.AddEntry("file1.txt", data1, ZipCompressionMethod.Store);
        writer.AddEntry("dir/file2.txt", data2, ZipCompressionMethod.Deflate);
      }
      archive = ms.ToArray();
    }

    using var archiveMs = new MemoryStream(archive);
    using var sysZip = new SysZipArchive(archiveMs, SysZipArchiveMode.Read);
    Assert.That(sysZip.Entries, Has.Count.EqualTo(2));

    using (var s = sysZip.Entries[0].Open())
    using (var o = new MemoryStream()) {
      s.CopyTo(o);
      Assert.That(o.ToArray(), Is.EqualTo(data1));
    }

    using (var s = sysZip.Entries[1].Open())
    using (var o = new MemoryStream()) {
      s.CopyTo(o);
      Assert.That(o.ToArray(), Is.EqualTo(data2));
    }
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void WeRead_SystemArchive_MultipleEntries() {
    byte[] data1 = "First"u8.ToArray();
    byte[] data2 = "Second"u8.ToArray();
    byte[] data3 = "Third"u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var sysZip = new SysZipArchive(ms, SysZipArchiveMode.Create, leaveOpen: true)) {
        AddSystemEntry(sysZip, "a.txt", data1, SysCompressionLevel.NoCompression);
        AddSystemEntry(sysZip, "b.txt", data2, SysCompressionLevel.Optimal);
        AddSystemEntry(sysZip, "c.txt", data3, SysCompressionLevel.Fastest);
      }
      archive = ms.ToArray();
    }

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries, Has.Count.EqualTo(3));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data1));
    Assert.That(reader.ExtractEntry(reader.Entries[1]), Is.EqualTo(data2));
    Assert.That(reader.ExtractEntry(reader.Entries[2]), Is.EqualTo(data3));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void SystemReads_OurArchive_LargeData() {
    byte[] pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    byte[] data = new byte[pattern.Length * 500];
    for (int i = 0; i < 500; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] archive = CreateOurArchive("large.txt", data, ZipCompressionMethod.Deflate);

    using var ms = new MemoryStream(archive);
    using var sysZip = new SysZipArchive(ms, SysZipArchiveMode.Read);
    using var entryStream = sysZip.Entries[0].Open();
    using var output = new MemoryStream();
    entryStream.CopyTo(output);
    Assert.That(output.ToArray(), Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void WeRead_SystemArchive_LargeData() {
    byte[] pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    byte[] data = new byte[pattern.Length * 500];
    for (int i = 0; i < 500; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] archive = CreateSystemArchive("large.txt", data, SysCompressionLevel.Optimal);

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void SystemReads_OurArchive_WithComment() {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new ZipWriter(ms, leaveOpen: true)) {
        writer.Comment = "Archive comment for interop";
        writer.AddEntry("file.txt", "data"u8.ToArray());
      }
      archive = ms.ToArray();
    }

    using var archiveMs = new MemoryStream(archive);
    using var sysZip = new SysZipArchive(archiveMs, SysZipArchiveMode.Read);
    Assert.That(sysZip.Comment, Is.EqualTo("Archive comment for interop"));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void WeRead_SystemArchive_Utf8Names() {
    byte[] data = "content"u8.ToArray();
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var sysZip = new SysZipArchive(ms, SysZipArchiveMode.Create, leaveOpen: true)) {
        AddSystemEntry(sysZip, "caf\u00e9.txt", data, SysCompressionLevel.NoCompression);
      }
      archive = ms.ToArray();
    }

    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].FileName, Is.EqualTo("caf\u00e9.txt"));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
  }

  private static byte[] CreateOurArchive(string fileName, byte[] data, ZipCompressionMethod method) {
    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(fileName, data, method);
    }
    return ms.ToArray();
  }

  private static byte[] CreateSystemArchive(string fileName, byte[] data, SysCompressionLevel level) {
    using var ms = new MemoryStream();
    using (var sysZip = new SysZipArchive(ms, SysZipArchiveMode.Create, leaveOpen: true)) {
      AddSystemEntry(sysZip, fileName, data, level);
    }
    return ms.ToArray();
  }

  private static void AddSystemEntry(SysZipArchive archive, string name, byte[] data, SysCompressionLevel level) {
    var entry = archive.CreateEntry(name, level);
    using var stream = entry.Open();
    stream.Write(data, 0, data.Length);
  }
}
