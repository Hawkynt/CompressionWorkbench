namespace Compression.Tests.IffCdaf;

[TestFixture]
public class IffCdafTests {

  private static byte[] BuildCdaf(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    var w = new FileFormat.IffCdaf.IffCdafWriter();
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var content = "Hello IFF!"u8.ToArray();
    var cdaf = BuildCdaf(("test.txt", content));
    using var ms = new MemoryStream(cdaf);

    var r = new FileFormat.IffCdaf.IffCdafReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "First"u8.ToArray();
    var data2 = "Second"u8.ToArray();
    var cdaf = BuildCdaf(("a.txt", data1), ("b.bin", data2));
    using var ms = new MemoryStream(cdaf);

    var r = new FileFormat.IffCdaf.IffCdafReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath")]
  public void Header_IsFORM_CDAF() {
    var cdaf = BuildCdaf(("f.txt", "x"u8.ToArray()));
    Assert.That(cdaf[0..4], Is.EqualTo("FORM"u8.ToArray()));
    Assert.That(cdaf[8..12], Is.EqualTo("CDAF"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.IffCdaf.IffCdafFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("IffCdaf"));
    Assert.That(desc.Extensions, Does.Contain(".cdaf"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var cdaf = BuildCdaf(("doc.txt", new byte[50]));
    using var ms = new MemoryStream(cdaf);
    var desc = new FileFormat.IffCdaf.IffCdafFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_OddSizeData() {
    // Test IFF word-alignment padding
    var data = "ABC"u8.ToArray(); // odd size
    var cdaf = BuildCdaf(("odd.txt", data));
    using var ms = new MemoryStream(cdaf);

    var r = new FileFormat.IffCdaf.IffCdafReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[8]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.IffCdaf.IffCdafReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_NotFORM_Throws() {
    var data = new byte[100];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.IffCdaf.IffCdafReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var cdaf = BuildCdaf(("f.txt", "x"u8.ToArray()));
    using var ms = new MemoryStream(cdaf);
    var r = new FileFormat.IffCdaf.IffCdafReader(ms);
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }
}
