namespace Compression.Tests.T64;

[TestFixture]
public class T64Tests {

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var data = "Hello C64 tape!"u8.ToArray();
    var w = new FileFormat.T64.T64Writer();
    w.AddFile("HELLO", data);
    var tape = w.Build();

    using var ms = new MemoryStream(tape);
    var r = new FileFormat.T64.T64Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileFormat.T64.T64Writer();
    w.AddFile("FILE1", "First"u8.ToArray());
    w.AddFile("FILE2", "Second"u8.ToArray());
    var tape = w.Build();

    using var ms = new MemoryStream(tape);
    var r = new FileFormat.T64.T64Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("First"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("Second"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void TapeName_ReadBack() {
    var w = new FileFormat.T64.T64Writer();
    w.AddFile("TEST", new byte[10]);
    var tape = w.Build("MY TAPE");

    using var ms = new MemoryStream(tape);
    var r = new FileFormat.T64.T64Reader(ms);
    Assert.That(r.TapeName, Is.EqualTo("MY TAPE"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.T64.T64FormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("T64"));
    Assert.That(desc.Extensions, Does.Contain(".t64"));
    Assert.That(desc.MagicSignatures, Has.Count.GreaterThan(0));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var w = new FileFormat.T64.T64Writer();
    w.AddFile("TEST", new byte[50]);
    var tape = w.Build();

    using var ms = new MemoryStream(tape);
    var desc = new FileFormat.T64.T64FormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_ViaInterface() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileFormat.T64.T64FormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally { File.Delete(tmpFile); }
  }

  [Test, Category("HappyPath")]
  public void StartAddress_Preserved() {
    var w = new FileFormat.T64.T64Writer();
    w.AddFile("CODE", 0xC000, new byte[256]);
    var tape = w.Build();

    using var ms = new MemoryStream(tape);
    var r = new FileFormat.T64.T64Reader(ms);
    Assert.That(r.Entries[0].StartAddress, Is.EqualTo(0xC000));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[10]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.T64.T64Reader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[100];
    data[0] = 0xFF;
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.T64.T64Reader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyTape_NoEntries() {
    var w = new FileFormat.T64.T64Writer();
    var tape = w.Build();
    using var ms = new MemoryStream(tape);
    var r = new FileFormat.T64.T64Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }
}
