using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.ReiserFs;

[TestFixture]
public class ReiserFsTests {

  // Build a minimal ReiserFS v3.6 image with spec-compliant offsets.
  // Superblock layout (per kernel fs/reiserfs/reiserfs.h):
  //   +0   u32 s_block_count         +44  u16 s_blocksize
  //   +4   u32 s_free_blocks         +52  u8[10] s_magic
  //   +8   u32 s_root_block          +64  u32 s_hash_function_code
  //   +12  32  journal_params        +68  u16 s_tree_height
  //                                  +72  u16 s_version
  //                                  +84  u8[16] s_uuid
  private static byte[] BuildMinimalReiserFs(params (string Name, byte[] Data)[] files) {
    const int blockSize = 4096;
    const int sbOff = 65536;
    const int rootBlock = 18;
    var imageSize = 512 * 1024;
    var img = new byte[imageSize];

    // Superblock
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(sbOff), (uint)(imageSize / blockSize));
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(sbOff + 8), rootBlock);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(sbOff + 44), (ushort)blockSize);
    "ReIsEr2Fs"u8.CopyTo(img.AsSpan(sbOff + 52));
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(sbOff + 72), 2); // s_version = 3.6
    img[sbOff + 84] = 0x11; // non-zero uuid byte

    // Root block (leaf, level=1)
    var rootBlockOff = rootBlock * blockSize;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(rootBlockOff), 1);

    var nrItems = files.Length > 0 ? 1 + files.Length : 0;
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(rootBlockOff + 2), (ushort)nrItems);

    if (files.Length == 0) return img;

    var dirDehData = new List<byte>();
    var nameData = new List<byte>();
    var nameOffsets = new List<int>();

    for (int i = 0; i < files.Length; i++) {
      nameOffsets.Add(nameData.Count);
      nameData.AddRange(Encoding.UTF8.GetBytes(files[i].Name));
      nameData.Add(0);
    }
    for (int i = 0; i < files.Length; i++) {
      var deh = new byte[16];
      BinaryPrimitives.WriteUInt32LittleEndian(deh.AsSpan(0), 0);
      BinaryPrimitives.WriteUInt32LittleEndian(deh.AsSpan(4), 2);
      BinaryPrimitives.WriteUInt32LittleEndian(deh.AsSpan(8), (uint)(100 + i));
      var nameLocInItem = files.Length * 16 + nameOffsets[i];
      BinaryPrimitives.WriteUInt16LittleEndian(deh.AsSpan(12), (ushort)nameLocInItem);
      BinaryPrimitives.WriteUInt16LittleEndian(deh.AsSpan(14), 4); // visible
      dirDehData.AddRange(deh);
    }

    var dirItemData = new byte[dirDehData.Count + nameData.Count];
    dirDehData.ToArray().CopyTo(dirItemData, 0);
    nameData.ToArray().CopyTo(dirItemData, dirDehData.Count);

    var dirDataOff = rootBlockOff + blockSize - dirItemData.Length;
    dirItemData.CopyTo(img, dirDataOff);

    // Dir item header — key uses KEY_FORMAT_2 with type=TYPE_DIRENTRY (3)
    // packed in bits 60-63 of offset_v2 at +8.
    var ih0Off = rootBlockOff + 24;
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(ih0Off), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(ih0Off + 4), 2);
    BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(ih0Off + 8), (3UL << 60) | 1UL); // type=DIRENTRY, offset=1
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(ih0Off + 16), (ushort)files.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(ih0Off + 18), (ushort)dirItemData.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(ih0Off + 20), (ushort)(dirDataOff - rootBlockOff));

    // Direct items — key uses KEY_FORMAT_2 with type=TYPE_DIRECT (2).
    for (int i = 0; i < files.Length; i++) {
      var (_, data) = files[i];
      var ihOff = rootBlockOff + 24 + (i + 1) * 24;
      var dataLocation = dirDataOff - (i + 1) * Math.Max(data.Length, 1);

      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(ihOff), 2);
      BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(ihOff + 4), (uint)(100 + i));
      BinaryPrimitives.WriteUInt64LittleEndian(img.AsSpan(ihOff + 8), (2UL << 60) | 1UL); // type=DIRECT, offset=1
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(ihOff + 16), 0xFFFF);
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(ihOff + 18), (ushort)data.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(img.AsSpan(ihOff + 20), (ushort)(dataLocation - rootBlockOff));

      if (data.Length > 0 && dataLocation >= rootBlockOff && dataLocation + data.Length <= img.Length)
        data.CopyTo(img, dataLocation);
    }

    return img;
  }

  [Test, Category("HappyPath")]
  public void Read_SingleFile() {
    var img = BuildMinimalReiserFs(("test.txt", "Hello"u8.ToArray()));
    using var ms = new MemoryStream(img);
    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_SingleFile() {
    var content = "ReiserFS data"u8.ToArray();
    var img = BuildMinimalReiserFs(("test.txt", content));
    using var ms = new MemoryStream(img);
    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(content));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("ReiserFs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".reiserfs"));
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(3));
    Assert.That(desc.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.ReiserFs.ReiserFsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_BadMagic_Throws() {
    var data = new byte[70000];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new FileSystem.ReiserFs.ReiserFsReader(ms));
  }

  // ── WORM creation ────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<Compression.Registry.IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<Compression.Registry.IArchiveWriteConstraints>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_WriteConstraints_Expose128MiBFloor() {
    var d = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
    var c = (Compression.Registry.IArchiveWriteConstraints)d;
    Assert.That(c.MaxTotalArchiveSize, Is.Null, "ReiserFS has no inherent ceiling.");
    Assert.That(c.MinTotalArchiveSize, Is.EqualTo(128L * 1024 * 1024));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleFile_RoundTrips() {
    var payload = "Hello ReiserFS!"u8.ToArray();
    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    w.AddFile("readme.txt", payload);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("readme.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_MultipleFiles_AllRoundTrip() {
    var p1 = new byte[64];
    var p2 = new byte[128];
    new Random(11).NextBytes(p1);
    new Random(22).NextBytes(p2);

    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    w.AddFile("alpha.bin", p1);
    w.AddFile("beta.bin", p2);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.ReiserFs.ReiserFsReader(ms);
    var names = r.Entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("alpha.bin"));
    Assert.That(names, Does.Contain("beta.bin"));
    var a = r.Entries.First(e => e.Name == "alpha.bin");
    var b = r.Entries.First(e => e.Name == "beta.bin");
    Assert.That(r.Extract(a), Is.EqualTo(p1));
    Assert.That(r.Extract(b), Is.EqualTo(p2));
  }

  [Test, Category("HappyPath")]
  public void Writer_HasReiserFs36Magic() {
    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    w.AddFile("x.txt", "x"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();
    Assert.That(Encoding.ASCII.GetString(bytes, 65536 + 52, 9), Is.EqualTo("ReIsEr2Fs"),
      "ReiserFS 3.6 writer must emit \"ReIsEr2Fs\" at superblock offset +52.");
  }

  [Test, Category("HappyPath")]
  public void Writer_SuperblockFieldsAtSpecOffsets() {
    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    w.AddFile("spec.txt", "spec"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();
    const int sb = 65536;

    // root block at +8 (per Linux kernel fs/reiserfs/reiserfs.h). Our writer ships
    // a default-sized journal (reiserfsprogs:journal_default_size = 8192 blocks starting
    // at block 18), so the root leaf sits immediately AFTER the journal body + header.
    // Real mkfs.reiserfs does the same — a fresh 34 MB image has s_root_block ≈ 8211.
    var root = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(sb + 8));
    Assert.That(root, Is.GreaterThan(18u),
      "s_root_block must be at spec offset +8 and must sit past the journal.");

    // blocksize at +44
    var bs = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(sb + 44));
    Assert.That(bs, Is.EqualTo((ushort)4096), "s_blocksize must be at spec offset +44.");

    // hash function code at +64 (R5_HASH = 3)
    var hash = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(sb + 64));
    Assert.That(hash, Is.EqualTo(3u), "s_hash_function_code R5_HASH at +64.");

    // tree height at +68
    var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(sb + 68));
    Assert.That(height, Is.GreaterThanOrEqualTo((ushort)2), "s_tree_height at +68.");

    // bmap_nr at +70
    var bmap = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(sb + 70));
    Assert.That(bmap, Is.GreaterThanOrEqualTo((ushort)1), "s_bmap_nr at +70 must be >=1.");

    // version at +72 (REISERFS_VERSION_2 == 2 for 3.6)
    var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(sb + 72));
    Assert.That(version, Is.EqualTo((ushort)2), "s_version = 2 (3.6) at +72.");
  }

  [Test, Category("HappyPath")]
  public void Writer_HasNonZeroUuid() {
    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    w.AddFile("u.txt", "u"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();
    var uuid = bytes.AsSpan(65536 + 84, 16);
    bool anyNonZero = false;
    foreach (var b in uuid) if (b != 0) { anyNonZero = true; break; }
    Assert.That(anyNonZero, Is.True, "s_uuid at +84 must be non-zero.");
  }

  [Test, Category("HappyPath")]
  public void Writer_LeafBlockHead_HasFreeSpaceAndRightDelimKey() {
    var w = new FileSystem.ReiserFs.ReiserFsWriter();
    w.AddFile("bh.txt", "bh"u8.ToArray());
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    var bytes = ms.ToArray();

    // Root leaf block is past the default journal (writer emits the spec-mandated
    // 8192-block journal starting at block 18 + journal header). Read s_root_block
    // from the superblock instead of assuming a fixed offset.
    var rootBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(65536 + 8));
    var boff = rootBlock * 4096;
    var level = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(boff));
    Assert.That(level, Is.EqualTo((ushort)1), "blk_level must be 1 for leaf.");

    var freeSpace = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(boff + 4));
    Assert.That(freeSpace, Is.GreaterThan((ushort)0),
      "blk_free_space (at block_head +4) must be populated.");

    // blk_right_delim_key occupies block_head +8..+24; spec says "maximum key"
    // for no-right-sibling leaves. Must be non-zero.
    bool anyNonZero = false;
    for (int i = 8; i < 24; i++) if (bytes[boff + i] != 0) { anyNonZero = true; break; }
    Assert.That(anyNonZero, Is.True,
      "blk_right_delim_key (block_head +8..+24) must be written.");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "reiserfs descriptor test"u8.ToArray());
      var d = new FileSystem.ReiserFs.ReiserFsFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "note.txt", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries.Where(e => !e.IsDirectory).Select(e => e.Name), Has.Member("note.txt"));
    } finally {
      File.Delete(tmp);
    }
  }
}
