namespace Compression.Tests.ExFat;

[TestFixture]
public class ExFatTests {

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var content = "Hello exFAT!"u8.ToArray();
    var w = new FileFormat.ExFat.ExFatWriter();
    w.AddFile("TEST.TXT", content);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.ExFat.ExFatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEST.TXT"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(content.Length));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "First file content"u8.ToArray();
    var data2 = "Second file"u8.ToArray();
    var data3 = new byte[100];
    new Random(42).NextBytes(data3);

    var w = new FileFormat.ExFat.ExFatWriter();
    w.AddFile("FILE1.TXT", data1);
    w.AddFile("FILE2.TXT", data2);
    w.AddFile("DATA.BIN", data3);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.ExFat.ExFatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));

    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(data2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_LargeFile() {
    var data = new byte[16384]; // 4 clusters worth
    new Random(123).NextBytes(data);

    var w = new FileFormat.ExFat.ExFatWriter();
    w.AddFile("LARGE.BIN", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.ExFat.ExFatReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.ExFat.ExFatFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("ExFat"));
    Assert.That(desc.DisplayName, Is.EqualTo("exFAT"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".img"));
    Assert.That(desc.Extensions, Does.Contain(".img"));
    Assert.That(desc.Extensions, Does.Contain(".exfat"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(desc.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(3));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var w = new FileFormat.ExFat.ExFatWriter();
    w.AddFile("TEST.DAT", new byte[50]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var desc = new FileFormat.ExFat.ExFatFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("TEST.DAT"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_ViaInterface() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileFormat.ExFat.ExFatFormatDescriptor();
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
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.ExFat.ExFatReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[1024];
    data[0] = 0xEB; data[1] = 0x76; data[2] = 0x90;
    // Write something other than "EXFAT   " at offset 3
    System.Text.Encoding.ASCII.GetBytes("NOTEXFAT").CopyTo(data, 3);
    data[510] = 0x55; data[511] = 0xAA;
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.ExFat.ExFatReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var w = new FileFormat.ExFat.ExFatWriter();
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.ExFat.ExFatReader(ms);
    // Should have no file entries (only system entries like bitmap/upcase)
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }
}
