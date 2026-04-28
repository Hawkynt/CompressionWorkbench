using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.BcacheFs;

[TestFixture]
public class BcacheFsTests {

  /// <summary>
  /// Synthesises a minimal BcacheFS image whose superblock follows the actual
  /// kernel layout (<c>fs/bcachefs/bcachefs_format.h</c>): 16-byte csum +
  /// __le16 version + __le16 version_min + 4-byte pad + 16-byte magic UUID +
  /// 16-byte uuid + 16-byte user_uuid + 32-byte label + __le64 offset +
  /// __le64 seq + __le16 block_size + u8 dev_idx + u8 nr_devices + __le32
  /// u64s + the rest.
  /// </summary>
  private static byte[] BuildMinimal() {
    var image = new byte[8192];
    var sbStart = 4096;

    // 0..16  csum (struct bch_csum: __le64 lo + __le64 hi) — left zero (BCH_SB_CSUM_TYPE_NONE)
    // 16..18 version (__le16 = (major << 10) | minor) — encode 1.7
    var version = (ushort)((1 << 10) | 7);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sbStart + 16, 2), version);
    // 18..20 version_min (__le16) — accept the same as version
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sbStart + 18, 2), version);
    // 20..24 pad[2] — left zero

    // 24..40 Magic UUID (BCHFS_MAGIC c685 73f6 66ce 90a9 d96a 60cf 803d f7ef)
    byte[] magic = [
      0xC6, 0x85, 0x73, 0xF6,
      0x66, 0xCE,
      0x90, 0xA9,
      0xD9, 0x6A,
      0x60, 0xCF, 0x80, 0x3D, 0xF7, 0xEF,
    ];
    magic.CopyTo(image.AsSpan(sbStart + 24));

    // 40..56 internal uuid
    var poolUuid = Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray();
    poolUuid.CopyTo(image.AsSpan(sbStart + 40));
    // 56..72 user uuid
    var userUuid = Guid.Parse("66666666-7777-8888-9999-AAAAAAAAAAAA").ToByteArray();
    userUuid.CopyTo(image.AsSpan(sbStart + 56));

    // 72..104 label[32] (NUL-terminated)
    Encoding.UTF8.GetBytes("test-pool").CopyTo(image.AsSpan(sbStart + 72));

    // 104..112 offset (__le64)
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sbStart + 104, 8), 8UL);
    // 112..120 seq
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sbStart + 112, 8), 0xCAFEUL);
    // 120..122 block_size (sectors)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sbStart + 120, 2), 8);
    // 122 dev_idx (u8)
    image[sbStart + 122] = 0;
    // 123 nr_devices (u8)
    image[sbStart + 123] = 1;
    // 124..128 u64s (__le32) — 0 trailing bytes for this synthetic image
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sbStart + 124, 4), 0U);

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.BcacheFs.BcacheFsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("BcacheFs"));
    Assert.That(d.DisplayName, Is.EqualTo("BcacheFS"));
    Assert.That(d.Extensions, Does.Contain(".bcachefs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(4120));
    Assert.That(d.MagicSignatures[0].Bytes, Has.Length.EqualTo(16));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.85).Within(0.01));
    // The descriptor now opts in to creation; verify the capability flag.
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.BcacheFs.BcacheFsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(2));
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.bcachefs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("superblock.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeader() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.BcacheFs.BcacheFsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "bcachefs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.bcachefs")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "superblock.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      Assert.That(meta, Does.Contain("label=test-pool"));
      // Version is now (major.minor) per BCH_VERSION encoding.
      Assert.That(meta, Does.Contain("version=1.7"));
      Assert.That(meta, Does.Contain("block_size=8"));
      Assert.That(meta, Does.Contain("dev_idx=0"));
      Assert.That(meta, Does.Contain("nr_devices=1"));
      Assert.That(meta, Does.Contain("seq=51966"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_NoMagic_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[8192]);
    var d = new FileSystem.BcacheFs.BcacheFsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.bcachefs"));
    Assert.That(entries.Select(e => e.Name), Does.Not.Contain("superblock.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void List_TinyImage_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[16]);
    var d = new FileSystem.BcacheFs.BcacheFsFormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_FallsBackToFull() {
    var rnd = new Random(42);
    var buf = new byte[8192];
    rnd.NextBytes(buf);
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.BcacheFs.BcacheFsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.bcachefs"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }

  // ── Writer round-trip + spec-shape tests ────────────────────────────

  /// <summary>
  /// Smoke: the writer emits a non-empty image at the configured size and
  /// the magic UUID lands at byte 4120 (= sb sector 8 + 24 bytes into
  /// struct bch_sb, past csum + version + version_min + pad).
  /// </summary>
  [Test, Category("HappyPath")]
  public void Writer_SuperblockMagicAtOffset4120() {
    var w = new FileSystem.BcacheFs.BcacheFsWriter();
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var img = ms.ToArray();
    Assert.That(img.Length, Is.EqualTo(FileSystem.BcacheFs.BcacheFsWriter.MinImageSize));
    var magic = img.AsSpan(4120, 16).ToArray();
    Assert.That(magic, Is.EqualTo(FileSystem.BcacheFs.BcacheFsWriter.BcachefsMagic));
  }

  /// <summary>
  /// Writer's version fields are spec-shaped: u16 at +16/+18 (NOT u64),
  /// version_min ≤ version, and both versions land in
  /// [bcachefs_metadata_version_min, current].
  /// </summary>
  [Test, Category("HappyPath")]
  public void Writer_VersionFieldsValid() {
    var w = new FileSystem.BcacheFs.BcacheFsWriter();
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var img = ms.ToArray();
    var version = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(4096 + 16, 2));
    var versionMin = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(4096 + 18, 2));
    // We pick a version recognised by the widely-deployed bcachefs-tools 1.3.x
    // line — newer kernels accept it, older tools accept it.
    Assert.That(version, Is.EqualTo((1 << 10) | 3), "version should be BCH_VERSION(1, 3)");
    Assert.That(versionMin, Is.EqualTo(9), "version_min should be bcachefs_metadata_version_min = 9");
    Assert.That(versionMin, Is.LessThanOrEqualTo(version), "version_min must be ≤ version");
  }

  /// <summary>
  /// The layout struct (sector 7) advertises four backup superblocks; each
  /// advertised slot must actually contain a valid SB (magic at +24).
  /// </summary>
  [Test, Category("HappyPath")]
  public void Writer_FourBackupSuperblocksPresent() {
    var w = new FileSystem.BcacheFs.BcacheFsWriter();
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var img = ms.ToArray();

    // The layout copy at sector 7 = byte 3584. Layout: 16-byte magic, then
    // u8 layout_type, u8 sb_max_size_bits, u8 nr_superblocks, u8 pad[5],
    // then __le64 sb_offset[61].
    var layoutOff = 7 * 512;
    var layoutMagic = img.AsSpan(layoutOff, 16).ToArray();
    Assert.That(layoutMagic, Is.EqualTo(FileSystem.BcacheFs.BcacheFsWriter.BcachefsMagic));
    var nrSbs = img[layoutOff + 18];
    Assert.That(nrSbs, Is.EqualTo(4), "layout should advertise 4 superblock copies");

    // Walk every advertised offset and verify the magic UUID is at sector + 24 bytes.
    for (var i = 0; i < nrSbs; i++) {
      var sectorOff = BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(layoutOff + 24 + 8 * i, 8));
      var sbByteOff = (long)sectorOff * 512;
      Assert.That(sbByteOff + 40, Is.LessThanOrEqualTo(img.LongLength),
        $"backup SB #{i} at sector {sectorOff} must fit in the image");
      var sbMagic = img.AsSpan((int)sbByteOff + 24, 16).ToArray();
      Assert.That(sbMagic, Is.EqualTo(FileSystem.BcacheFs.BcacheFsWriter.BcachefsMagic),
        $"backup SB #{i} at sector {sectorOff} must carry the magic UUID");

      // Each backup also stamps its own sector address at the offset field.
      var selfOffset = BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan((int)sbByteOff + 104, 8));
      Assert.That(selfOffset, Is.EqualTo(sectorOff),
        $"backup SB #{i} should self-report its sector address as {sectorOff}");
    }
  }

  /// <summary>
  /// The writer rejects sub-MinImageSize requests (otherwise BCH_MIN_NR_NBUCKETS
  /// = 512 with our 256 KiB buckets cannot be satisfied, and bcachefs would
  /// reject with "Not enough buckets").
  /// </summary>
  [Test, Category("HappyPath")]
  public void Writer_MinSizeImage() {
    var w = new FileSystem.BcacheFs.BcacheFsWriter();
    Assert.That(FileSystem.BcacheFs.BcacheFsWriter.MinImageSize, Is.EqualTo(128L * 1024 * 1024));
    Assert.That(() => w.SetImageSize(1024 * 1024),
      Throws.InstanceOf<ArgumentOutOfRangeException>(),
      "Sub-MinImageSize must be rejected so BCH_MIN_NR_NBUCKETS is satisfied.");
    // MinImageSize exactly should be accepted.
    Assert.That(() => w.SetImageSize(FileSystem.BcacheFs.BcacheFsWriter.MinImageSize), Throws.Nothing);
  }

  /// <summary>
  /// Round-trip: writer output is parseable by our (now spec-accurate)
  /// reader. <c>parse_status=ok</c> and the user-set label survives.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Writer_OutputIsParseableByReader() {
    var w = new FileSystem.BcacheFs.BcacheFsWriter();
    w.SetLabel("rt-label");
    w.SetUserUuid(Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00"));
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var d = new FileSystem.BcacheFs.BcacheFsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "bcachefs_rt_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"), meta);
      Assert.That(meta, Does.Contain("label=rt-label"), meta);
      Assert.That(meta, Does.Contain("nr_devices=1"), meta);
      Assert.That(meta, Does.Match(@"version=1\.\d+"), meta);
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }
}
