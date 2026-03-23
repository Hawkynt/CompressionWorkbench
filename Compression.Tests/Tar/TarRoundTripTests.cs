using System.Text;
using FileFormat.Tar;

namespace Compression.Tests.Tar;

[TestFixture]
public class TarRoundTripTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleFile() {
    var data = "Hello, World!"u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var tw = new TarWriter(ms, leaveOpen: true)) {
        var entry = new TarEntry { Name = "hello.txt" };
        tw.AddEntry(entry, data);
      }

      archive = ms.ToArray();
    }

    using var tr = new TarReader(new MemoryStream(archive));
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    Assert.That(readEntry!.Name, Is.EqualTo("hello.txt"));
    Assert.That(readEntry.Size, Is.EqualTo(data.Length));

    using var entryStream = tr.GetEntryStream();
    var readData = new byte[readEntry.Size];
    _ = entryStream.Read(readData, 0, readData.Length);
    Assert.That(readData, Is.EqualTo(data));

    Assert.That(tr.GetNextEntry(), Is.Null);
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MultipleFiles() {
    var data1 = "First file content"u8.ToArray();
    var data2 = "Second file with different content"u8.ToArray();
    var data3 = "Third file data"u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var tw = new TarWriter(ms, leaveOpen: true)) {
        tw.AddEntry(new TarEntry { Name = "file1.txt" }, data1);
        tw.AddEntry(new TarEntry { Name = "subdir/file2.txt" }, data2);
        tw.AddEntry(new TarEntry { Name = "file3.dat" }, data3);
      }

      archive = ms.ToArray();
    }

    using var tr = new TarReader(new MemoryStream(archive));

    var entry1 = tr.GetNextEntry();
    Assert.That(entry1, Is.Not.Null);
    Assert.That(entry1!.Name, Is.EqualTo("file1.txt"));
    using (var s1 = tr.GetEntryStream()) {
      var read1 = new byte[entry1.Size];
      _ = s1.Read(read1, 0, read1.Length);
      Assert.That(read1, Is.EqualTo(data1));
    }

    var entry2 = tr.GetNextEntry();
    Assert.That(entry2, Is.Not.Null);
    Assert.That(entry2!.Name, Is.EqualTo("subdir/file2.txt"));
    using (var s2 = tr.GetEntryStream()) {
      var read2 = new byte[entry2.Size];
      _ = s2.Read(read2, 0, read2.Length);
      Assert.That(read2, Is.EqualTo(data2));
    }

    var entry3 = tr.GetNextEntry();
    Assert.That(entry3, Is.Not.Null);
    Assert.That(entry3!.Name, Is.EqualTo("file3.dat"));
    using (var s3 = tr.GetEntryStream()) {
      var read3 = new byte[entry3.Size];
      _ = s3.Read(read3, 0, read3.Length);
      Assert.That(read3, Is.EqualTo(data3));
    }

    Assert.That(tr.GetNextEntry(), Is.Null);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyFile() {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var tw = new TarWriter(ms, leaveOpen: true)) {
        var entry = new TarEntry { Name = "empty.txt" };
        tw.AddEntry(entry, ReadOnlySpan<byte>.Empty);
      }

      archive = ms.ToArray();
    }

    using var tr = new TarReader(new MemoryStream(archive));
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    Assert.That(readEntry!.Name, Is.EqualTo("empty.txt"));
    Assert.That(readEntry.Size, Is.EqualTo(0));

    Assert.That(tr.GetNextEntry(), Is.Null);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeFile() {
    // Create a file larger than 512 bytes to test padding
    var pattern = "ABCDEFGHIJKLMNOP"u8.ToArray();
    var data = new byte[2048];
    for (var i = 0; i < data.Length; ++i)
      data[i] = pattern[i % pattern.Length];

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var tw = new TarWriter(ms, leaveOpen: true)) {
        var entry = new TarEntry { Name = "large.bin" };
        tw.AddEntry(entry, data);
      }

      archive = ms.ToArray();
    }

    using var tr = new TarReader(new MemoryStream(archive));
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    Assert.That(readEntry!.Name, Is.EqualTo("large.bin"));
    Assert.That(readEntry.Size, Is.EqualTo(2048));

    using var entryStream = tr.GetEntryStream();
    var readData = new byte[readEntry.Size];
    _ = entryStream.Read(readData, 0, readData.Length);
    Assert.That(readData, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Directory() {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var tw = new TarWriter(ms, leaveOpen: true)) {
        var entry = new TarEntry {
          Name = "mydir/",
          TypeFlag = TarConstants.TypeDirectory,
          Mode = 493, // 0755 octal
        };
        tw.AddEntry(entry, ReadOnlySpan<byte>.Empty);
      }

      archive = ms.ToArray();
    }

    using var tr = new TarReader(new MemoryStream(archive));
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    Assert.That(readEntry!.Name, Is.EqualTo("mydir/"));
    Assert.That(readEntry.IsDirectory, Is.True);
    Assert.That(readEntry.IsFile, Is.False);

    Assert.That(tr.GetNextEntry(), Is.Null);
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LongFileName() {
    // Create a name longer than 100 characters
    var longName = "very/long/path/to/a/deeply/nested/directory/structure/that/exceeds/one/hundred/characters/in/total/length/file.txt";
    Assert.That(longName.Length, Is.GreaterThan(100));

    var data = "Long name file content"u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var tw = new TarWriter(ms, leaveOpen: true)) {
        var entry = new TarEntry { Name = longName };
        tw.AddEntry(entry, data);
      }

      archive = ms.ToArray();
    }

    using var tr = new TarReader(new MemoryStream(archive));
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    Assert.That(readEntry!.Name, Is.EqualTo(longName));
    Assert.That(readEntry.Size, Is.EqualTo(data.Length));

    using var entryStream = tr.GetEntryStream();
    var readData = new byte[readEntry.Size];
    _ = entryStream.Read(readData, 0, readData.Length);
    Assert.That(readData, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SymLink() {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var tw = new TarWriter(ms, leaveOpen: true)) {
        var entry = new TarEntry {
          Name = "link.txt",
          TypeFlag = TarConstants.TypeSymLink,
          LinkName = "target.txt",
        };
        tw.AddEntry(entry, ReadOnlySpan<byte>.Empty);
      }

      archive = ms.ToArray();
    }

    using var tr = new TarReader(new MemoryStream(archive));
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    Assert.That(readEntry!.Name, Is.EqualTo("link.txt"));
    Assert.That(readEntry.TypeFlag, Is.EqualTo(TarConstants.TypeSymLink));
    Assert.That(readEntry.LinkName, Is.EqualTo("target.txt"));

    Assert.That(tr.GetNextEntry(), Is.Null);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Permissions() {
    var data = "Executable script"u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var tw = new TarWriter(ms, leaveOpen: true)) {
        var entry = new TarEntry {
          Name = "script.sh",
          Mode = 493, // 0755 octal
        };
        tw.AddEntry(entry, data);
      }

      archive = ms.ToArray();
    }

    using var tr = new TarReader(new MemoryStream(archive));
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    Assert.That(readEntry!.Mode, Is.EqualTo(493));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Timestamps() {
    // Use a specific timestamp (TAR stores Unix seconds, so sub-second precision is lost)
    var timestamp = new DateTimeOffset(2024, 6, 15, 12, 30, 45, TimeSpan.Zero);
    var data = "Timestamped file"u8.ToArray();

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var tw = new TarWriter(ms, leaveOpen: true)) {
        var entry = new TarEntry {
          Name = "dated.txt",
          ModifiedTime = timestamp,
        };
        tw.AddEntry(entry, data);
      }

      archive = ms.ToArray();
    }

    using var tr = new TarReader(new MemoryStream(archive));
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    // Compare to second precision (TAR uses Unix timestamps)
    Assert.That(readEntry!.ModifiedTime.ToUnixTimeSeconds(),
      Is.EqualTo(timestamp.ToUnixTimeSeconds()));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_PaxHeader_NonAsciiName() {
    var data = "PAX test data"u8.ToArray();
    var name = "日本語/テスト.txt";

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var tw = new TarWriter(ms, leaveOpen: true)) {
        var entry = new TarEntry { Name = name };
        tw.AddEntry(entry, data);
      }
      archive = ms.ToArray();
    }

    using var tr = new TarReader(new MemoryStream(archive));
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    Assert.That(readEntry!.Name, Is.EqualTo(name));
    Assert.That(readEntry.Size, Is.EqualTo(data.Length));

    using var entryStream = tr.GetEntryStream();
    var extracted = new byte[readEntry.Size];
    entryStream.ReadExactly(extracted);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_PaxHeader_NonAsciiLinkName() {
    var name = "link.txt";
    var linkTarget = "ターゲット/ファイル.txt";

    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var tw = new TarWriter(ms, leaveOpen: true)) {
        var entry = new TarEntry {
          Name = name,
          TypeFlag = (byte)'2', // symlink
          LinkName = linkTarget,
        };
        tw.AddEntry(entry);
      }
      archive = ms.ToArray();
    }

    using var tr = new TarReader(new MemoryStream(archive));
    var readEntry = tr.GetNextEntry();
    Assert.That(readEntry, Is.Not.Null);
    Assert.That(readEntry!.Name, Is.EqualTo(name));
    Assert.That(readEntry.LinkName, Is.EqualTo(linkTarget));
  }
}
