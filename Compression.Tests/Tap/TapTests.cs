namespace Compression.Tests.Tap;

[TestFixture]
public class TapTests {

  private static byte[] RoundTripSingle(string name, byte[] data, byte fileType = 3) {
    using var ms = new MemoryStream();
    var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
    w.AddFile(name, data, fileType);
    w.Finish();

    ms.Position = 0;
    var r = new FileFormat.Tap.TapReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    return r.Extract(r.Entries[0]);
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello ZX Spectrum!"u8.ToArray();
    using var ms = new MemoryStream();
    var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
    w.AddFile("hello", data);
    w.Finish();

    ms.Position = 0;
    var r = new FileFormat.Tap.TapReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var files = new (string Name, byte[] Data)[] {
      ("program",  "BASIC code here"u8.ToArray()),
      ("screen$",  "screen data here"u8.ToArray()),
      ("data",     new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }),
    };

    using var ms = new MemoryStream();
    var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    w.Finish();

    ms.Position = 0;
    var r = new FileFormat.Tap.TapReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));

    for (var i = 0; i < files.Length; i++) {
      Assert.That(r.Entries[i].Name, Is.EqualTo(files[i].Name));
      Assert.That(r.Extract(r.Entries[i]), Is.EqualTo(files[i].Data));
    }
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_FileTypes() {
    byte[] payload = [0xAA, 0xBB, 0xCC];

    foreach (var fileType in new byte[] { 0, 1, 2, 3 }) {
      using var ms = new MemoryStream();
      var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
      w.AddFile("test", payload, fileType);
      w.Finish();

      ms.Position = 0;
      var r = new FileFormat.Tap.TapReader(ms);
      Assert.That(r.Entries, Has.Count.EqualTo(1), $"fileType={fileType}");
      Assert.That(r.Entries[0].FileType, Is.EqualTo(fileType), $"fileType={fileType}");
      Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload), $"fileType={fileType}");
    }
  }

  [Test]
  public void Descriptor_Properties() {
    var desc = new FileFormat.Tap.TapFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Tap"));
    Assert.That(desc.Extensions, Contains.Item(".tap"));
    Assert.That(desc.MagicSignatures, Is.Empty);
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test]
  public void EmptyStream_NoEntries() {
    using var ms = new MemoryStream();
    var r = new FileFormat.Tap.TapReader(ms);
    Assert.That(r.Entries, Is.Empty);
  }

  // ── IArchiveModifiable ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveModifiable() {
    var desc = new FileFormat.Tap.TapFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveModifiable>());
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Add_ViaDescriptor_NewFileAppearsInListing() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
    w.AddFile("original", "old data"u8.ToArray());
    w.Finish();

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "new data"u8.ToArray());
      var desc = new FileFormat.Tap.TapFormatDescriptor();
      ((Compression.Registry.IArchiveModifiable)desc).Add(ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "added", false)]);

      ms.Position = 0;
      var r = new FileFormat.Tap.TapReader(ms);
      var names = r.Entries.Select(e => e.Name).ToList();
      Assert.That(names, Does.Contain("original"));
      Assert.That(names, Does.Contain("added"));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Remove_ViaDescriptor_FileDisappears() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
    w.AddFile("keep", "keep me"u8.ToArray());
    w.AddFile("drop", "drop me"u8.ToArray());
    w.Finish();

    var desc = new FileFormat.Tap.TapFormatDescriptor();
    ((Compression.Registry.IArchiveModifiable)desc).Remove(ms, ["drop"]);

    ms.Position = 0;
    var r = new FileFormat.Tap.TapReader(ms);
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("keep"));
    Assert.That(names, Does.Not.Contain("drop"));
  }

  [Test, Category("RoundTrip")]
  public void Add_ContentPreserved() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
    w.AddFile("first", "alpha"u8.ToArray());
    w.Finish();

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "beta"u8.ToArray());
      var desc = new FileFormat.Tap.TapFormatDescriptor();
      ((Compression.Registry.IArchiveModifiable)desc).Add(ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "second", false)]);

      ms.Position = 0;
      var r = new FileFormat.Tap.TapReader(ms);
      Assert.That(r.Extract(r.Entries.First(e => e.Name == "first")), Is.EqualTo("alpha"u8.ToArray()));
      Assert.That(r.Extract(r.Entries.First(e => e.Name == "second")), Is.EqualTo("beta"u8.ToArray()));
    } finally { File.Delete(tmp); }
  }

  // ── IArchiveDefragmentable ─────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveDefragmentable() {
    var desc = new FileFormat.Tap.TapFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveDefragmentable>());
  }

  [Test, Category("RoundTrip")]
  public void Defragment_PreservesAllFiles() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
    w.AddFile("A", "alpha"u8.ToArray());
    w.AddFile("B", "bravo"u8.ToArray());
    w.AddFile("C", "charlie"u8.ToArray());
    w.Finish();

    ((Compression.Registry.IArchiveDefragmentable)new FileFormat.Tap.TapFormatDescriptor()).Defragment(ms);

    ms.Position = 0;
    var r = new FileFormat.Tap.TapReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo("alpha"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("bravo"u8.ToArray()));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo("charlie"u8.ToArray()));
  }

  // ── IArchiveLayoutMap ──────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveLayoutMap() {
    var desc = new FileFormat.Tap.TapFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveLayoutMap>());
  }

  [Test, Category("RoundTrip")]
  public void EnumerateLayout_ReportsHeaderAndDataBlocks() {
    using var ms = new MemoryStream();
    var w = new FileFormat.Tap.TapWriter(ms, leaveOpen: true);
    w.AddFile("hello", "world"u8.ToArray());
    w.AddFile("data", new byte[64]);
    w.Finish();

    var desc = new FileFormat.Tap.TapFormatDescriptor();
    ms.Position = 0;
    var tiles = ((Compression.Registry.IArchiveLayoutMap)desc).EnumerateLayout(ms).ToList();

    Assert.That(tiles, Is.Not.Empty);
    Assert.That(tiles.Any(t => t.Kind == Compression.Registry.DefragBlockKind.MetadataReserved), Is.True,
      "Expected header blocks as MetadataReserved");
    Assert.That(tiles.Any(t => t.Kind == Compression.Registry.DefragBlockKind.Used
                                && t.FileName == "hello"), Is.True);
    Assert.That(tiles.Any(t => t.Kind == Compression.Registry.DefragBlockKind.Used
                                && t.FileName == "data"), Is.True);
  }
}
