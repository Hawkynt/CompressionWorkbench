namespace Compression.Tests.Ext;

[TestFixture]
public class ExtTests {

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var content = "Hello ext2!"u8.ToArray();
    var w = new FileFormat.Ext.ExtWriter();
    w.AddFile("test.txt", content);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileFormat.Ext.ExtReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(content.Length));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "First file content"u8.ToArray();
    var data2 = "Second file content"u8.ToArray();
    var data3 = new byte[100];
    for (var i = 0; i < data3.Length; i++) data3[i] = (byte)(i & 0xFF);

    var w = new FileFormat.Ext.ExtWriter();
    w.AddFile("alpha.txt", data1);
    w.AddFile("beta.bin", data2);
    w.AddFile("gamma.dat", data3);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileFormat.Ext.ExtReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_LargeFile() {
    // Test a file that spans multiple blocks (>1024 bytes with default 1K block size)
    var data = new byte[5000];
    var rng = new Random(42);
    rng.NextBytes(data);

    var w = new FileFormat.Ext.ExtWriter();
    w.AddFile("large.bin", data);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileFormat.Ext.ExtReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(5000));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Ext.ExtFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Ext"));
    Assert.That(desc.DisplayName, Is.EqualTo("ext2/3/4"));
    Assert.That(desc.Extensions, Does.Contain(".ext2"));
    Assert.That(desc.Extensions, Does.Contain(".ext3"));
    Assert.That(desc.Extensions, Does.Contain(".ext4"));
    Assert.That(desc.Extensions, Does.Contain(".img"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(1080));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileFormat.Ext.ExtFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST.TXT", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally { File.Delete(tmpFile); }
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Ext.ExtReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[2048];
    // Write wrong magic at offset 1080
    data[1080] = 0x00;
    data[1081] = 0x00;
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Ext.ExtReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var w = new FileFormat.Ext.ExtWriter();
    var image = w.Build();
    using var ms = new MemoryStream(image);
    var r = new FileFormat.Ext.ExtReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }
}
