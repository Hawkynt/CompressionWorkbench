using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Hammer;

[TestFixture]
public class HammerTests {

  /// <summary>Synthesises a minimal HAMMER volume header at offset 0.</summary>
  private static byte[] BuildMinimal(string label = "TESTVOL", int volNo = 0, int volCount = 1) {
    var image = new byte[64 * 1024]; // 64 KB — well past the 1928-byte header capture.

    // vol_signature (uint64 LE) at offset 0 — 0xC8414D4DC5523031.
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0, 8), 0xC8414D4DC5523031UL);
    // vol_bot_beg .. vol_buf_end at +8/+16/+24/+32 (signed int64). Choose
    // recognisable layout values consistent with a small 64 KB volume.
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(8, 8), 0x0000_0000_0000_2000L); // vol_bot_beg
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(16, 8), 0x0000_0000_0000_3000L); // vol_mem_beg
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(24, 8), 0x0000_0000_0001_0000L); // vol_buf_beg
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(32, 8), 0x0000_0000_0010_0000L); // vol_buf_end

    // vol_fsid (16 bytes) at offset 48, recognisable pattern.
    for (var i = 0; i < 16; i++) image[48 + i] = (byte)(0x10 + i);
    // vol_fstype (16 bytes) at offset 64 — HAMMER fstype UUID
    // 9e9eaef0-9788-11dd-b1a9-01301bb8a9f5 (network byte order in Dragonfly).
    var hammerFsType = new byte[] {
      0x9E, 0x9E, 0xAE, 0xF0, 0x97, 0x88, 0x11, 0xDD,
      0xB1, 0xA9, 0x01, 0x30, 0x1B, 0xB8, 0xA9, 0xF5
    };
    hammerFsType.CopyTo(image.AsSpan(64, 16));

    // vol_label[64] at offset 80 — NUL-padded ASCII.
    Encoding.ASCII.GetBytes(label).CopyTo(image.AsSpan(80));
    // vol_no, vol_count, vol_version, vol_crc, vol_flags, vol_rootvol.
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(144, 4), volNo);
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(148, 4), volCount);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(152, 4), 7u); // vol_version
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(156, 4), 0xDEADBEEFu); // vol_crc
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(160, 4), 0x00000003u); // vol_flags
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(164, 4), 0u); // vol_rootvol = 0
    // vol0_stat_bigblocks/freebigblocks/inodes.
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(200, 8), 1234L);
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(208, 8), 567L);
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(224, 8), 42L);
    // vol0_btree_root, vol0_next_tid.
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(240, 8), 0x0000_0000_0040_0000L);
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(248, 8), 0x0001_0002_0003_0004L);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Hammer.HammerFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Hammer"));
    Assert.That(d.DisplayName, Is.EqualTo("HAMMER (DragonFly BSD)"));
    Assert.That(d.Extensions, Does.Contain(".hammer"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.85).Within(0.01));
    // First 8 LE bytes of 0xC8414D4DC5523031 == 31 30 52 C5 4D 4D 41 C8.
    Assert.That(d.MagicSignatures[0].Bytes,
      Is.EqualTo(new byte[] { 0x31, 0x30, 0x52, 0xC5, 0x4D, 0x4D, 0x41, 0xC8 }));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Hammer.HammerFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(3));
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.hammer"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("volume_header.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeader() {
    var img = BuildMinimal("MYHAMVOL");
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Hammer.HammerFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "hammer_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.hammer")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "volume_header.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      Assert.That(meta, Does.Contain("vol_signature=0xC8414D4DC5523031"));
      Assert.That(meta, Does.Contain("vol_label=MYHAMVOL"));
      Assert.That(meta, Does.Contain("vol_no=0"));
      Assert.That(meta, Does.Contain("vol_count=1"));
      Assert.That(meta, Does.Contain("vol_version=7"));
      Assert.That(meta, Does.Contain("vol0_stat_bigblocks=1234"));
      Assert.That(meta, Does.Contain("vol0_stat_freebigblocks=567"));
      Assert.That(meta, Does.Contain("vol0_stat_inodes=42"));
      Assert.That(meta, Does.Contain("9E9EAEF0978811DDB1A901301BB8A9F5"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyStream_DoesNotThrow() {
    using var ms = new MemoryStream(Array.Empty<byte>());
    var d = new FileSystem.Hammer.HammerFormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.hammer"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_FallsBackToPartial() {
    var rng = new Random(0xC0DE);
    var buf = new byte[4096];
    rng.NextBytes(buf);
    // Stomp the magic so the random bytes can't accidentally match.
    buf[0] = 0x00; buf[1] = 0x00; buf[2] = 0x00; buf[3] = 0x00;
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.Hammer.HammerFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.hammer"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("volume_header.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_GarbageInput_WritesPartialMetadata() {
    var buf = new byte[256];
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.Hammer.HammerFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "hammer_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.hammer")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void VolumeOndisk_Magic_Bytes_Match() {
    Assert.That(FileSystem.Hammer.HammerVolumeOndisk.VolumeSignature,
                Is.EqualTo(0xC8414D4DC5523031UL));
    Assert.That(FileSystem.Hammer.HammerVolumeOndisk.MagicBytesLE,
                Is.EqualTo(new byte[] { 0x31, 0x30, 0x52, 0xC5, 0x4D, 0x4D, 0x41, 0xC8 }));
  }
}
