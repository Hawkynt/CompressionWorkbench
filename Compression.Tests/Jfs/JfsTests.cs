using System.Buffers.Binary;

namespace Compression.Tests.Jfs;

[TestFixture]
public class JfsTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new FileSystem.Jfs.JfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Read_SingleFile() {
    var content = "Hello JFS!"u8.ToArray();
    using var ms = new MemoryStream(BuildImage(("test.txt", content)));
    var r = new FileSystem.Jfs.JfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_SingleFile() {
    var content = "Hello JFS!"u8.ToArray();
    using var ms = new MemoryStream(BuildImage(("test.txt", content)));
    var r = new FileSystem.Jfs.JfsReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Read_MultipleFiles() {
    var a = "alpha"u8.ToArray();
    var b = "BRAVO!"u8.ToArray();
    using var ms = new MemoryStream(BuildImage(("a.txt", a), ("b.txt", b)));
    var r = new FileSystem.Jfs.JfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(r.Extract(byName["a.txt"]), Is.EqualTo(a));
    Assert.That(r.Extract(byName["b.txt"]), Is.EqualTo(b));
  }

  [Test, Category("HappyPath")]
  public void Extract_LargerThanBlock_RoundTrips() {
    var data = new byte[8192];
    for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);
    using var ms = new MemoryStream(BuildImage(("big.bin", data)));
    var r = new FileSystem.Jfs.JfsReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.Jfs.JfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Jfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".jfs"));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo("JFS1"u8.ToArray()));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_CanCreate() {
    var desc = new FileSystem.Jfs.JfsFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True,
      "JFS writer is re-enabled and must advertise CanCreate.");
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveCreatable>());
    Assert.That(desc, Is.InstanceOf<Compression.Registry.IArchiveWriteConstraints>());
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Jfs.JfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[40000];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.Jfs.JfsReader(ms));
  }

  // ── spec-offset tests ──────────────────────────────────────────────────
  [Test, Category("HappyPath")]
  public void Writer_SuperblockFieldsAtSpecOffsets() {
    var img = BuildImage(("t.txt", "x"u8.ToArray()));
    var sb = img.AsSpan(0x8000);
    // s_magic "JFS1" at offset 0
    Assert.That(sb[..4].ToArray(), Is.EqualTo("JFS1"u8.ToArray()), "s_magic");
    // s_version (le32) = 2 at offset 4
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sb[4..]), Is.EqualTo(2u), "s_version");
    // s_bsize at offset 16
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sb[16..]), Is.EqualTo(4096u), "s_bsize");
    // s_l2bsize at offset 20
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(sb[20..]), Is.EqualTo((ushort)12), "s_l2bsize");
    // s_l2bfactor at offset 22
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(sb[22..]), Is.EqualTo((ushort)3), "s_l2bfactor");
    // s_pbsize at offset 24
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sb[24..]), Is.EqualTo(512u), "s_pbsize");
    // s_l2pbsize at offset 28
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(sb[28..]), Is.EqualTo((ushort)9), "s_l2pbsize");
    // s_state at offset 40 = 0 (FM_CLEAN)
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sb[40..]), Is.EqualTo(0u), "s_state");
    // s_compress at offset 44 = 0
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(sb[44..]), Is.EqualTo(0u), "s_compress");
  }

  [Test, Category("HappyPath")]
  public void Writer_HasNonZeroUuid() {
    var img = BuildImage(("t.txt", "x"u8.ToArray()));
    var uuid = img.AsSpan(0x8000 + 136, 16);
    Assert.That(uuid.ToArray().Any(b => b != 0), Is.True, "s_uuid must be non-zero (per spec)");
    var logUuid = img.AsSpan(0x8000 + 168, 16);
    Assert.That(logUuid.ToArray().Any(b => b != 0), Is.True, "s_loguuid must be non-zero");
  }

  [Test, Category("HappyPath")]
  public void Writer_PxdEncodingCorrect() {
    // A pxd_t with length=0x123456 and address=0x123456789ABCDE (56 bits max fits)
    // Real packing (from linux/fs/jfs/jfs_types.h):
    //   len_addr = (len & 0xFFFFFF) | ((addr >> 32) << 24) stored as le32
    //   addr2    = addr & 0xFFFFFFFF stored as le32
    Span<byte> pxd = stackalloc byte[8];
    // Use a 40-bit address that fits the pxd encoding exactly: (addr>>32) has only low 8 bits set.
    // address = 0x00_3456_789A → addr >> 32 = 0x34 → << 24 = 0x34000000
    const ulong Address = 0x0000_0034_5678_9ABCUL;
    FileSystem.Jfs.JfsWriter.WritePxd(pxd, length: 0x123456u, address: Address);
    var lenAddr = BinaryPrimitives.ReadUInt32LittleEndian(pxd);
    var addr2 = BinaryPrimitives.ReadUInt32LittleEndian(pxd[4..]);
    Assert.That(lenAddr, Is.EqualTo(0x34123456u), "len_addr bit-pack: low 24 = length, high 8 = addr>>32");
    Assert.That(addr2, Is.EqualTo(0x56789ABCu), "addr2 low 32");
    // Round-trip via reader helper
    Assert.That(FileSystem.Jfs.JfsReader.ReadPxdLength(pxd), Is.EqualTo(0x123456u));
    Assert.That(FileSystem.Jfs.JfsReader.ReadPxdAddress(pxd), Is.EqualTo(Address),
      "40-bit address recovered");
  }

  [Test, Category("HappyPath")]
  public void Writer_SecondaryAitPxdPresent() {
    var img = BuildImage(("t.txt", "x"u8.ToArray()));
    var sb = img.AsSpan(0x8000);
    // s_ait2 pxd is at sb offset 48, 8 bytes. Length=3 (3 blocks to cover inodes 0..23), address=9.
    var pxd = sb.Slice(48, 8);
    Assert.That(FileSystem.Jfs.JfsReader.ReadPxdLength(pxd), Is.EqualTo(3u));
    Assert.That(FileSystem.Jfs.JfsReader.ReadPxdAddress(pxd), Is.EqualTo(9UL));
  }
}
