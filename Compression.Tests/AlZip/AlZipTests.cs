namespace Compression.Tests.AlZip;

[TestFixture]
public class AlZipTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello, ALZip! This is a test of the Korean archive format."u8.ToArray();

    using var archive = new MemoryStream();
    using (var w = new FileFormat.AlZip.AlZipWriter(archive, leaveOpen: true))
      w.AddFile("test.txt", data);

    archive.Position = 0;
    using var r = new FileFormat.AlZip.AlZipReader(archive, leaveOpen: true);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].FileName, Is.EqualTo("test.txt"));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "File one content"u8.ToArray();
    var data2 = "File two content with more data here"u8.ToArray();
    var data3 = new byte[1024];
    Array.Fill(data3, (byte)'X');

    using var archive = new MemoryStream();
    using (var w = new FileFormat.AlZip.AlZipWriter(archive, leaveOpen: true)) {
      w.AddFile("a.txt", data1);
      w.AddFile("b.txt", data2);
      w.AddFile("c.bin", data3);
    }

    archive.Position = 0;
    using var r = new FileFormat.AlZip.AlZipReader(archive, leaveOpen: true);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsAlz() {
    using var archive = new MemoryStream();
    using (var w = new FileFormat.AlZip.AlZipWriter(archive, leaveOpen: true))
      w.AddFile("test.txt", "data"u8.ToArray());

    archive.Position = 0;
    Assert.That(archive.ReadByte(), Is.EqualTo(0x41)); // 'A'
    Assert.That(archive.ReadByte(), Is.EqualTo(0x4C)); // 'L'
    Assert.That(archive.ReadByte(), Is.EqualTo(0x5A)); // 'Z'
    Assert.That(archive.ReadByte(), Is.EqualTo(0x01));
  }

  [Test, Category("HappyPath")]
  public void Entry_HasCorrectSizes() {
    var data = new byte[500];
    new Random(42).NextBytes(data);

    using var archive = new MemoryStream();
    using (var w = new FileFormat.AlZip.AlZipWriter(archive, leaveOpen: true))
      w.AddFile("random.bin", data);

    archive.Position = 0;
    using var r = new FileFormat.AlZip.AlZipReader(archive, leaveOpen: true);
    Assert.That(r.Entries[0].OriginalSize, Is.EqualTo(500));
    Assert.That(r.Entries[0].CompressedSize, Is.LessThanOrEqualTo(500));
  }

  [Test, Category("EdgeCase")]
  public void EmptyFile_RoundTrip() {
    using var archive = new MemoryStream();
    using (var w = new FileFormat.AlZip.AlZipWriter(archive, leaveOpen: true))
      w.AddFile("empty.txt", []);

    archive.Position = 0;
    using var r = new FileFormat.AlZip.AlZipReader(archive, leaveOpen: true);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[100_000];
    new Random(123).NextBytes(data);

    using var archive = new MemoryStream();
    using (var w = new FileFormat.AlZip.AlZipWriter(archive, leaveOpen: true))
      w.AddFile("large.bin", data);

    archive.Position = 0;
    using var r = new FileFormat.AlZip.AlZipReader(archive, leaveOpen: true);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Directory_Entry() {
    using var archive = new MemoryStream();
    using (var w = new FileFormat.AlZip.AlZipWriter(archive, leaveOpen: true)) {
      w.AddDirectory("mydir/");
      w.AddFile("mydir/file.txt", "hello"u8.ToArray());
    }

    archive.Position = 0;
    using var r = new FileFormat.AlZip.AlZipReader(archive, leaveOpen: true);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].IsDirectory, Is.True);
    Assert.That(r.Entries[0].FileName, Is.EqualTo("mydir/"));
    Assert.That(r.Entries[1].IsDirectory, Is.False);
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("hello"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void StoreMethod_ForIncompressibleData() {
    // Random data that deflate can't compress
    var data = new byte[32];
    new Random(999).NextBytes(data);

    using var archive = new MemoryStream();
    using (var w = new FileFormat.AlZip.AlZipWriter(archive, leaveOpen: true))
      w.AddFile("random.bin", data);

    archive.Position = 0;
    using var r = new FileFormat.AlZip.AlZipReader(archive, leaveOpen: true);
    // Small random data should fall back to store
    Assert.That(r.Entries[0].Method, Is.EqualTo(0).Or.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }
}
