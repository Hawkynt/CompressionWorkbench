using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.Nilfs1;

[TestFixture]
public class Nilfs1DetectionTests {

  // Build a minimal NILFS v1 image: superblock at offset 1024, magic 0x3434 at +6, rev_level == 1.
  private static byte[] BuildMinimalImage(uint revLevel = 1) {
    var image = new byte[8 * 1024];
    var sb = 1024;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 0x00, 4), revLevel);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 0x06, 2), 0x3434);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 0x14, 4), 2); // log_block_size -> 4096
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x18, 8), 32);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x20, 8), 256UL * 1024);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 0x30, 4), 1024);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x38, 8), 7);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties_AndMagic() {
    var d = new FileSystem.Nilfs1.Nilfs1FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Nilfs1"));
    Assert.That(d.DisplayName, Is.EqualTo("NILFS v1"));
    Assert.That(d.Extensions, Does.Contain(".nilfs1"));
    Assert.That(d.Extensions, Does.Contain(".nilfs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    // Two signatures: the rev-1-discriminating one that beats NILFS2 on a v1
    // volume, and the bare shared magic below NILFS2's confidence.
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(1024));
    Assert.That(d.MagicSignatures[0].Mask, Is.Not.Null);
    Assert.That(d.MagicSignatures[1].Offset, Is.EqualTo(1030));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo(new byte[] { 0x34, 0x34 }));
    // Nilfs1 now ships a minimal writer (single segment + compact directory
    // index) so the descriptor advertises IArchiveCreatable. External NILFS v1
    // images that we didn't write ourselves still fall back to the surface-
    // metadata read path.
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Read_MinimalImage_SurfacesMetadataAndSuperblock() {
    using var ms = new MemoryStream(BuildMinimalImage(revLevel: 1));
    var r = new FileSystem.Nilfs1.Nilfs1Reader(ms);
    Assert.That(r.ValidSuperblock, Is.True);
    Assert.That(r.Magic, Is.EqualTo((ushort)0x3434));
    Assert.That(r.RevLevel, Is.EqualTo(1u));
    Assert.That(r.NumSegments, Is.EqualTo(32ul));
    Assert.That(r.BlocksPerSegment, Is.EqualTo(1024u));
    Assert.That(r.LastCheckpoint, Is.EqualTo(7ul));

    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.nilfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("superblock.bin"));
  }

  [Test, Category("Sad")]
  public void Read_RevLevel2_Refuses() {
    // s_rev_level == 2 means NILFS2 — Nilfs1 reader must refuse so detection routes to NILFS2.
    using var ms = new MemoryStream(BuildMinimalImage(revLevel: 2));
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Nilfs1.Nilfs1Reader(ms));
  }

  [Test, Category("Sad")]
  public void Nilfs2Reader_RevLevel1_Refuses() {
    // Cross-check the v1 guard in NILFS2 reader: a rev=1 image must be refused there too.
    using var ms = new MemoryStream(BuildMinimalImage(revLevel: 1));
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Nilfs2.Nilfs2Reader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListAndExtract() {
    var d = new FileSystem.Nilfs1.Nilfs1FormatDescriptor();
    using var ms = new MemoryStream(BuildMinimalImage(revLevel: 1));
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(3));

    var tmp = Path.Combine(Path.GetTempPath(), $"nilfs1-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      ms.Position = 0;
      d.Extract(ms, tmp, null, null);
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("format=NILFS v1"));
      Assert.That(meta, Does.Contain("rev_level=1"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }
}
