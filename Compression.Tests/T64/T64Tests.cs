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

  // ── IArchiveModifiable ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveModifiable() {
    var desc = new FileFormat.T64.T64FormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveModifiable>());
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Add_ViaDescriptor_NewFileAppearsInListing() {
    var w = new FileFormat.T64.T64Writer();
    w.AddFile("ORIGINAL", "old data"u8.ToArray());
    var tape = w.Build();
    using var ms = new MemoryStream();
    ms.Write(tape);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "new data"u8.ToArray());
      var desc = new FileFormat.T64.T64FormatDescriptor();
      ((Compression.Registry.IArchiveModifiable)desc).Add(ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "ADDED", false)]);

      ms.Position = 0;
      var r = new FileFormat.T64.T64Reader(ms);
      var names = r.Entries.Select(e => e.Name).ToList();
      Assert.That(names, Does.Contain("ORIGINAL"));
      Assert.That(names, Does.Contain("ADDED"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Remove_ViaDescriptor_FileDisappears() {
    var w = new FileFormat.T64.T64Writer();
    w.AddFile("KEEP", "keep"u8.ToArray());
    w.AddFile("DROP", "drop"u8.ToArray());
    var tape = w.Build();
    using var ms = new MemoryStream();
    ms.Write(tape);

    var desc = new FileFormat.T64.T64FormatDescriptor();
    ((Compression.Registry.IArchiveModifiable)desc).Remove(ms, ["DROP"]);

    ms.Position = 0;
    var r = new FileFormat.T64.T64Reader(ms);
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("KEEP"));
    Assert.That(names, Does.Not.Contain("DROP"));
  }

  [Test, Category("RoundTrip")]
  public void Add_ContentPreserved() {
    var w = new FileFormat.T64.T64Writer();
    w.AddFile("FIRST", "alpha"u8.ToArray());
    var tape = w.Build();
    using var ms = new MemoryStream();
    ms.Write(tape);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "beta"u8.ToArray());
      var desc = new FileFormat.T64.T64FormatDescriptor();
      ((Compression.Registry.IArchiveModifiable)desc).Add(ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "SECOND", false)]);

      ms.Position = 0;
      var r = new FileFormat.T64.T64Reader(ms);
      Assert.That(r.Extract(r.Entries.First(e => e.Name == "FIRST")), Is.EqualTo("alpha"u8.ToArray()));
      Assert.That(r.Extract(r.Entries.First(e => e.Name == "SECOND")), Is.EqualTo("beta"u8.ToArray()));
    } finally { File.Delete(tmp); }
  }

  // ── IArchiveDefragmentable ─────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveDefragmentable() {
    var desc = new FileFormat.T64.T64FormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveDefragmentable>());
  }

  [Test, Category("RoundTrip")]
  public void Defragment_PreservesAllFiles() {
    var w = new FileFormat.T64.T64Writer();
    w.AddFile("A", "alpha"u8.ToArray());
    w.AddFile("B", "bravo"u8.ToArray());
    w.AddFile("C", "charlie"u8.ToArray());
    var tape = w.Build();
    using var ms = new MemoryStream();
    ms.Write(tape);

    ((Compression.Registry.IArchiveDefragmentable)new FileFormat.T64.T64FormatDescriptor()).Defragment(ms);

    ms.Position = 0;
    var r = new FileFormat.T64.T64Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("alpha"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("bravo"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo("charlie"u8.ToArray()));
  }

  // ── IArchiveLayoutMap ──────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveLayoutMap() {
    var desc = new FileFormat.T64.T64FormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveLayoutMap>());
  }

  [Test, Category("RoundTrip")]
  public void EnumerateLayout_ReportsHeaderAndEntries() {
    var w = new FileFormat.T64.T64Writer();
    w.AddFile("HELLO", "world"u8.ToArray());
    w.AddFile("BIG", new byte[256]);
    var tape = w.Build();
    using var ms = new MemoryStream(tape);

    var desc = new FileFormat.T64.T64FormatDescriptor();
    var tiles = ((Compression.Registry.IArchiveLayoutMap)desc).EnumerateLayout(ms).ToList();

    Assert.That(tiles, Is.Not.Empty);
    Assert.That(tiles.Any(t => t.Kind == Compression.Registry.DefragBlockKind.MetadataReserved), Is.True);
    Assert.That(tiles.Any(t => t.Kind == Compression.Registry.DefragBlockKind.Used
                                && t.FileName == "HELLO"), Is.True);
    Assert.That(tiles.Any(t => t.Kind == Compression.Registry.DefragBlockKind.Used
                                && t.FileName == "BIG"), Is.True);
  }
}
