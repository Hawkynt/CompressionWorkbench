using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Nilfs2;

[TestFixture]
public class Nilfs2Tests {

  // Build a minimal NILFS2 image with a valid superblock header.
  // The superblock sits at file offset 1024 with magic 0x3434 at +6.
  private static byte[] BuildMinimalImage() {
    var image = new byte[8 * 1024];
    var sb = 1024;
    // u32 s_rev_level = 2
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 0x00, 4), 2);
    // u16 s_minor_rev_level = 0
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 0x04, 2), 0);
    // u16 s_magic = 0x3434
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 0x06, 2), 0x3434);
    // u16 s_bytes = 1024
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 0x08, 2), 1024);
    // u32 s_log_block_size = 2 (4096 bytes: 1<<(2+10))
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 0x14, 4), 2);
    // u64 s_nsegments = 64
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x18, 8), 64);
    // u64 s_dev_size = 1024*1024
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x20, 8), 1024UL * 1024);
    // u32 s_blocks_per_segment = 2048
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 0x30, 4), 2048);
    // u64 s_last_cno = 1
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x38, 8), 1);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Nilfs2.Nilfs2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Nilfs2"));
    Assert.That(d.DisplayName, Is.EqualTo("NILFS2"));
    Assert.That(d.Extensions, Does.Contain(".nilfs2"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(1030));
  }

  [Test, Category("HappyPath")]
  public void Read_MinimalSyntheticImage() {
    var img = BuildMinimalImage();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Nilfs2.Nilfs2Reader(ms);
    Assert.That(r.ValidSuperblock, Is.True);
    Assert.That(r.Magic, Is.EqualTo((ushort)0x3434));
    Assert.That(r.RevLevel, Is.EqualTo(2u));
    Assert.That(r.LogBlockSize, Is.EqualTo(2u));
    Assert.That(r.NumSegments, Is.EqualTo(64ul));
    Assert.That(r.BlocksPerSegment, Is.EqualTo(2048u));
    Assert.That(r.LastCheckpoint, Is.EqualTo(1ul));

    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.nilfs2"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("superblock.bin"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_Extract() {
    var img = BuildMinimalImage();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Nilfs2.Nilfs2FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(3));

    var tmp = Path.Combine(Path.GetTempPath(), $"nilfs2-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);
    try {
      ms.Position = 0;
      d.Extract(ms, tmp, null, null);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("format=NILFS2"));
      Assert.That(meta, Does.Contain("magic=0x3434"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("Sad")]
  public void InvalidMagic_Throws() {
    var img = new byte[2048];
    using var ms = new MemoryStream(img);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Nilfs2.Nilfs2Reader(ms));
  }

  [Test, Category("Sad")]
  public void Defragment_Throws() {
    var d = new FileSystem.Nilfs2.Nilfs2FormatDescriptor();
    using var ms = new MemoryStream(BuildMinimalImage());
    Assert.Throws<NotSupportedException>(() => d.Defragment(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IsRwCapable() {
    var d = new FileSystem.Nilfs2.Nilfs2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
      "NILFS2 ships R/W via continuous-snapshot segment-log append + last-cno bump.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }
}
