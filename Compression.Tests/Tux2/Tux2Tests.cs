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

  [Test, Category("Sad")]
  public void Defragment_Throws() {
    var d = new FileSystem.Tux2.Tux2FormatDescriptor();
    using var ms = new MemoryStream(BuildSyntheticImage([]));
    Assert.Throws<NotSupportedException>(() => d.Defragment(ms));
  }

  [Test, Category("HappyPath")]
  public void Implements_IArchiveCreatable() {
    var d = new FileSystem.Tux2.Tux2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }
}
