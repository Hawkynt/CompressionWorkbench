using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.F2fs;

namespace Compression.Tests.F2fs;

[TestFixture]
public class F2fsTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new F2fsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("F2fs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".f2fs"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Offset, Is.EqualTo(1024));
    Assert.That(desc.Category, Is.EqualTo(FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IsWriteable() {
    var desc = new F2fsFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(desc, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(desc, Is.InstanceOf<IArchiveWriteConstraints>());
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new F2fsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[4096];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new F2fsReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_ValidMagic_EmptyFs() {
    var data = new byte[1024 * 1024]; // 1MB
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1024), 0xF2F52010);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1024 + 16), 12); // log_blocksize = 12 => 4096
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1024 + 84), 10); // nat_blkaddr
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1024 + 92), 50); // main_blkaddr
    using var ms = new MemoryStream(data);
    var r = new F2fsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  // ------------------------------------------------------------------
  // Writer tests
  // ------------------------------------------------------------------

  [Test, Category("HappyPath")]
  public void Writer_SuperblockMagicAtSpecOffset() {
    var w = new F2fsWriter();
    w.AddFile("a.txt", "hello"u8.ToArray());
    var img = w.Build();

    // Magic MUST be at file offset 0x400 = 1024 (F2FS_SUPER_OFFSET), NOT at offset 0.
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x400));
    Assert.That(magic, Is.EqualTo(0xF2F52010));
    // And the second superblock copy lives at block 1 = offset 4096 + 1024.
    var magic2 = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x1400));
    Assert.That(magic2, Is.EqualTo(0xF2F52010));
  }

  [Test, Category("HappyPath")]
  public void Writer_CheckpointMagicCorrect() {
    var w = new F2fsWriter();
    w.AddFile("a.txt", "hello"u8.ToArray());
    var img = w.Build();

    // Checkpoint starts at segment 1 (block 512) — cp_blkaddr is also in the SB.
    var cpBlkAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x400 + 76));
    Assert.That(cpBlkAddr, Is.GreaterThan(0));
    var cpOff = cpBlkAddr * 4096;

    // checkpoint_ver at offset 0 — we write 1.
    var ver = BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(cpOff));
    Assert.That(ver, Is.EqualTo(1UL));

    // Magic is repeated just before the checksum at offset 4088.
    var cpMagic = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(cpOff + 4088));
    Assert.That(cpMagic, Is.EqualTo(0xF2F52010));
  }

  [Test, Category("HappyPath")]
  public void Writer_NatEntriesPopulated() {
    var w = new F2fsWriter();
    w.AddFile("a.txt", "hello"u8.ToArray());
    w.AddFile("b.txt", "world"u8.ToArray());
    var img = w.Build();

    var natBlkAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x400 + 84));
    var natOff = natBlkAddr * 4096;
    // NAT entry for root (nid=3): offset = 3 * 9 = 27. Expect non-zero block_addr.
    var rootBlockAddr = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(natOff + 3 * 9 + 5));
    Assert.That(rootBlockAddr, Is.GreaterThan(0u));
    // NAT entries for file inodes (nid=4, nid=5) also populated.
    var file1Addr = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(natOff + 4 * 9 + 5));
    var file2Addr = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(natOff + 5 * 9 + 5));
    Assert.That(file1Addr, Is.GreaterThan(0u));
    Assert.That(file2Addr, Is.GreaterThan(0u));
    Assert.That(file1Addr, Is.Not.EqualTo(file2Addr));
  }

  [Test, Category("HappyPath")]
  public void Writer_HasNonZeroUuid() {
    var w = new F2fsWriter();
    w.AddFile("x.bin", new byte[] { 1, 2, 3 });
    var img = w.Build();

    // UUID at SB offset 108, 16 bytes — must not be all zero.
    var uuid = img.AsSpan(0x400 + 108, 16).ToArray();
    var allZero = uuid.All(b => b == 0);
    Assert.That(allZero, Is.False);
  }

  [Test, Category("HappyPath")]
  public void Writer_InlineDentryLayout() {
    var w = new F2fsWriter();
    w.AddFile("foo", "bar"u8.ToArray());
    var img = w.Build();

    // Root inode block is the first block of the main area.
    var mainBlkAddr = (int)BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x400 + 92));
    var rootOff = mainBlkAddr * 4096;

    // i_mode: directory (S_IFDIR | 0755 = 0x41ED)
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(rootOff));
    Assert.That(mode & 0xF000, Is.EqualTo(0x4000));

    // i_inline flag at offset 3 MUST include F2FS_INLINE_DENTRY (0x04).
    var inlineFlag = img[rootOff + 3];
    Assert.That(inlineFlag & 0x04, Is.EqualTo(0x04));
  }

  [Test, Category("HappyPath")]
  public void Writer_RoundTrip_SingleFile() {
    var w = new F2fsWriter();
    var payload = Encoding.UTF8.GetBytes("Hello, F2FS!");
    w.AddFile("greeting.txt", payload);
    var img = w.Build();

    using var ms = new MemoryStream(img);
    var r = new F2fsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    // Inline-dentry single-slot store = 7 chars of the name survive the round-trip
    // (F2FS_SLOT_LEN is 8 bytes total — 1 for null terminator in some readers, 7 usable).
    Assert.That(r.Entries[0].Name, Is.EqualTo("greetin"));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Writer_RoundTripsMultipleFiles() {
    var w = new F2fsWriter();
    var files = new Dictionary<string, byte[]> {
      ["a"] = Encoding.UTF8.GetBytes("alpha"),
      ["b"] = Encoding.UTF8.GetBytes("beta-beta"),
      ["c"] = Encoding.UTF8.GetBytes("gamma gamma gamma"),
      ["d"] = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 },
      ["e"] = Encoding.UTF8.GetBytes(new string('x', 5000)), // > 1 block
    };
    foreach (var (name, data) in files) w.AddFile(name, data);
    var img = w.Build();

    using var ms = new MemoryStream(img);
    var r = new F2fsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(5));

    foreach (var entry in r.Entries) {
      // Names are truncated to 7 bytes per inline-slot limit; single-char names fit exactly.
      Assert.That(files.ContainsKey(entry.Name), Is.True, $"Unexpected entry: {entry.Name}");
      var data = r.Extract(entry);
      Assert.That(data, Is.EqualTo(files[entry.Name]), $"Roundtrip failed for {entry.Name}");
    }
  }

  [Test, Category("ErrorHandling")]
  public void Writer_OversizedFile_Throws() {
    var w = new F2fsWriter();
    Assert.Throws<InvalidOperationException>(() => w.AddFile("big.bin", new byte[924 * 4096]));
  }
}
