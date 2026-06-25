using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.AdvFs;

[TestFixture]
public class AdvFsTests {

  /// <summary>Synthesises a minimal AdvFS image carrying the detection cookie
  /// and the parsed-field layout AdvFsReader expects after the cookie.</summary>
  private static byte[] BuildMinimal(string volumeTag = "DOMAIN1.VOL0") {
    // 256 KB image — comfortably past page 16 × 8192 = 131072 plus the 4096-byte
    // capture window.
    var image = new byte[256 * 1024];
    var rbmtOffset = FileSystem.AdvFs.AdvFsReader.RbmtPageOffset;

    // 16-byte detection cookie at start of RBMT page 0.
    FileSystem.AdvFs.AdvFsReader.DetectionCookie.CopyTo(image, (int)rbmtOffset);

    var p = (int)rbmtOffset + FileSystem.AdvFs.AdvFsReader.DetectionCookie.Length;

    // BSR_DMN_ATTR: 16-byte domain UUID — recognisable pattern.
    for (var i = 0; i < 16; i++) image[p + i] = (byte)(0x20 + i);
    p += 16;
    // mountId — 8-byte ulong LE.
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(p, 8), 0x1234_5678_9ABC_DEF0UL);
    p += 8;
    // onDiskVersion — 4.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p, 4), 4u);
    p += 4;

    // BSR_VD_ATTR: vdIndex, vdCount, state, vdBlkCnt, vdMetaBlkCnt.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p, 4), 0u); p += 4; // vdIndex
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p, 4), 1u); p += 4; // vdCount
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p, 4), 0x00000003u); p += 4; // state
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(p, 8), 0x0001_0000UL); p += 8; // vdBlkCnt
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(p, 4), 16u); p += 4; // vdMetaBlkCnt

    // 64-byte ASCII volume tag, NUL-padded.
    var tagBytes = Encoding.ASCII.GetBytes(volumeTag);
    Array.Copy(tagBytes, 0, image, p, tagBytes.Length);

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.AdvFs.AdvFsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("AdvFs"));
    Assert.That(d.DisplayName, Is.EqualTo("AdvFS (Tru64 UNIX)"));
    Assert.That(d.Extensions, Does.Contain(".advfs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(131072));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(FileSystem.AdvFs.AdvFsReader.DetectionCookie));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsHeaderSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.AdvFs.AdvFsFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.advfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("rbmt_page0.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeader() {
    var img = BuildMinimal("MYTRU64.VOLA");
    using var ms = new MemoryStream(img);
    var d = new FileSystem.AdvFs.AdvFsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "advfs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.advfs")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "rbmt_page0.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      Assert.That(meta, Does.Contain("on_disk_version=4"));
      Assert.That(meta, Does.Contain("vd_index=0"));
      Assert.That(meta, Does.Contain("vd_count=1"));
      Assert.That(meta, Does.Contain("vd_meta_blk_cnt=16"));
      Assert.That(meta, Does.Contain("volume_tag=MYTRU64.VOLA"));
      Assert.That(meta, Does.Contain("mount_id=0x123456789ABCDEF0"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyStream_DoesNotThrow() {
    using var ms = new MemoryStream(Array.Empty<byte>());
    var d = new FileSystem.AdvFs.AdvFsFormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
    ms.Position = 0;
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.advfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("rbmt_page0.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_FallsBackToPartial() {
    var buf = new byte[200 * 1024];
    // Stomp the would-be RBMT cookie area so random bytes can't accidentally match.
    for (var i = 0; i < buf.Length; i++) buf[i] = (byte)((i + 0x55) & 0xFF);
    for (var i = 0; i < 16; i++) buf[(int)FileSystem.AdvFs.AdvFsReader.RbmtPageOffset + i] = 0xAA;
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.AdvFs.AdvFsFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.advfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("rbmt_page0.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_GarbageInput_WritesPartialMetadata() {
    var buf = new byte[2048];
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.AdvFs.AdvFsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "advfs_" + Guid.NewGuid().ToString("N"));
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

  [Test, Category("RoundTrip")]
  public void Defragment_NowSupported_RebuildsAndStaysReadable() {
    // AdvFs is now genuine R/W (AdvFsInPlaceModifier), so defrag is no longer refused —
    // it rebuilds the layout and leaves a valid, listable volume.
    var d = new FileSystem.AdvFs.AdvFsFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal());
    Assert.DoesNotThrow(() => d.Defragment(ms));
    ms.Position = 0;
    Assert.DoesNotThrow(() => d.List(ms, null));
    ms.Position = 0;
    Assert.DoesNotThrow(() => d.Defragment(ms, new DefragOptions()));
  }

  [Test, Category("HappyPath")]
  public void Reader_Cookie_Bytes_Match() {
    Assert.That(FileSystem.AdvFs.AdvFsReader.DetectionCookie.Length, Is.EqualTo(16));
    Assert.That(FileSystem.AdvFs.AdvFsReader.PageSize, Is.EqualTo(8192));
    Assert.That(FileSystem.AdvFs.AdvFsReader.RbmtPageOffset, Is.EqualTo(131072));
  }
}
