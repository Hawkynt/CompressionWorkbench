using System.Text;

namespace Compression.Tests.Iso;

[TestFixture]
public class IsoTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SingleFile() {
    var data = "Hello ISO!"u8.ToArray();
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("TEST.TXT", data);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Iso.IsoReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Does.Contain("TEST"));
    Assert.That(r.Entries[0].IsDirectory, Is.False);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("A.TXT", "First"u8.ToArray());
    w.AddFile("B.TXT", "Second"u8.ToArray());
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Iso.IsoReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_EmptyImage() {
    var w = new FileSystem.Iso.IsoWriter();
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Iso.IsoReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_LargeFile() {
    var data = new byte[5000];
    new Random(42).NextBytes(data);
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("BIG.DAT", data);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Iso.IsoReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.Iso.IsoFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Iso"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".iso"));
    Assert.That(desc.Extensions, Does.Contain(".iso"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(3));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(0x8001));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(desc.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("FILE.BIN", new byte[10]);
    var image = w.Build();
    using var ms = new MemoryStream(image);

    var desc = new FileSystem.Iso.IsoFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Iso.IsoReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[20 * 2048];
    data[16 * 2048] = 1; // PVD type byte
    // No CD001 magic
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Iso.IsoReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Magic_Detection() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("X.TXT", new byte[1]);
    var image = w.Build();

    // Verify CD001 exists at offset 0x8001
    Assert.That(Encoding.ASCII.GetString(image, 0x8001, 5), Is.EqualTo("CD001"));
  }

  // ArchiveInputInfo takes a path on disk; the modifiable tests write each
  // payload to a temp file, then point the InputInfo at it.
  private static (string TmpFile, Compression.Registry.ArchiveInputInfo Info) MakeInput(string archiveName, byte[] data) {
    var tmp = Path.GetTempFileName();
    File.WriteAllBytes(tmp, data);
    return (tmp, new Compression.Registry.ArchiveInputInfo(tmp, archiveName, false));
  }

  [Test, Category("RoundTrip")]
  public void Modifiable_Add_AppendsNewFile() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("EXISTING.TXT", "old"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    var (tmp, info) = MakeInput("NEW.TXT", "added"u8.ToArray());
    try {
      new FileSystem.Iso.IsoFormatDescriptor().Add(ms, [info]);

      ms.Position = 0;
      var reader = new FileSystem.Iso.IsoReader(ms);
      Assert.That(reader.Entries, Has.Count.EqualTo(2));
      var byName = reader.Entries.ToDictionary(e => e.Name, e => reader.Extract(e));
      Assert.That(byName["EXISTING.TXT"], Is.EqualTo("old"u8.ToArray()));
      Assert.That(byName["NEW.TXT"], Is.EqualTo("added"u8.ToArray()));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Modifiable_Add_ReplacesByName() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("DOC.TXT", "v1"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    var (tmp, info) = MakeInput("DOC.TXT", "v2-replacement"u8.ToArray());
    try {
      new FileSystem.Iso.IsoFormatDescriptor().Add(ms, [info]);

      ms.Position = 0;
      var reader = new FileSystem.Iso.IsoReader(ms);
      Assert.That(reader.Entries, Has.Count.EqualTo(1), "replacement should not duplicate the entry");
      Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo("v2-replacement"u8.ToArray()));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Modifiable_Remove_DropsTargetEntry() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("KEEP.TXT", "stay"u8.ToArray());
    w.AddFile("DROP.TXT", "go"u8.ToArray());
    w.AddFile("ALSO.TXT", "stay too"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    new FileSystem.Iso.IsoFormatDescriptor().Remove(ms, ["DROP.TXT"]);

    ms.Position = 0;
    var reader = new FileSystem.Iso.IsoReader(ms);
    Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "KEEP.TXT", "ALSO.TXT" }));
  }

  [Test, Category("RoundTrip")]
  public void Modifiable_Remove_WipesTargetBytes() {
    // The rebuild path must leave no recoverable trace of the removed file.
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("KEEP.TXT", "harmless"u8.ToArray());
    w.AddFile("SECRET.TXT", "TOPSECRET-MARKER-ABC123"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    new FileSystem.Iso.IsoFormatDescriptor().Remove(ms, ["SECRET.TXT"]);

    var bytes = ms.ToArray();
    var asAscii = Encoding.ASCII.GetString(bytes);
    Assert.That(asAscii, Does.Not.Contain("TOPSECRET-MARKER-ABC123"));
  }

  [Test, Category("RoundTrip")]
  public void Modifiable_AddRemove_Sequence_RoundTrips() {
    var w = new FileSystem.Iso.IsoWriter();
    w.AddFile("A.TXT", "alpha"u8.ToArray());
    using var ms = new MemoryStream();
    ms.Write(w.Build());

    var (tmp1, info1) = MakeInput("B.TXT", "beta"u8.ToArray());
    var (tmp2, info2) = MakeInput("C.TXT", "gamma"u8.ToArray());
    try {
      var desc = new FileSystem.Iso.IsoFormatDescriptor();
      desc.Add(ms, [info1]);
      desc.Add(ms, [info2]);
      desc.Remove(ms, ["A.TXT"]);

      ms.Position = 0;
      var reader = new FileSystem.Iso.IsoReader(ms);
      Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "B.TXT", "C.TXT" }));
      var byName = reader.Entries.ToDictionary(e => e.Name, e => Encoding.ASCII.GetString(reader.Extract(e)));
      Assert.That(byName["B.TXT"], Is.EqualTo("beta"));
      Assert.That(byName["C.TXT"], Is.EqualTo("gamma"));
    } finally {
      File.Delete(tmp1);
      File.Delete(tmp2);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_DeclaresIArchiveModifiable() {
    var desc = new FileSystem.Iso.IsoFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveModifiable>());
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanModify), Is.True);
  }
}
