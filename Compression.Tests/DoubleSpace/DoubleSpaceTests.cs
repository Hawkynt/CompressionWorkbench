namespace Compression.Tests.DoubleSpace;

[TestFixture]
public class DoubleSpaceTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello DoubleSpace!"u8.ToArray();
    var w = new FileFormat.DoubleSpace.DoubleSpaceWriter();
    w.AddFile("TEST.TXT", data);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileFormat.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("TEST.TXT"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileFormat.DoubleSpace.DoubleSpaceWriter();
    w.AddFile("A.TXT", "First file"u8.ToArray());
    w.AddFile("B.TXT", "Second file"u8.ToArray());
    w.AddFile("C.BIN", new byte[200]);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileFormat.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("First file"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("Second file"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(new byte[200]));
  }

  [Test, Category("HappyPath")]
  public void DsCompression_RoundTrip() {
    var original = "The quick brown fox jumps over the lazy dog. The quick brown fox."u8.ToArray();
    var compressed = FileFormat.DoubleSpace.DsCompression.Compress(original);
    var decompressed = FileFormat.DoubleSpace.DsCompression.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void DsCompression_RoundTrip_AllZeros() {
    var original = new byte[512];
    var compressed = FileFormat.DoubleSpace.DsCompression.Compress(original);
    var decompressed = FileFormat.DoubleSpace.DsCompression.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void DsCompression_RoundTrip_Random() {
    var original = new byte[512];
    new Random(42).NextBytes(original);
    var compressed = FileFormat.DoubleSpace.DsCompression.Compress(original);
    var decompressed = FileFormat.DoubleSpace.DsCompression.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void DoubleSpace_Descriptor_Properties() {
    var desc = new FileFormat.DoubleSpace.DoubleSpaceFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("DoubleSpace"));
    Assert.That(desc.DisplayName, Is.EqualTo("DoubleSpace CVF"));
    Assert.That(desc.Extensions, Does.Contain(".cvf"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(3));
    Assert.That(desc.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
  }

  [Test, Category("HappyPath")]
  public void DriveSpace_Descriptor_Properties() {
    var desc = new FileFormat.DoubleSpace.DriveSpaceFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("DriveSpace"));
    Assert.That(desc.DisplayName, Is.EqualTo("DriveSpace CVF"));
    Assert.That(desc.Extensions, Does.Contain(".cvf"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.Description, Does.Contain("DriveSpace"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.DoubleSpace.DoubleSpaceReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[1024];
    data[0] = 0xEB; data[1] = 0x3C; data[2] = 0x90;
    System.Text.Encoding.ASCII.GetBytes("BADMAGIC").CopyTo(data, 3);
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.DoubleSpace.DoubleSpaceReader(ms));
  }

  [Test, Category("EdgeCase")]
  public void EmptyDisk_NoEntries() {
    var w = new FileFormat.DoubleSpace.DoubleSpaceWriter();
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileFormat.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ViaInterface() {
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, new byte[10]);
      var desc = new FileFormat.DoubleSpace.DoubleSpaceFormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "TEST.TXT", false)], new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmpFile);
    }
  }

  [Test, Category("RoundTrip")]
  public void DriveSpace_RoundTrip() {
    var data = "DriveSpace test data"u8.ToArray();
    var w = new FileFormat.DoubleSpace.DoubleSpaceWriter { DriveSpace = true };
    w.AddFile("DS.TXT", data);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileFormat.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.IsDriveSpace, Is.True);
    Assert.That(r.Signature, Is.EqualTo("MSDSP6.2"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void DoubleSpace_Signature() {
    var w = new FileFormat.DoubleSpace.DoubleSpaceWriter { DriveSpace = false };
    w.AddFile("X.TXT", new byte[1]);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new FileFormat.DoubleSpace.DoubleSpaceReader(ms);
    Assert.That(r.IsDriveSpace, Is.False);
    Assert.That(r.Signature, Is.EqualTo("MSDSP6.0"));
  }
}
