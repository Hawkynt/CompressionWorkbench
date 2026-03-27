namespace Compression.Tests.Ampk;

[TestFixture]
public class AmpkTests {

  private static byte[] BuildAmpk(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    var w = new FileFormat.Ampk.AmpkWriter();
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var content = "Hello AMPK!"u8.ToArray();
    var ampk = BuildAmpk(("test.txt", content));
    using var ms = new MemoryStream(ampk);

    var r = new FileFormat.Ampk.AmpkReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "First"u8.ToArray();
    var data2 = "Second"u8.ToArray();
    var ampk = BuildAmpk(("a.txt", data1), ("b.bin", data2));
    using var ms = new MemoryStream(ampk);

    var r = new FileFormat.Ampk.AmpkReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsCorrect() {
    var ampk = BuildAmpk(("f.txt", "x"u8.ToArray()));
    Assert.That(ampk[0], Is.EqualTo((byte)'A'));
    Assert.That(ampk[1], Is.EqualTo((byte)'M'));
    Assert.That(ampk[2], Is.EqualTo((byte)'P'));
    Assert.That(ampk[3], Is.EqualTo((byte)'K'));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Ampk.AmpkFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Ampk"));
    Assert.That(desc.Extensions, Does.Contain(".ampk"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var ampk = BuildAmpk(("doc.pdf", new byte[50]));
    using var ms = new MemoryStream(ampk);
    var desc = new FileFormat.Ampk.AmpkFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[4]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Ampk.AmpkReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[100];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Ampk.AmpkReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var ampk = BuildAmpk(("f.txt", "x"u8.ToArray()));
    using var ms = new MemoryStream(ampk);
    var r = new FileFormat.Ampk.AmpkReader(ms);
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }
}
