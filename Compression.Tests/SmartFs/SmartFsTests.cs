using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.SmartFs;

[TestFixture]
public class SmartFsTests {

  // Build a minimal SmartFS format sector: SMRT signature at offset 10,
  // format version 1, sector size code 2 (1024-byte sectors), 1 root sector.
  private static byte[] BuildMinimalImage() {
    var image = new byte[4096];
    // 10 bytes of leading per-sector + reserved header (zeroed)
    "SMRT"u8.ToArray().CopyTo(image.AsSpan(10));
    image[14] = 1;   // format version
    image[15] = 2;   // sector size code => 1024
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16, 2), 1); // root sector count
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.SmartFs.SmartFsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("SmartFs"));
    Assert.That(d.DisplayName, Is.EqualTo("SmartFS"));
    Assert.That(d.Extensions, Does.Contain(".smartfs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("SMRT"u8.ToArray()));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(10));
  }

  [Test, Category("HappyPath")]
  public void Read_MinimalSyntheticImage() {
    var img = BuildMinimalImage();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.SmartFs.SmartFsReader(ms);
    Assert.That(r.ValidFormatSector, Is.True);
    Assert.That(r.FormatVersion, Is.EqualTo((byte)1));
    Assert.That(r.SectorSize, Is.EqualTo(1024u));
    Assert.That(r.RootSectorCount, Is.EqualTo((ushort)1));

    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.smartfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalImage();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.SmartFs.SmartFsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));

    var tmp = Path.Combine(Path.GetTempPath(), $"smartfs-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      ms.Position = 0;
      d.Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("format=SmartFS"));
      Assert.That(meta, Does.Contain("sector_size=1024"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("Sad")]
  public void InvalidMagic_Throws() {
    var img = new byte[4096];
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.SmartFs.SmartFsReader(ms));
  }

  /// <summary>
  /// The descriptor used to refuse both verbs for want of a writer. It has one
  /// now: it lays a volume out in the state mksmartfs leaves behind, so a
  /// defragmentation is a fresh layout of the same files.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Defragment_RelaysTheVolume_AndKeepsItsFiles() {
    var d = new FileSystem.SmartFs.SmartFsFormatDescriptor();
    var writer = new FileSystem.SmartFs.SmartFsWriter();
    writer.AddFile("A.BIN", [1, 2, 3, 4]);
    writer.AddFile("B.BIN", [9, 9, 9]);

    using var ms = new MemoryStream();
    ms.Write(writer.Build());
    ms.Position = 0;
    var before = d.List(ms, null).Select(e => e.Name).ToHashSet();

    ms.Position = 0;
    d.Defragment(ms);

    ms.Position = 0;
    Assert.That(d.List(ms, null).Select(e => e.Name).ToHashSet(), Is.EquivalentTo(before));
  }

  [Test, Category("HappyPath")]
  public void Creatable_Interface() {
    var d = new FileSystem.SmartFs.SmartFsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }
}
