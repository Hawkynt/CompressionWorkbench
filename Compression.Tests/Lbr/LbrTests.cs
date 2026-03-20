namespace Compression.Tests.Lbr;

[TestFixture]
public class LbrTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello, CP/M LBR!"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Lbr.LbrWriter(ms, leaveOpen: true))
      w.AddFile("HELLO.TXT", data);
    ms.Position = 0;

    var r = new FileFormat.Lbr.LbrReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].FileName, Is.EqualTo("HELLO.TXT"));
    Assert.That(r.Extract(r.Entries[0]).AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "File one content"u8.ToArray();
    var data2 = "File two"u8.ToArray();

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Lbr.LbrWriter(ms, leaveOpen: true)) {
      w.AddFile("FILE1.TXT", data1);
      w.AddFile("FILE2.DAT", data2);
    }
    ms.Position = 0;

    var r = new FileFormat.Lbr.LbrReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries[0].FileName, Is.EqualTo("FILE1.TXT"));
    Assert.That(r.Entries[1].FileName, Is.EqualTo("FILE2.DAT"));
  }

  [Test, Category("HappyPath")]
  public void FileNames_AreUppercase() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Lbr.LbrWriter(ms, leaveOpen: true))
      w.AddFile("hello.txt", "data"u8.ToArray());
    ms.Position = 0;

    var r = new FileFormat.Lbr.LbrReader(ms);
    Assert.That(r.Entries[0].FileName, Is.EqualTo("HELLO.TXT"));
  }

  [Test, Category("HappyPath")]
  public void SectorAlignment() {
    // All data should be 128-byte aligned
    var data = new byte[100]; // less than one sector
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Lbr.LbrWriter(ms, leaveOpen: true))
      w.AddFile("TEST.BIN", data);

    // Total size should be multiple of 128
    Assert.That(ms.Length % 128, Is.EqualTo(0));
  }
}
