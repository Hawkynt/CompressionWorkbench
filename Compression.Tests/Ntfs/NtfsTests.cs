namespace Compression.Tests.Ntfs;

[TestFixture]
public class NtfsTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello NTFS!"u8.ToArray();
    var w = new FileFormat.Ntfs.NtfsWriter();
    w.AddFile("TEST.TXT", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.Ntfs.NtfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEST.TXT"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileFormat.Ntfs.NtfsWriter();
    w.AddFile("A.TXT", "First"u8.ToArray());
    w.AddFile("B.TXT", "Second"u8.ToArray());
    w.AddFile("C.BIN", new byte[200]);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.Ntfs.NtfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("First"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("Second"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(new byte[200]));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_SmallFile_Resident() {
    // Small file should use resident $DATA (< 700 bytes)
    var data = "Small resident data"u8.ToArray();
    var w = new FileFormat.Ntfs.NtfsWriter();
    w.AddFile("SMALL.TXT", data);
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.Ntfs.NtfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Lznt1_RoundTrip() {
    var original = new byte[8192];
    // Fill with compressible data
    for (var i = 0; i < original.Length; i++)
      original[i] = (byte)(i % 26 + 'A');

    var compressed = FileFormat.Ntfs.Lznt1.Compress(original);
    var decompressed = FileFormat.Ntfs.Lznt1.Decompress(compressed, original.Length);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Ntfs.NtfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Ntfs"));
    Assert.That(desc.DisplayName, Is.EqualTo("NTFS"));
    Assert.That(desc.Extensions, Does.Contain(".ntfs"));
    Assert.That(desc.Extensions, Does.Contain(".img"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(3));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileFormat.Ntfs.NtfsFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST.TXT", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmpFile);
    }
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Ntfs.NtfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[1024];
    data[0] = 0xEB; data[1] = 0x52; data[2] = 0x90;
    // Leave OEM ID as zeros (not "NTFS    ")
    data[510] = 0x55; data[511] = 0xAA;
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Ntfs.NtfsReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var w = new FileFormat.Ntfs.NtfsWriter();
    var disk = w.Build();

    using var ms = new MemoryStream(disk);
    var r = new FileFormat.Ntfs.NtfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }
}
