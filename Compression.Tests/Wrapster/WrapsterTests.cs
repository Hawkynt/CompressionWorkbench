namespace Compression.Tests.Wrapster;

[TestFixture]
public class WrapsterTests {

  private static byte[] BuildWrapster(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    var w = new FileFormat.Wrapster.WrapsterWriter();
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var content = "Hello Wrapster!"u8.ToArray();
    var wrapped = BuildWrapster(("test.txt", content));
    using var ms = new MemoryStream(wrapped);

    var r = new FileFormat.Wrapster.WrapsterReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "First"u8.ToArray();
    var data2 = "Second file content"u8.ToArray();
    var wrapped = BuildWrapster(("a.txt", data1), ("b.bin", data2));
    using var ms = new MemoryStream(wrapped);

    var r = new FileFormat.Wrapster.WrapsterReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
  }

  [Test, Category("HappyPath")]
  public void Mp3Header_Present() {
    var wrapped = BuildWrapster(("f.txt", "x"u8.ToArray()));
    Assert.That(wrapped[0], Is.EqualTo(0xFF));
    Assert.That(wrapped[1] & 0xE0, Is.EqualTo(0xE0));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Wrapster.WrapsterFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Wrapster"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var wrapped = BuildWrapster(("doc.pdf", new byte[50]));
    using var ms = new MemoryStream(wrapped);
    var desc = new FileFormat.Wrapster.WrapsterFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("doc.pdf"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[10]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Wrapster.WrapsterReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_NotMp3_Throws() {
    var data = new byte[100];
    data[0] = 0x00;
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Wrapster.WrapsterReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var wrapped = BuildWrapster(("f.txt", "x"u8.ToArray()));
    using var ms = new MemoryStream(wrapped);
    var r = new FileFormat.Wrapster.WrapsterReader(ms);
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_LargeFile() {
    var data = new byte[5000];
    Random.Shared.NextBytes(data);
    var wrapped = BuildWrapster(("big.bin", data));
    using var ms = new MemoryStream(wrapped);

    var r = new FileFormat.Wrapster.WrapsterReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }
}
