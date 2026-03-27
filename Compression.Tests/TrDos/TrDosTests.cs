using System.Text;

namespace Compression.Tests.TrDos;

[TestFixture]
public class TrDosTests {

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var data = "Hello ZX Spectrum!"u8.ToArray();
    var w = new FileFormat.TrDos.TrDosWriter();
    w.AddFile("HELLO", 'C', data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.TrDos.TrDosReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("HELLO.cod"));

    var extracted = r.Extract(r.Entries[0]);
    // TR-DOS stores in 256-byte sectors, so extracted may be padded
    Assert.That(extracted.AsSpan(0, data.Length).ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileFormat.TrDos.TrDosWriter();
    w.AddFile("FILE1", 'B', "basic program"u8.ToArray());
    w.AddFile("FILE2", 'C', "machine code"u8.ToArray());
    w.AddFile("FILE3", 'D', "data block"u8.ToArray());
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.TrDos.TrDosReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Does.Contain("FILE1"));
    Assert.That(r.Entries[1].Name, Does.Contain("FILE2"));
    Assert.That(r.Entries[2].Name, Does.Contain("FILE3"));
  }

  [Test, Category("HappyPath")]
  public void DiskSize_Is655360() {
    var w = new FileFormat.TrDos.TrDosWriter();
    var disk = w.Build();
    Assert.That(disk.Length, Is.EqualTo(655360));
  }

  [Test, Category("HappyPath")]
  public void TrDosIdByte_AtCorrectOffset() {
    var w = new FileFormat.TrDos.TrDosWriter();
    var disk = w.Build();
    Assert.That(disk[0x800 + 0xE7], Is.EqualTo(0x10));
  }

  [Test, Category("HappyPath")]
  public void DiskLabel_ReadBack() {
    var w = new FileFormat.TrDos.TrDosWriter();
    var disk = w.Build("TESTDISK");

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.TrDos.TrDosReader(ms);
    Assert.That(r.DiskLabel, Does.StartWith("TESTDISK"));
  }

  [Test, Category("HappyPath")]
  public void FileTypes_MapCorrectly() {
    var w = new FileFormat.TrDos.TrDosWriter();
    w.AddFile("PROG", 'B', new byte[10]);
    w.AddFile("CODE", 'C', new byte[10]);
    w.AddFile("DATA", 'D', new byte[10]);
    w.AddFile("SEQ", '#', new byte[10]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.TrDos.TrDosReader(ms);
    Assert.That(r.Entries[0].Name, Does.EndWith(".bas"));
    Assert.That(r.Entries[1].Name, Does.EndWith(".cod"));
    Assert.That(r.Entries[2].Name, Does.EndWith(".dat"));
    Assert.That(r.Entries[3].Name, Does.EndWith(".seq"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.TrDos.TrDosFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("TrDos"));
    Assert.That(desc.Extensions, Does.Contain(".trd"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var w = new FileFormat.TrDos.TrDosWriter();
    w.AddFile("TEST", 'C', new byte[100]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var desc = new FileFormat.TrDos.TrDosFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.TrDos.TrDosReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadIdByte_Throws() {
    var data = new byte[655360];
    // Don't set the ID byte at 0x8E7
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.TrDos.TrDosReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var w = new FileFormat.TrDos.TrDosWriter();
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.TrDos.TrDosReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }
}
