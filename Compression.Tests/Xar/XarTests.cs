namespace Compression.Tests.Xar;

[TestFixture]
public class XarTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello, XAR! This is a test of the eXtensible ARchive format."u8.ToArray();

    using var archive = new MemoryStream();
    using (var w = new FileFormat.Xar.XarWriter(archive, leaveOpen: true))
      w.AddFile("test.txt", data);

    archive.Position = 0;
    var r = new FileFormat.Xar.XarReader(archive);
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
    using (var w = new FileFormat.Xar.XarWriter(archive, leaveOpen: true)) {
      w.AddFile("a.txt", data1);
      w.AddFile("b.txt", data2);
      w.AddFile("c.bin", data3);
    }

    archive.Position = 0;
    var r = new FileFormat.Xar.XarReader(archive);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsXar() {
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Xar.XarWriter(archive, leaveOpen: true))
      w.AddFile("test.txt", "data"u8.ToArray());

    archive.Position = 0;
    Assert.That(archive.ReadByte(), Is.EqualTo(0x78)); // 'x'
    Assert.That(archive.ReadByte(), Is.EqualTo(0x61)); // 'a'
    Assert.That(archive.ReadByte(), Is.EqualTo(0x72)); // 'r'
    Assert.That(archive.ReadByte(), Is.EqualTo(0x21)); // '!'
  }

  [Test, Category("HappyPath")]
  public void Entry_HasCorrectSizes() {
    var data = new byte[500];
    new Random(42).NextBytes(data);

    using var archive = new MemoryStream();
    using (var w = new FileFormat.Xar.XarWriter(archive, leaveOpen: true))
      w.AddFile("random.bin", data);

    archive.Position = 0;
    var r = new FileFormat.Xar.XarReader(archive);
    Assert.That(r.Entries[0].OriginalSize, Is.EqualTo(500));
    Assert.That(r.Entries[0].Method, Is.EqualTo("zlib"));
  }

  [Test, Category("EdgeCase")]
  public void EmptyFile_RoundTrip() {
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Xar.XarWriter(archive, leaveOpen: true))
      w.AddFile("empty.txt", []);

    archive.Position = 0;
    var r = new FileFormat.Xar.XarReader(archive);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[100_000];
    new Random(123).NextBytes(data);

    using var archive = new MemoryStream();
    using (var w = new FileFormat.Xar.XarWriter(archive, leaveOpen: true))
      w.AddFile("large.bin", data);

    archive.Position = 0;
    var r = new FileFormat.Xar.XarReader(archive);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }
}
