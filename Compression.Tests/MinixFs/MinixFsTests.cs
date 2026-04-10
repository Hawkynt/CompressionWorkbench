namespace Compression.Tests.MinixFs;

[TestFixture]
public class MinixFsTests {

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleFile() {
    var content = "Hello Minix!"u8.ToArray();

    using var ms = new MemoryStream();
    var w = new FileFormat.MinixFs.MinixFsWriter(ms, leaveOpen: true);
    w.AddFile("hello.txt", content);
    w.Finish();

    ms.Position = 0;
    var r = new FileFormat.MinixFs.MinixFsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(content.Length));
    Assert.That(r.Entries[0].IsDirectory, Is.False);

    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var data1 = "First file"u8.ToArray();
    var data2 = "Second file content here"u8.ToArray();
    var data3 = new byte[200];
    for (var i = 0; i < data3.Length; i++) data3[i] = (byte)(i & 0xFF);

    using var ms = new MemoryStream();
    var w = new FileFormat.MinixFs.MinixFsWriter(ms, leaveOpen: true);
    w.AddFile("alpha.txt", data1);
    w.AddFile("beta.bin", data2);
    w.AddFile("gamma.dat", data3);
    w.Finish();

    ms.Position = 0;
    var r = new FileFormat.MinixFs.MinixFsReader(ms);

    Assert.That(r.Entries, Has.Count.EqualTo(3));

    var names = r.Entries.Select(e => e.Name).ToArray();
    Assert.That(names, Does.Contain("alpha.txt"));
    Assert.That(names, Does.Contain("beta.bin"));
    Assert.That(names, Does.Contain("gamma.dat"));

    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(r.Extract(byName["alpha.txt"]), Is.EqualTo(data1));
    Assert.That(r.Extract(byName["beta.bin"]),  Is.EqualTo(data2));
    Assert.That(r.Extract(byName["gamma.dat"]), Is.EqualTo(data3));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.MinixFs.MinixFsFormatDescriptor();
    Assert.That(desc.Id,             Is.EqualTo("MinixFs"));
    Assert.That(desc.DisplayName,    Is.EqualTo("Minix FS"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".minix"));
    Assert.That(desc.Extensions,     Does.Contain(".minix"));
    Assert.That(desc.Extensions,     Does.Contain(".img"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(5));

    // v3 magic 0x4D5A (LE: 5A 4D) at offset 1048
    var v3sig = desc.MagicSignatures.First(s => s.Offset == 1048);
    Assert.That(v3sig.Bytes, Is.EqualTo(new byte[] { 0x5A, 0x4D }));

    // v1 14-char magic 0x137F (LE: 7F 13) at offset 1040
    var v1sig = desc.MagicSignatures.First(s => s.Offset == 1040 && s.Bytes[0] == 0x7F);
    Assert.That(v1sig.Bytes, Is.EqualTo(new byte[] { 0x7F, 0x13 }));
  }

  [Test, Category("ErrorHandling")]
  public void BadMagic_Throws() {
    // Build a buffer large enough (>1048+2) with wrong magic bytes at both offsets
    var data = new byte[2048];
    // offset 1040: wrong v1 magic
    data[1040] = 0x00;
    data[1041] = 0x00;
    // offset 1048: wrong v3 magic
    data[1048] = 0x00;
    data[1049] = 0x00;

    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.MinixFs.MinixFsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[64]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.MinixFs.MinixFsReader(ms));
  }
}
