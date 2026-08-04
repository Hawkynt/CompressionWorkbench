using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Tux2;

[TestFixture]
public class Tux2Tests {

  // Build a minimal synthetic TUX2 image with two embedded files.
  private static byte[] BuildSyntheticImage((string name, byte[] data)[] files) {
    using var ms = new MemoryStream();
    ms.Write(FileSystem.Tux2.Tux2Reader.Magic);
    var hdr = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0, 4), 1u); // version
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4, 4), (uint)files.Length);
    ms.Write(hdr);
    foreach (var (name, data) in files) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      var rec = new byte[2];
      BinaryPrimitives.WriteUInt16LittleEndian(rec, (ushort)nameBytes.Length);
      ms.Write(rec);
      ms.Write(nameBytes);
      var sizeRec = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(sizeRec, (uint)data.Length);
      ms.Write(sizeRec);
      ms.Write(data);
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Tux2.Tux2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Tux2"));
    Assert.That(d.DisplayName, Is.EqualTo("TUX2"));
    Assert.That(d.Extensions, Does.Contain(".tux2"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Read_SyntheticImage() {
    var img = BuildSyntheticImage([
      ("hello.txt", "Hello TUX2!"u8.ToArray()),
      ("data.bin",  new byte[] { 1, 2, 3, 4, 5 }),
    ]);
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Tux2.Tux2Reader(ms);
    Assert.That(r.ValidHeader, Is.True);
    Assert.That(r.Version, Is.EqualTo(1u));
    Assert.That(r.FileCount, Is.EqualTo(2u));

    // Entries: FULL.tux2 + metadata.ini + 2 files
    Assert.That(r.Entries, Has.Count.EqualTo(4));
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(byName.ContainsKey("FULL.tux2"), Is.True);
    Assert.That(byName.ContainsKey("metadata.ini"), Is.True);
    Assert.That(byName.ContainsKey("hello.txt"), Is.True);
    Assert.That(byName.ContainsKey("data.bin"), Is.True);

    Assert.That(Encoding.UTF8.GetString(r.Extract(byName["hello.txt"])), Is.EqualTo("Hello TUX2!"));
    Assert.That(r.Extract(byName["data.bin"]), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildSyntheticImage([("one.txt", "ONE"u8.ToArray())]);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Tux2.Tux2FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Count, Is.GreaterThanOrEqualTo(3));

    var tmp = Path.Combine(Path.GetTempPath(), $"tux2-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      ms.Position = 0;
      d.Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "one.txt")), Is.True);
      Assert.That(File.ReadAllText(Path.Combine(tmp, "one.txt")), Is.EqualTo("ONE"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("Sad")]
  public void InvalidMagic_Throws() {
    using var ms = new MemoryStream(new byte[32]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Tux2.Tux2Reader(ms));
  }

  /// <summary>
  /// Carving a hole used to be refused outright, because the rebuild always
  /// packed from the front. The pass moves whole records now, so a container
  /// with no records in it has nothing to refuse: there is nothing to move and
  /// nothing to carve around.
  /// </summary>
  [Test]
  public void Defragment_CarveHole_OnAnEmptyContainer_DoesNothing() {
    var d = new FileSystem.Tux2.Tux2FormatDescriptor();
    var empty = BuildSyntheticImage([]);
    using var ms = new MemoryStream(empty.Length);
    ms.Write(empty, 0, empty.Length);

    ms.Position = 0;
    Assert.DoesNotThrow(() => d.Defragment(ms, new DefragOptions { Mode = DefragMode.CarveHole }));
    Assert.That(ms.ToArray(), Is.EqualTo(empty), "an empty container comes back byte for byte");
  }

  [Test, Category("HappyPath")]
  public void Defragment_PreservesFiles() {
    var descriptor = new FileSystem.Tux2.Tux2FormatDescriptor();
    var payload = new byte[5000];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)(i * 11);

    var path = Path.Combine(Path.GetTempPath(), "tux2_defrag_" + Guid.NewGuid().ToString("N"));
    var outDir = path + "_out";
    try {
      using (var create = File.Create(path))
        descriptor.Create(create, [ArchiveInputInfo.InMemory("data.bin", payload)], new FormatCreateOptions());
      using (var archive = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
        descriptor.Defragment(archive);

      Directory.CreateDirectory(outDir);
      using (var read = File.OpenRead(path))
        descriptor.Extract(read, outDir, null, ["data.bin"]);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "data.bin")), Is.EqualTo(payload));
    } finally {
      try { File.Delete(path); } catch { /* scratch file already gone */ }
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Implements_IArchiveCreatable() {
    var d = new FileSystem.Tux2.Tux2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }
}
