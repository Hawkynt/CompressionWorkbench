namespace Compression.Tests.RomFs;

[TestFixture]
public class RomFsTests {

  private static byte[] BuildImage(params (string Path, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using var w = new FileSystem.RomFs.RomFsWriter(ms, leaveOpen: true);
    foreach (var (path, data) in files)
      w.AddFile(path, data);
    w.Finish();
    return ms.ToArray();
  }

  // -------------------------------------------------------------------------
  // Round-trip tests
  // -------------------------------------------------------------------------

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello ROMFS!"u8.ToArray();
    var img = BuildImage(("hello.txt", data));

    using var ms = new MemoryStream(img);
    var r = new FileSystem.RomFs.RomFsReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(r.Extract(files[0]), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var a = "First"u8.ToArray();
    var b = "Second"u8.ToArray();
    var c = new byte[200];
    Array.Fill(c, (byte)0xAB);

    var img = BuildImage(("a.txt", a), ("b.txt", b), ("c.bin", c));

    using var ms = new MemoryStream(img);
    var r = new FileSystem.RomFs.RomFsReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory).OrderBy(e => e.Name).ToList();
    Assert.That(files, Has.Count.EqualTo(3));
    Assert.That(r.Extract(files.First(e => e.Name == "a.txt")), Is.EqualTo(a));
    Assert.That(r.Extract(files.First(e => e.Name == "b.txt")), Is.EqualTo(b));
    Assert.That(r.Extract(files.First(e => e.Name == "c.bin")), Is.EqualTo(c));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_NestedDirectories() {
    var cfgData  = "key=value\n"u8.ToArray();
    var appData  = new byte[64];
    Array.Fill(appData, (byte)0x7F);

    var img = BuildImage(("etc/config.txt", cfgData), ("bin/app", appData));

    using var ms = new MemoryStream(img);
    var r = new FileSystem.RomFs.RomFsReader(ms);

    var allFiles = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(allFiles, Has.Count.EqualTo(2));

    var cfg = allFiles.FirstOrDefault(e => e.Name == "etc/config.txt");
    Assert.That(cfg, Is.Not.Null, "etc/config.txt not found");
    Assert.That(r.Extract(cfg!), Is.EqualTo(cfgData));

    var app = allFiles.FirstOrDefault(e => e.Name == "bin/app");
    Assert.That(app, Is.Not.Null, "bin/app not found");
    Assert.That(r.Extract(app!), Is.EqualTo(appData));
  }

  // -------------------------------------------------------------------------
  // Modify / Defragment / ExtentMap tests
  // -------------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsModifiable() {
    var desc = new FileSystem.RomFs.RomFsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveModifiable>());
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveDefragmentable>());
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IFilesystemExtentMap>());
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void AddRemove_RoundTrips() {
    var img = BuildImage(("keep.txt", "keep"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(img);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "added"u8.ToArray());
      var desc = new FileSystem.RomFs.RomFsFormatDescriptor();
      ((Compression.Registry.IArchiveModifiable)desc).Add(ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "new.txt", false)]);

      ms.Position = 0;
      var r = new FileSystem.RomFs.RomFsReader(ms);
      var files = r.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(files.Any(e => e.Name == "keep.txt"), Is.True);
      Assert.That(files.Any(e => e.Name == "new.txt"), Is.True);

      ((Compression.Registry.IArchiveModifiable)desc).Remove(ms, ["keep.txt"]);
      ms.Position = 0;
      var r2 = new FileSystem.RomFs.RomFsReader(ms);
      var files2 = r2.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(files2.Any(e => e.Name == "keep.txt"), Is.False);
      Assert.That(files2.Any(e => e.Name == "new.txt"), Is.True);
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Defragment_PreservesFiles() {
    var img = BuildImage(("a.txt", "alpha"u8.ToArray()), ("b.txt", "beta"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(img);

    new FileSystem.RomFs.RomFsFormatDescriptor().Defragment(ms,
      new Compression.Registry.DefragOptions { Mode = Compression.Registry.DefragMode.ConsolidateAtStart });

    ms.Position = 0;
    var r = new FileSystem.RomFs.RomFsReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Any(e => e.Name == "a.txt"), Is.True);
    Assert.That(files.Any(e => e.Name == "b.txt"), Is.True);
    Assert.That(r.Extract(files.First(e => e.Name == "a.txt")), Is.EqualTo("alpha"u8.ToArray()));
    Assert.That(r.Extract(files.First(e => e.Name == "b.txt")), Is.EqualTo("beta"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void ExtentMap_ReturnsEntries() {
    var img = BuildImage(("x.bin", new byte[100]));
    using var ms = new MemoryStream(img);
    var desc = new FileSystem.RomFs.RomFsFormatDescriptor();
    var extents = ((Compression.Registry.IFilesystemExtentMap)desc).EnumerateExtents(ms).ToList();
    Assert.That(extents, Has.Count.GreaterThan(0));
    Assert.That(extents.Any(e => e.Kind == Compression.Registry.DefragBlockKind.MetadataReserved), Is.True);
    Assert.That(extents.Any(e => e.Kind == Compression.Registry.DefragBlockKind.Used), Is.True);
  }

  // -------------------------------------------------------------------------
  // Descriptor tests
  // -------------------------------------------------------------------------

  [Test]
  public void Descriptor_Properties() {
    var d = new FileSystem.RomFs.RomFsFormatDescriptor();
    Assert.That(d.Id,               Is.EqualTo("RomFs"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".romfs"));
    Assert.That(d.MagicSignatures,  Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("-rom1fs-"u8.ToArray()));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate),  Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList),    Is.True);
  }

  // -------------------------------------------------------------------------
  // Error handling tests
  // -------------------------------------------------------------------------

  [Test]
  public void BadMagic_Throws() {
    var bad = new byte[512];
    Array.Fill(bad, (byte)0xFF);
    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.RomFs.RomFsReader(ms));
  }

  [Test]
  public void TooSmall_Throws() {
    var tiny = new byte[8]; // less than the 16-byte minimum
    using var ms = new MemoryStream(tiny);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.RomFs.RomFsReader(ms));
  }
}
