namespace Compression.Tests.Bsa;

[TestFixture]
public class BsaTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello BSA Skyrim test!"u8.ToArray();
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Bsa.BsaWriter(archive, leaveOpen: true)) {
      w.AddFile("meshes\\test.nif", data);
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Bsa.BsaReader(archive);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Format, Is.EqualTo(FileFormat.Bsa.BsaReader.BsaFormat.Tes4));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "Mesh data"u8.ToArray();
    var data2 = "Texture data here"u8.ToArray();
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Bsa.BsaWriter(archive, leaveOpen: true)) {
      w.AddFile("meshes\\test.nif", data1);
      w.AddFile("textures\\test.dds", data2);
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Bsa.BsaReader(archive);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsBsa() {
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Bsa.BsaWriter(archive, leaveOpen: true)) {
      w.AddFile("test.nif", "data"u8.ToArray());
      w.Finish();
    }
    archive.Position = 0;
    var buf = new byte[4];
    archive.Read(buf, 0, 4);
    // BSA\0 = 0x00415342 LE = bytes 0x42, 0x53, 0x41, 0x00
    Assert.That(buf[0], Is.EqualTo(0x42));
    Assert.That(buf[1], Is.EqualTo(0x53));
    Assert.That(buf[2], Is.EqualTo(0x41));
    Assert.That(buf[3], Is.EqualTo(0x00));
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension() {
    Assert.That(Compression.Lib.FormatDetector.DetectByExtension("test.bsa"),
      Is.EqualTo(Compression.Lib.FormatDetector.Format.Bsa));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[50000];
    new Random(42).NextBytes(data);
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Bsa.BsaWriter(archive, leaveOpen: true)) {
      w.AddFile("data\\large.bin", data);
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Bsa.BsaReader(archive);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Entry_HasCorrectPath() {
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Bsa.BsaWriter(archive, leaveOpen: true)) {
      w.AddFile("meshes\\armor\\test.nif", "data"u8.ToArray());
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Bsa.BsaReader(archive);
    Assert.That(r.Entries[0].FullPath, Does.Contain("test.nif"));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_EmptyFile() {
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Bsa.BsaWriter(archive, leaveOpen: true)) {
      w.AddFile("empty.txt", []);
      w.Finish();
    }
    archive.Position = 0;
    var r = new FileFormat.Bsa.BsaReader(archive);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Version_Is105() {
    using var archive = new MemoryStream();
    using (var w = new FileFormat.Bsa.BsaWriter(archive, leaveOpen: true)) {
      w.AddFile("test.txt", "data"u8.ToArray());
      w.Finish();
    }
    archive.Position = 0;
    var br = new BinaryReader(archive);
    br.ReadUInt32(); // magic
    var version = br.ReadInt32();
    Assert.That(version, Is.EqualTo(105));
  }
}
