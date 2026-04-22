namespace Compression.Tests.Pak;

[TestFixture]
public class PakDescriptorTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Pak.PakFormatDescriptor();

    Assert.That(desc.Id, Is.EqualTo("Pak"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".pak"));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(desc.Description, Does.Contain("Quake"));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_ViaInterface() {
    var tmpFile = Path.GetTempFileName();
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      var data = "Hello PAK archive!"u8.ToArray();
      File.WriteAllBytes(tmpFile, data);

      var desc = new FileFormat.Pak.PakFormatDescriptor();
      var ops = desc;

      // Create
      using var ms = new MemoryStream();
      ops.Create(ms, [new Compression.Registry.ArchiveInputInfo(tmpFile, "test.txt", false)],
        new Compression.Registry.FormatCreateOptions());

      // List
      ms.Position = 0;
      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("test.txt"));

      // Extract
      ms.Position = 0;
      ops.Extract(ms, tmpDir, null, null);
      var extracted = File.ReadAllBytes(Path.Combine(tmpDir, "test.txt"));
      Assert.That(extracted, Is.EqualTo(data));
    } finally {
      File.Delete(tmpFile);
      if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var tmpFiles = new string[3];
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      var data1 = "First file content"u8.ToArray();
      var data2 = "Second file content"u8.ToArray();
      var data3 = "Third file content"u8.ToArray();

      tmpFiles[0] = Path.GetTempFileName();
      tmpFiles[1] = Path.GetTempFileName();
      tmpFiles[2] = Path.GetTempFileName();
      File.WriteAllBytes(tmpFiles[0], data1);
      File.WriteAllBytes(tmpFiles[1], data2);
      File.WriteAllBytes(tmpFiles[2], data3);

      var desc = new FileFormat.Pak.PakFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [
        new Compression.Registry.ArchiveInputInfo(tmpFiles[0], "a.txt", false),
        new Compression.Registry.ArchiveInputInfo(tmpFiles[1], "b.txt", false),
        new Compression.Registry.ArchiveInputInfo(tmpFiles[2], "c.txt", false),
      ], new Compression.Registry.FormatCreateOptions());

      // List
      ms.Position = 0;
      var entries = ops.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(3));
      Assert.That(entries[0].Name, Is.EqualTo("a.txt"));
      Assert.That(entries[1].Name, Is.EqualTo("b.txt"));
      Assert.That(entries[2].Name, Is.EqualTo("c.txt"));

      // Extract all and verify
      ms.Position = 0;
      ops.Extract(ms, tmpDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmpDir, "a.txt")), Is.EqualTo(data1));
      Assert.That(File.ReadAllBytes(Path.Combine(tmpDir, "b.txt")), Is.EqualTo(data2));
      Assert.That(File.ReadAllBytes(Path.Combine(tmpDir, "c.txt")), Is.EqualTo(data3));
    } finally {
      foreach (var f in tmpFiles) if (f != null) File.Delete(f);
      if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_WithFilter() {
    var tmpFiles = new string[3];
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      var data1 = "Alpha"u8.ToArray();
      var data2 = "Bravo"u8.ToArray();
      var data3 = "Charlie"u8.ToArray();

      tmpFiles[0] = Path.GetTempFileName();
      tmpFiles[1] = Path.GetTempFileName();
      tmpFiles[2] = Path.GetTempFileName();
      File.WriteAllBytes(tmpFiles[0], data1);
      File.WriteAllBytes(tmpFiles[1], data2);
      File.WriteAllBytes(tmpFiles[2], data3);

      var desc = new FileFormat.Pak.PakFormatDescriptor();
      var ops = desc;

      using var ms = new MemoryStream();
      ops.Create(ms, [
        new Compression.Registry.ArchiveInputInfo(tmpFiles[0], "alpha.txt", false),
        new Compression.Registry.ArchiveInputInfo(tmpFiles[1], "bravo.txt", false),
        new Compression.Registry.ArchiveInputInfo(tmpFiles[2], "charlie.txt", false),
      ], new Compression.Registry.FormatCreateOptions());

      // Extract only bravo.txt
      ms.Position = 0;
      ops.Extract(ms, tmpDir, null, ["bravo.txt"]);

      Assert.That(File.Exists(Path.Combine(tmpDir, "bravo.txt")), Is.True);
      Assert.That(File.ReadAllBytes(Path.Combine(tmpDir, "bravo.txt")), Is.EqualTo(data2));
      Assert.That(File.Exists(Path.Combine(tmpDir, "alpha.txt")), Is.False);
      Assert.That(File.Exists(Path.Combine(tmpDir, "charlie.txt")), Is.False);
    } finally {
      foreach (var f in tmpFiles) if (f != null) File.Delete(f);
      if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
    }
  }
}
