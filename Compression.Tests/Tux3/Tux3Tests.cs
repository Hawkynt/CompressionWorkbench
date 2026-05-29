using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.Tux3;

[TestFixture]
public class Tux3Tests {

  // Build a minimal TUX3 image with valid superblock at offset 4096.
  private static byte[] BuildMinimalImage() {
    var image = new byte[8 * 1024];
    var sb = 4096;
    FileSystem.Tux3.Tux3Reader.Magic.CopyTo(image.AsSpan(sb));
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x08, 8), 0x1234_5678_9ABC_DEF0UL); // birthday
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x10, 8), 0x0000_0000_0000_0001UL); // flags
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x18, 8), 100UL); // iroot
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x20, 8), 200UL); // oroot
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x28, 8), 300UL); // aroot
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x30, 8), 12UL);  // blockbits => 4096
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x38, 8), 1024UL); // volblocks
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x40, 8), 512UL);  // freeblocks
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Tux3.Tux3FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Tux3"));
    Assert.That(d.DisplayName, Is.EqualTo("TUX3"));
    Assert.That(d.Extensions, Does.Contain(".tux3"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(4096));
  }

  [Test, Category("HappyPath")]
  public void Read_MinimalSyntheticImage() {
    var img = BuildMinimalImage();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Tux3.Tux3Reader(ms);
    Assert.That(r.ValidSuperblock, Is.True);
    Assert.That(r.Birthday, Is.EqualTo(0x1234_5678_9ABC_DEF0UL));
    Assert.That(r.IRoot, Is.EqualTo(100UL));
    Assert.That(r.BlockBits, Is.EqualTo(12UL));
    Assert.That(r.VolBlocks, Is.EqualTo(1024UL));
    Assert.That(r.FreeBlocks, Is.EqualTo(512UL));

    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.tux3"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("superblock.bin"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalImage();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Tux3.Tux3FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(3));

    var tmp = Path.Combine(Path.GetTempPath(), $"tux3-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      ms.Position = 0;
      d.Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("format=TUX3"));
      Assert.That(meta, Does.Contain("blockbits=12"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("Sad")]
  public void InvalidMagic_Throws() {
    var img = new byte[8 * 1024];
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Tux3.Tux3Reader(ms));
  }

  [Test, Category("Sad")]
  public void Defragment_Throws() {
    var d = new FileSystem.Tux3.Tux3FormatDescriptor();
    using var ms = new MemoryStream(BuildMinimalImage());
    Assert.Throws<NotSupportedException>(() => d.Defragment(ms));
  }

  [Test, Category("Sad")]
  public void NoCreatable_Interface() {
    var d = new FileSystem.Tux3.Tux3FormatDescriptor();
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }
}
