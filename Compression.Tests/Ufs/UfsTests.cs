using System.Buffers.Binary;

namespace Compression.Tests.Ufs;

[TestFixture]
public class UfsTests {
  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new FileSystem.Ufs.UfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Read_SingleFile() {
    var data = "Hello UFS!"u8.ToArray();
    using var ms = new MemoryStream(BuildImage(("test.txt", data)));
    var r = new FileSystem.Ufs.UfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Read_MultipleFiles() {
    using var ms = new MemoryStream(BuildImage(("a.txt", "First"u8.ToArray()), ("b.txt", "Second"u8.ToArray())));
    var r = new FileSystem.Ufs.UfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Extract_BlockSpanningFile_RoundTrips() {
    var data = new byte[16384]; // spans two 8K blocks
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 131 + 17);
    using var ms = new MemoryStream(BuildImage(("big.bin", data)));
    var r = new FileSystem.Ufs.UfsReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.Ufs.UfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Ufs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".ufs"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_CanCreate() {
    var desc = new FileSystem.Ufs.UfsFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True,
      "UFS writer is re-enabled and must advertise CanCreate.");
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveCreatable>());
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveWriteConstraints>());
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Ufs.UfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[16384];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Ufs.UfsReader(ms));
  }

  // ── spec-offset canaries ───────────────────────────────────────────────
  [Test, Category("HappyPath")]
  public void Writer_SuperblockHasFsMagic() {
    var img = BuildImage(("t", "x"u8.ToArray()));
    // fs_magic is the LAST int32 of the 1376-byte struct fs → byte offset 1372 from sb start
    var sb = img.AsSpan(8192);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sb[1372..]), Is.EqualTo(0x00011954u),
      "fs_magic must be 0x00011954 at superblock offset 1372");
    // fs_bsize at offset 48
    Assert.That(BinaryPrimitives.ReadInt32LittleEndian(sb[48..]), Is.EqualTo(8192), "fs_bsize");
    // fs_fsize at offset 52
    Assert.That(BinaryPrimitives.ReadInt32LittleEndian(sb[52..]), Is.EqualTo(1024), "fs_fsize");
    // fs_frag at offset 56
    Assert.That(BinaryPrimitives.ReadInt32LittleEndian(sb[56..]), Is.EqualTo(8), "fs_frag");
    // fs_bshift at offset 80 = log2(8192) = 13
    Assert.That(BinaryPrimitives.ReadInt32LittleEndian(sb[80..]), Is.EqualTo(13), "fs_bshift");
    // fs_fshift at offset 84 = log2(1024) = 10
    Assert.That(BinaryPrimitives.ReadInt32LittleEndian(sb[84..]), Is.EqualTo(10), "fs_fshift");
    // fs_ncg at offset 44 = 4 (faithful newfs -O1 multi-cylinder-group layout;
    // the previous single-cg value of 1 encoded the old non-newfs writer).
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sb[44..]), Is.EqualTo(4u), "fs_ncg");
    // fs_ipg at offset 184 — inodes per group for the 16 MB newfs profile.
    // fs_ipg follows from the group size, which now scales with the volume, so it
    // is read from the superblock rather than pinned to the old four-group value.
    var ipg = BinaryPrimitives.ReadUInt32LittleEndian(sb[184..]);
    Assert.That(ipg, Is.GreaterThan(0u), "fs_ipg");
    // fs_fpg at offset 188 — fragments per group. It scales with the volume, and a
    // group's bitmaps have to fit the one block its header occupies, which caps it.
    var fpg = BinaryPrimitives.ReadInt32LittleEndian(sb[188..]);
    Assert.That(fpg, Is.GreaterThan(0), "fs_fpg");
    Assert.That(fpg, Is.LessThanOrEqualTo(32768 + 8), "fs_fpg stays inside one header block");
  }

  [Test, Category("HappyPath")]
  public void Writer_CgHeaderHasCgMagic() {
    var img = BuildImage(("t", "x"u8.ToArray()));
    // CG 0 header is at fragment fs_cblkno * fragSize = 24 * 1024 = 24576
    // (newfs -O1 places sblkno=16, cblkno=24, iblkno=32 for 8K/1K geometry).
    var cg = img.AsSpan(24 * 1024);
    Assert.That(BinaryPrimitives.ReadInt32LittleEndian(cg[4..]), Is.EqualTo(0x00090255),
      "cg_magic must be 0x00090255 at cg_firstfield+4 offset");
    // cg_cgx at offset 12
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(cg[12..]), Is.EqualTo(0u), "cg_cgx");
    // cg_iusedoff at offset 92 — should be a reasonable byte offset into the cg block
    var iusedOff = BinaryPrimitives.ReadUInt32LittleEndian(cg[92..]);
    Assert.That(iusedOff, Is.GreaterThan(128u).And.LessThan(8192u), "cg_iusedoff inside cg block");
    // cg_freeoff > cg_iusedoff
    var freeOff = BinaryPrimitives.ReadUInt32LittleEndian(cg[96..]);
    Assert.That(freeOff, Is.GreaterThan(iusedOff), "cg_freeoff after cg_iusedoff");
    // cg_niblk at offset 116 = 0 for UFS1 (it is a UFS2-only field; the old
    // assertion of 2048 encoded the previous writer's bug — niblk lives in the
    // historic cg_old_niblk@18 instead, which is fs_ipg = 2112).
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(cg[116..]), Is.EqualTo(0u), "cg_niblk (UFS1: 0)");
    var sbIpg = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(8192 + 184));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(cg[18..]), Is.EqualTo((ushort)sbIpg), "cg_old_niblk = fs_ipg");
  }

  [Test, Category("HappyPath")]
  public void Writer_HasNonZeroUuid() {
    var img = BuildImage(("t", "x"u8.ToArray()));
    // Volume UUID is stored in fs_sparecon64[12] (offset 896 in superblock, 16 bytes)
    var uuid = img.AsSpan(8192 + 896, 16);
    Assert.That(uuid.ToArray().Any(b => b != 0), Is.True, "UUID must be non-zero");
  }

  [Test, Category("HappyPath")]
  public void Writer_FsCsSummaryConsistent() {
    var img = BuildImage(("f1", "one"u8.ToArray()), ("f2", "two"u8.ToArray()));
    var sb = img.AsSpan(8192);
    // fs_cstotal (csum_total: 8 int64s) at offset 1008: ndir, nbfree, nifree, nffree...
    var ndir = BinaryPrimitives.ReadInt64LittleEndian(sb[1008..]);
    var nifree = BinaryPrimitives.ReadInt64LittleEndian(sb[1024..]);
    // newfs emits two directories: the root and the synthetic /.snap (the old
    // single value of 1 predated faithful newfs emulation).
    Assert.That(ndir, Is.EqualTo(2), "root + /.snap directories");
    // ncg × ipg − 6 used inodes (0,1 reserved + root + .snap + f1 + f2). Both come
    // from the superblock: the group count scales with the volume.
    var ncg = BinaryPrimitives.ReadInt32LittleEndian(img.AsSpan(8192 + 44));
    var ipgAll = (int)BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(8192 + 184));
    Assert.That(nifree, Is.EqualTo(ncg * ipgAll - 6), "free inodes across all cylinder groups");

    // CG0's cg_cs echoes the cg-local summary. CG 0 header at 24 KiB.
    var cg = img.AsSpan(24 * 1024);
    var cgNdir = BinaryPrimitives.ReadInt32LittleEndian(cg[24..]);
    var cgNifree = BinaryPrimitives.ReadInt32LittleEndian(cg[32..]);
    Assert.That(cgNdir, Is.EqualTo(2), "cg0 holds both directories");
    Assert.That(cgNifree, Is.EqualTo(ipgAll - 6), "cg0 holds every live inode");
  }
}
