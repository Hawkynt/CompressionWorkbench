using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Ext;

namespace Compression.Tests.Ext;

/// <summary>
/// Smoke coverage for <see cref="ExtFormatDescriptor"/>'s schema wiring —
/// verifies the published knobs match the spec and that Create() honours a
/// non-default Version selection by inspecting the superblock
/// s_feature_incompat field.
/// </summary>
[TestFixture]
public class ExtSchemaTests {

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsFormatOptionsSchema() {
    var desc = new ExtFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IFormatOptionsSchema>());
  }

  [Test, Category("HappyPath")]
  public void Schema_ContainsExpectedKeys() {
    var desc = (IFormatOptionsSchema)new ExtFormatDescriptor();
    var keys = desc.OptionsSchema.Select(o => o.Key).ToHashSet();
    Assert.That(keys, Does.Contain("Version"));
    Assert.That(keys, Does.Contain("BlockSize"));
    Assert.That(keys, Does.Contain("Journal"));
    Assert.That(keys, Does.Contain("VolumeLabel"));
    Assert.That(keys, Does.Contain("InodeSize"));

    var journalOpt = desc.OptionsSchema.First(o => o.Key == "Journal");
    Assert.That(journalOpt.Kind, Is.EqualTo(FormatOptionKind.Boolean));
    Assert.That(journalOpt.DependsOn, Is.EqualTo("Version=ext3|ext4"));
  }

  [Test, Category("HappyPath")]
  public void Create_WithVersionExt2_OmitsExtentsAndJournalFlags() {
    // Superblock at offset 1024:
    //   s_feature_compat   at SB+92  (4 bytes LE) → must NOT contain HAS_JOURNAL (0x4) for ext2
    //   s_feature_incompat at SB+96  (4 bytes LE) → must NOT contain EXTENTS (0x40) or 64BIT (0x80) for ext2
    var desc = new ExtFormatDescriptor();
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, "hi"u8.ToArray());
      var opts = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> {
          ["Version"] = "ext2",
        },
      };
      using var ms = new MemoryStream();
      desc.Create(ms, [new ArchiveInputInfo(tmpFile, "test.txt", false)], opts);
      var image = ms.ToArray();

      var compatFlags = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 92, 4));
      var incompatFlags = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 96, 4));

      Assert.That(compatFlags & 0x4u, Is.EqualTo(0u), "ext2 must not advertise HAS_JOURNAL.");
      Assert.That(incompatFlags & 0x40u, Is.EqualTo(0u), "ext2 must not advertise EXTENTS.");
      Assert.That(incompatFlags & 0x80u, Is.EqualTo(0u), "ext2 must not advertise 64BIT.");

      // Round-trip sanity: even with the ext2 flag set, the resulting image
      // should still list one file through the descriptor's reader.
      using var rs = new MemoryStream(image);
      var entries = desc.List(rs, null);
      Assert.That(entries, Has.Count.EqualTo(1));
    } finally {
      File.Delete(tmpFile);
    }
  }

  [Test, Category("HappyPath")]
  public void Create_WithVersionExt4Default_SetsExtentsAndJournalFlags() {
    // Default Version (= ext4) flips on HAS_JOURNAL + EXTENTS + 64BIT. The last of
    // those mandates 64-byte block group descriptors, which the writer emits and
    // declares in s_desc_size; mke2fs has turned it on by default for years, and a
    // volume without it is one an ordinary mkfs.ext4 would not have made.
    var desc = new ExtFormatDescriptor();
    var tmpFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpFile, "hi"u8.ToArray());
      using var ms = new MemoryStream();
      desc.Create(ms, [new ArchiveInputInfo(tmpFile, "test.txt", false)], new FormatCreateOptions());
      var image = ms.ToArray();

      var compatFlags = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 92, 4));
      var incompatFlags = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 96, 4));

      Assert.That(compatFlags & 0x4u, Is.EqualTo(0x4u), "ext4 default must advertise HAS_JOURNAL.");
      Assert.That(incompatFlags & 0x40u, Is.EqualTo(0x40u), "ext4 default must advertise EXTENTS.");
      Assert.That(incompatFlags & 0x80u, Is.EqualTo(0x80u), "ext4 default must advertise 64BIT.");
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(1024 + 254, 2)), Is.EqualTo(64),
        "A 64BIT volume has to say how wide its group descriptors are.");
    } finally {
      File.Delete(tmpFile);
    }
  }
}
