using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.Ext1;

[TestFixture]
public class Ext1Tests {

  /// <summary>
  /// Synthesises a minimal ext1 image: superblock at offset 1024 with the 0xEF51
  /// magic at offset 1080, plus the leading fields (inodes_count, blocks_count,
  /// reserved_blocks, free_blocks, free_inodes, first_data, log_block_size,
  /// blocks_per_group, inodes_per_group). Layout matches GOOD_OLD ext2 byte-for-byte.
  /// </summary>
  private static byte[] BuildMinimal() {
    var image = new byte[4096];
    var sbStart = 1024;

    // s_inodes_count @ +0
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sbStart + 0, 4), 256U);
    // s_blocks_count @ +4
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sbStart + 4, 4), 1024U);
    // s_r_blocks_count @ +8
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sbStart + 8, 4), 51U);
    // s_free_blocks_count @ +12
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sbStart + 12, 4), 900U);
    // s_free_inodes_count @ +16
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sbStart + 16, 4), 240U);
    // s_first_data_block @ +20
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sbStart + 20, 4), 1U);
    // s_log_block_size @ +24
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sbStart + 24, 4), 0U);
    // s_log_frag_size @ +28
    // s_blocks_per_group @ +32
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sbStart + 32, 4), 8192U);
    // s_frags_per_group @ +36
    // s_inodes_per_group @ +40
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sbStart + 40, 4), 256U);
    // s_magic @ +56 — 0xEF51 (ext1)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sbStart + 56, 2), 0xEF51);

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Ext1.Ext1FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ext1"));
    Assert.That(d.DisplayName, Is.EqualTo("ext1"));
    Assert.That(d.Extensions, Does.Contain(".ext1"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(1080));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x51, 0xEF }));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.9).Within(0.01));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Ext1.Ext1FormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.ext1"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("superblock.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeader() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Ext1.Ext1FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "ext1_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.ext1")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "superblock.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      Assert.That(meta, Does.Contain("magic=0xEF51"));
      Assert.That(meta, Does.Contain("inodes_count=256"));
      Assert.That(meta, Does.Contain("blocks_count=1024"));
      Assert.That(meta, Does.Contain("free_blocks_count=900"));
      Assert.That(meta, Does.Contain("free_inodes_count=240"));
      Assert.That(meta, Does.Contain("log_block_size=0"));
      Assert.That(meta, Does.Contain("block_size=1024"));
      Assert.That(meta, Does.Contain("blocks_per_group=8192"));
      Assert.That(meta, Does.Contain("inodes_per_group=256"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_NoMagic_DoesNotThrow() {
    // ext2 magic 0xEF53 — should NOT match ext1.
    var image = new byte[4096];
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(1080, 2), 0xEF53);
    using var ms = new MemoryStream(image);
    var d = new FileSystem.Ext1.Ext1FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.ext1"));
    Assert.That(entries.Select(e => e.Name), Does.Not.Contain("superblock.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void List_TinyImage_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[16]);
    var d = new FileSystem.Ext1.Ext1FormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_FallsBackToFull() {
    var rnd = new Random(7);
    var buf = new byte[4096];
    rnd.NextBytes(buf);
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.Ext1.Ext1FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.ext1"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }
}
