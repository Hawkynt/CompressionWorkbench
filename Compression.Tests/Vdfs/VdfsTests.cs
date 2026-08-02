namespace Compression.Tests.Vdfs;

[TestFixture]
public class VdfsTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new FileSystem.Vdfs.VdfsWriter();
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    return w.Build();
  }

  // -------------------------------------------------------------------------
  // Descriptor property tests
  // -------------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.Vdfs.VdfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Vdfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".vdf"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("PSVDSC_V2.00"u8.ToArray()));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_CanCreate() {
    var desc = new FileSystem.Vdfs.VdfsFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_CanModify() {
    var desc = new FileSystem.Vdfs.VdfsFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanModify), Is.True);
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IsDefragmentable() {
    var desc = new FileSystem.Vdfs.VdfsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveDefragmentable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_HasExtentMap() {
    var desc = new FileSystem.Vdfs.VdfsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IFilesystemExtentMap>());
  }

  // -------------------------------------------------------------------------
  // Round-trip tests
  // -------------------------------------------------------------------------

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello VDFS!"u8.ToArray();
    var img = BuildImage(("hello.txt", data));

    using var ms = new MemoryStream(img);
    var r = new FileSystem.Vdfs.VdfsReader(ms);

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
    var r = new FileSystem.Vdfs.VdfsReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(3));
    Assert.That(r.Extract(files.First(e => e.Name == "a.txt")), Is.EqualTo(a));
    Assert.That(r.Extract(files.First(e => e.Name == "b.txt")), Is.EqualTo(b));
    Assert.That(r.Extract(files.First(e => e.Name == "c.bin")), Is.EqualTo(c));
  }

  [Test, Category("RoundTrip")]
  public void Create_ViaDescriptor_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "payload"u8.ToArray());
      var desc = new FileSystem.Vdfs.VdfsFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)desc).Create(ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "test.dat", false)],
        new Compression.Registry.FormatCreateOptions());

      ms.Position = 0;
      var r = new FileSystem.Vdfs.VdfsReader(ms);
      var files = r.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(files, Has.Count.EqualTo(1));
      Assert.That(files[0].Name, Is.EqualTo("test.dat"));
      Assert.That(r.Extract(files[0]), Is.EqualTo("payload"u8.ToArray()));
    } finally { File.Delete(tmp); }
  }

  // -------------------------------------------------------------------------
  // Add / Remove tests
  // -------------------------------------------------------------------------

  [Test, Category("RoundTrip")]
  public void AddRemove_RoundTrips() {
    var img = BuildImage(("keep.txt", "keep"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(img);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "added"u8.ToArray());
      var desc = new FileSystem.Vdfs.VdfsFormatDescriptor();

      // Add a file
      ((Compression.Registry.IArchiveModifiable)desc).Add(ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "new.txt", false)]);

      ms.Position = 0;
      var r = new FileSystem.Vdfs.VdfsReader(ms);
      var files = r.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(files.Any(e => e.Name == "keep.txt"), Is.True);
      Assert.That(files.Any(e => e.Name == "new.txt"), Is.True);

      // Remove the original
      ((Compression.Registry.IArchiveModifiable)desc).Remove(ms, ["keep.txt"]);
      ms.Position = 0;
      var r2 = new FileSystem.Vdfs.VdfsReader(ms);
      var files2 = r2.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(files2.Any(e => e.Name == "keep.txt"), Is.False);
      Assert.That(files2.Any(e => e.Name == "new.txt"), Is.True);
      Assert.That(r2.Extract(files2.First(e => e.Name == "new.txt")), Is.EqualTo("added"u8.ToArray()));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Add_ReplacesExistingFile() {
    var img = BuildImage(("file.txt", "original"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(img);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "replaced"u8.ToArray());
      var desc = new FileSystem.Vdfs.VdfsFormatDescriptor();
      ((Compression.Registry.IArchiveModifiable)desc).Add(ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "file.txt", false)]);

      ms.Position = 0;
      var r = new FileSystem.Vdfs.VdfsReader(ms);
      var files = r.Entries.Where(e => !e.IsDirectory).ToList();
      Assert.That(files, Has.Count.EqualTo(1));
      Assert.That(r.Extract(files[0]), Is.EqualTo("replaced"u8.ToArray()));
    } finally { File.Delete(tmp); }
  }

  // -------------------------------------------------------------------------
  // Defragment tests
  // -------------------------------------------------------------------------

  [Test, Category("RoundTrip")]
  public void Defragment_PreservesFiles() {
    var img = BuildImage(("a.txt", "alpha"u8.ToArray()), ("b.txt", "beta"u8.ToArray()));
    using var ms = new MemoryStream();
    ms.Write(img);

    new FileSystem.Vdfs.VdfsFormatDescriptor().Defragment(ms,
      new Compression.Registry.DefragOptions { Mode = Compression.Registry.DefragMode.ConsolidateAtStart });

    ms.Position = 0;
    var r = new FileSystem.Vdfs.VdfsReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Any(e => e.Name == "a.txt"), Is.True);
    Assert.That(files.Any(e => e.Name == "b.txt"), Is.True);
    Assert.That(r.Extract(files.First(e => e.Name == "a.txt")), Is.EqualTo("alpha"u8.ToArray()));
    Assert.That(r.Extract(files.First(e => e.Name == "b.txt")), Is.EqualTo("beta"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Defragment_NoArg_PreservesFiles() {
    var img = BuildImage(("x.dat", new byte[50]));
    using var ms = new MemoryStream();
    ms.Write(img);

    new FileSystem.Vdfs.VdfsFormatDescriptor().Defragment(ms);

    ms.Position = 0;
    var r = new FileSystem.Vdfs.VdfsReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("x.dat"));
  }

  // -------------------------------------------------------------------------
  // ExtentMap tests
  // -------------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void ExtentMap_ReturnsEntries() {
    var img = BuildImage(("x.bin", new byte[100]));
    using var ms = new MemoryStream(img);
    var desc = new FileSystem.Vdfs.VdfsFormatDescriptor();
    var extents = ((Compression.Registry.IFilesystemExtentMap)desc).EnumerateExtents(ms).ToList();
    Assert.That(extents, Has.Count.GreaterThan(0));
    Assert.That(extents.Any(e => e.Kind == Compression.Registry.DefragBlockKind.MetadataReserved), Is.True);
    Assert.That(extents.Any(e => e.Kind == Compression.Registry.DefragBlockKind.Used), Is.True);
  }

  [Test, Category("HappyPath")]
  public void ExtentMap_MetadataCoversHeaderAndEntryTable() {
    var img = BuildImage(("a.txt", "hello"u8.ToArray()), ("b.txt", "world"u8.ToArray()));
    using var ms = new MemoryStream(img);
    var desc = new FileSystem.Vdfs.VdfsFormatDescriptor();
    var extents = ((Compression.Registry.IFilesystemExtentMap)desc).EnumerateExtents(ms).ToList();

    // The header and the entry table are reserved where they actually are, not
    // as one span up to the first file. Adding a file relocates the table past
    // the data, and describing it as the front of the image left it looking
    // like free space — wiping such a volume zeroed it and every file went
    // missing. Here the two happen to be adjacent, and the sum is what the
    // single span used to be: 36 bytes of header plus two 80-byte entries.
    var meta = extents.Where(e => e.Kind == Compression.Registry.DefragBlockKind.MetadataReserved)
                      .ToList();
    Assert.That(meta.Sum(e => e.Length), Is.EqualTo(196));
    Assert.That(meta.Any(e => e.Offset == 0), Is.True, "The header is not reserved.");

    using var reader = new FileSystem.Vdfs.VdfsReader(new MemoryStream(img));
    Assert.That(meta.Any(e => e.Offset == reader.EntryTableOffset
                           && e.Length == reader.EntryTableLength), Is.True,
      "The entry table is not reserved where the header says it is.");

    var used = extents.Where(e => e.Kind == Compression.Registry.DefragBlockKind.Used).ToList();
    Assert.That(used, Has.Count.EqualTo(2));
  }

  // -------------------------------------------------------------------------
  // Error handling tests
  // -------------------------------------------------------------------------

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[10]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Vdfs.VdfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[100];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Vdfs.VdfsReader(ms));
  }
}
