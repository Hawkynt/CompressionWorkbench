namespace Compression.Tests.Vpk;

[TestFixture]
public class VpkTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello VPK Source engine test!"u8.ToArray();
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Vpk.VpkWriter(archive, leaveOpen: true)) {
      w.AddFile("test.txt", data);
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Vpk.VpkReader(archive);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "File one"u8.ToArray();
    var data2 = "File two content"u8.ToArray();
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Vpk.VpkWriter(archive, leaveOpen: true)) {
      w.AddFile("a.txt", data1);
      w.AddFile("b.dat", data2);
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Vpk.VpkReader(archive);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    var ext1 = r.Extract(r.Entries.First(e => e.FileName == "a"));
    var ext2 = r.Extract(r.Entries.First(e => e.FileName == "b"));
    Assert.That(ext1, Is.EqualTo(data1));
    Assert.That(ext2, Is.EqualTo(data2));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsVpk() {
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Vpk.VpkWriter(archive, leaveOpen: true)) {
      w.AddFile("test.txt", "data"u8.ToArray());
      w.Finish();
    }
    archive.Position = 0;
    var buf = new byte[4];
    archive.Read(buf, 0, 4);
    var sig = BitConverter.ToUInt32(buf);
    Assert.That(sig, Is.EqualTo(0x55AA1234u));
  }

  [Test, Category("HappyPath")]
  public void Version_IsOne() {
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Vpk.VpkWriter(archive, leaveOpen: true)) {
      w.AddFile("test.txt", "data"u8.ToArray());
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Vpk.VpkReader(archive);
    Assert.That(r.Version, Is.EqualTo(1));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_DirectoryPaths() {
    var data = "nested file"u8.ToArray();
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Vpk.VpkWriter(archive, leaveOpen: true)) {
      w.AddFile("models/player/model.mdl", data);
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Vpk.VpkReader(archive);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].DirectoryPath, Is.EqualTo("models/player"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_EmptyFile() {
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Vpk.VpkWriter(archive, leaveOpen: true)) {
      w.AddFile("empty.txt", []);
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Vpk.VpkReader(archive);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("test.vpk");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Vpk));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[50000];
    new Random(42).NextBytes(data);
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Vpk.VpkWriter(archive, leaveOpen: true)) {
      w.AddFile("large.bin", data);
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Vpk.VpkReader(archive);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }
}
