using FileFormat.Cpio;

namespace Compression.Tests.Cpio;

[TestFixture]
public class CpioTests {
  [Test]
  public void RoundTrip_EmptyArchive() {
    byte[] archive = CreateArchive([]);
    var entries = ReadArchive(archive);
    Assert.That(entries, Is.Empty);
  }

  [Test]
  public void RoundTrip_SingleFile() {
    byte[] data = "Hello, cpio!"u8.ToArray();
    byte[] archive = CreateArchive([("test.txt", data)]);

    var entries = ReadArchive(archive);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Entry.Name, Is.EqualTo("test.txt"));
    Assert.That(entries[0].Data, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_MultipleFiles() {
    byte[] data1 = "First file"u8.ToArray();
    byte[] data2 = "Second file with more content"u8.ToArray();
    byte[] data3 = [0, 1, 2, 3, 4, 5];

    byte[] archive = CreateArchive([
      ("file1.txt", data1),
      ("subdir/file2.txt", data2),
      ("binary.dat", data3),
    ]);

    var entries = ReadArchive(archive);
    Assert.That(entries, Has.Count.EqualTo(3));
    Assert.That(entries[0].Entry.Name, Is.EqualTo("file1.txt"));
    Assert.That(entries[0].Data, Is.EqualTo(data1));
    Assert.That(entries[1].Entry.Name, Is.EqualTo("subdir/file2.txt"));
    Assert.That(entries[1].Data, Is.EqualTo(data2));
    Assert.That(entries[2].Entry.Name, Is.EqualTo("binary.dat"));
    Assert.That(entries[2].Data, Is.EqualTo(data3));
  }

  [Test]
  public void RoundTrip_EmptyFile() {
    byte[] archive = CreateArchive([("empty.txt", [])]);
    var entries = ReadArchive(archive);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Entry.Name, Is.EqualTo("empty.txt"));
    Assert.That(entries[0].Data, Is.Empty);
  }

  [Test]
  public void RoundTrip_WithDirectory() {
    byte[] archive;
    using (var ms = new MemoryStream()) {
      using (var writer = new CpioWriter(ms, leaveOpen: true)) {
        writer.AddDirectory("mydir");
        writer.AddFile("mydir/file.txt", "content"u8);
      }
      archive = ms.ToArray();
    }

    var entries = ReadArchive(archive);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Entry.Name, Is.EqualTo("mydir"));
    Assert.That(entries[0].Entry.IsDirectory, Is.True);
    Assert.That(entries[1].Entry.Name, Is.EqualTo("mydir/file.txt"));
    Assert.That(entries[1].Entry.IsRegularFile, Is.True);
  }

  [Test]
  public void Header_StartsWithMagic() {
    byte[] archive = CreateArchive([("x", [1])]);
    string header = System.Text.Encoding.ASCII.GetString(archive, 0, 6);
    Assert.That(header, Is.EqualTo("070701"));
  }

  [Test]
  public void RoundTrip_LargeFile() {
    var rng = new Random(42);
    byte[] data = new byte[10000];
    rng.NextBytes(data);

    byte[] archive = CreateArchive([("large.bin", data)]);
    var entries = ReadArchive(archive);
    Assert.That(entries[0].Data, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_DataAlignmentEdgeCases() {
    // Test various sizes that test 4-byte alignment padding
    foreach (int size in new[] { 1, 2, 3, 4, 5, 15, 16, 17 }) {
      byte[] data = new byte[size];
      for (int i = 0; i < size; ++i) data[i] = (byte)(i + 1);

      byte[] archive = CreateArchive([("test", data)]);
      var entries = ReadArchive(archive);
      Assert.That(entries[0].Data, Is.EqualTo(data), $"Failed for size {size}");
    }
  }

  private static byte[] CreateArchive(List<(string Name, byte[] Data)> files) {
    using var ms = new MemoryStream();
    using (var writer = new CpioWriter(ms, leaveOpen: true)) {
      foreach (var (name, data) in files)
        writer.AddFile(name, data);
    }
    return ms.ToArray();
  }

  private static List<(CpioEntry Entry, byte[] Data)> ReadArchive(byte[] archive) {
    using var ms = new MemoryStream(archive);
    using var reader = new CpioReader(ms);
    return reader.ReadAll();
  }
}
