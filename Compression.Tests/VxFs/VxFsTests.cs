using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.VxFs;

[TestFixture]
public class VxFsTests {

  /// <summary>Synthesises a minimal VxFS image with the superblock at offset 1024.</summary>
  private static byte[] BuildMinimal(bool bigEndian = false, int version = 7) {
    var image = new byte[8 * 1024]; // 8 KB image, superblock at 1024.
    var sb = image.AsSpan(FileSystem.VxFs.VxFsReader.SuperblockOffset);

    if (bigEndian) {
      BinaryPrimitives.WriteUInt32BigEndian(sb[..4], FileSystem.VxFs.VxFsReader.Magic);
      BinaryPrimitives.WriteInt32BigEndian(sb.Slice(4, 4), version);
      BinaryPrimitives.WriteUInt32BigEndian(sb.Slice(8, 4), 0x6500_0000u);  // vs_mtime
      BinaryPrimitives.WriteUInt32BigEndian(sb.Slice(12, 4), 0x6400_0000u); // vs_ctime
      BinaryPrimitives.WriteInt32BigEndian(sb.Slice(24, 4), 1024);          // vs_bsize
      BinaryPrimitives.WriteInt32BigEndian(sb.Slice(28, 4), 0x10_0000);     // vs_size
      BinaryPrimitives.WriteInt32BigEndian(sb.Slice(32, 4), 0x0F_F000);     // vs_dsize
      BinaryPrimitives.WriteInt32BigEndian(sb.Slice(40, 4), 8);             // vs_old_nau
      BinaryPrimitives.WriteInt32BigEndian(sb.Slice(52, 4), 96);            // vs_immedlen
      BinaryPrimitives.WriteInt32BigEndian(sb.Slice(56, 4), 10);            // vs_ndaddr
      BinaryPrimitives.WriteInt32BigEndian(sb.Slice(60, 4), 32);            // vs_firstau
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(sb[..4], FileSystem.VxFs.VxFsReader.Magic);
      BinaryPrimitives.WriteInt32LittleEndian(sb.Slice(4, 4), version);
      BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(8, 4), 0x6500_0000u);
      BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(12, 4), 0x6400_0000u);
      BinaryPrimitives.WriteInt32LittleEndian(sb.Slice(24, 4), 1024);
      BinaryPrimitives.WriteInt32LittleEndian(sb.Slice(28, 4), 0x10_0000);
      BinaryPrimitives.WriteInt32LittleEndian(sb.Slice(32, 4), 0x0F_F000);
      BinaryPrimitives.WriteInt32LittleEndian(sb.Slice(40, 4), 8);
      BinaryPrimitives.WriteInt32LittleEndian(sb.Slice(52, 4), 96);
      BinaryPrimitives.WriteInt32LittleEndian(sb.Slice(56, 4), 10);
      BinaryPrimitives.WriteInt32LittleEndian(sb.Slice(60, 4), 32);
    }
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.VxFs.VxFsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("VxFs"));
    Assert.That(d.DisplayName, Is.EqualTo("VxFS (Veritas)"));
    Assert.That(d.Extensions, Does.Contain(".vxfs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(1024));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0xF5, 0xFC, 0x01, 0xA5 }));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo(new byte[] { 0xA5, 0x01, 0xFC, 0xF5 }));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsHeaderSurface_LittleEndian() {
    var img = BuildMinimal(bigEndian: false);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.VxFs.VxFsFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.vxfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("superblock.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeader_LittleEndian() {
    var img = BuildMinimal(bigEndian: false, version: 7);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.VxFs.VxFsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "vxfs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.vxfs")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "superblock.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      Assert.That(meta, Does.Contain("endianness=little"));
      Assert.That(meta, Does.Contain("vs_magic=0xA501FCF5"));
      Assert.That(meta, Does.Contain("vs_version=7"));
      Assert.That(meta, Does.Contain("vs_blocksize=1024"));
      Assert.That(meta, Does.Contain("vs_immedlen=96"));
      Assert.That(meta, Does.Contain("vs_ndaddr=10"));
      Assert.That(meta, Does.Contain("vs_firstau=32"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeader_BigEndian() {
    var img = BuildMinimal(bigEndian: true, version: 10);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.VxFs.VxFsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "vxfs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      Assert.That(meta, Does.Contain("endianness=big"));
      Assert.That(meta, Does.Contain("vs_version=10"));
      Assert.That(meta, Does.Contain("vs_blocksize=1024"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyStream_DoesNotThrow() {
    using var ms = new MemoryStream(Array.Empty<byte>());
    var d = new FileSystem.VxFs.VxFsFormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
    ms.Position = 0;
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.vxfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("superblock.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_FallsBackToPartial() {
    var buf = new byte[4096];
    // Stomp magic area so random bytes can't accidentally match.
    for (var i = 0; i < 4; i++) buf[1024 + i] = 0x00;
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.VxFs.VxFsFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.vxfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("superblock.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_GarbageInput_WritesPartialMetadata() {
    var buf = new byte[512];
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.VxFs.VxFsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "vxfs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  /// <summary>
  /// The refusal has to name the reason it is really refused for: not the
  /// missing writer, which a block-moving pass does not need, but that this
  /// reader stops at the superblock and so no byte can be named as a file's.
  /// </summary>
  [Test, Category("ErrorHandling")]
  public void Defragment_Throws_BecauseNoByteBelongsToAFile() {
    var d = new FileSystem.VxFs.VxFsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal());
    Assert.That(() => d.Defragment(ms), Throws.TypeOf<NotSupportedException>()
                                              .With.Message.Contains("nothing to move"));
    ms.Position = 0;
    Assert.That(() => d.Defragment(ms, new DefragOptions()),
                Throws.TypeOf<NotSupportedException>()
                      .With.Message.Contains("superblock"));
  }

  [Test, Category("HappyPath")]
  public void Reader_Magic_Constants_Are_Consistent() {
    Assert.That(FileSystem.VxFs.VxFsReader.Magic, Is.EqualTo(0xA501FCF5u));
    Assert.That(FileSystem.VxFs.VxFsReader.MagicLE, Is.EqualTo(new byte[] { 0xF5, 0xFC, 0x01, 0xA5 }));
    Assert.That(FileSystem.VxFs.VxFsReader.MagicBE, Is.EqualTo(new byte[] { 0xA5, 0x01, 0xFC, 0xF5 }));
    Assert.That(FileSystem.VxFs.VxFsReader.SuperblockOffset, Is.EqualTo(1024));
  }
}
