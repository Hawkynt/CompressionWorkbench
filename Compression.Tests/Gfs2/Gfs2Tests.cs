using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Gfs2;

[TestFixture]
public class Gfs2Tests {

  private const uint MetaMagic = 0x01161970u;
  private const uint MetaTypeSb = 1;
  private const uint MetaTypeDi = 4;
  private const long SbOffset = 65536;
  private const uint BlockSize = 4096;

  /// <summary>
  /// Synthesises a minimal GFS2 image: superblock at offset 65536 + a root
  /// dinode at block 8 holding a single inline directory entry ("hello")
  /// that points to a regular-file dinode at block 9 with inline payload
  /// "Hello, GFS2!".
  /// </summary>
  private static byte[] BuildMinimal(
      string lockProto = "lock_dlm",
      string lockTable = "mycluster:myfs",
      ulong rootBlock = 8,
      ulong fileBlock = 9,
      string fileName = "hello",
      string fileBody = "Hello, GFS2!") {
    // Image: enough blocks to host SB (offset 65536 = block 16) + root inode
    // (block 8) + file inode (block 9). Need at least 17 blocks so block 16
    // has 4 KB of room for the superblock + master/root inum trailers.
    var image = new byte[32 * BlockSize];

    // ── Superblock @ 65536 ───────────────────────────────────────────
    var sb = image.AsSpan((int)SbOffset);

    // gfs2_meta_header (16 bytes) — magic + type + format + jid (all BE u32).
    BinaryPrimitives.WriteUInt32BigEndian(sb[..4], MetaMagic);
    BinaryPrimitives.WriteUInt32BigEndian(sb[4..8], MetaTypeSb);
    BinaryPrimitives.WriteUInt32BigEndian(sb[8..12], 0); // mh_format
    BinaryPrimitives.WriteUInt32BigEndian(sb[12..16], 0); // mh_jid

    BinaryPrimitives.WriteUInt32BigEndian(sb.Slice(16, 4), 1801u); // sb_fs_format
    BinaryPrimitives.WriteUInt32BigEndian(sb.Slice(20, 4), 1900u); // sb_multihost_format
    // pad0 @24
    BinaryPrimitives.WriteUInt32BigEndian(sb.Slice(28, 4), BlockSize); // sb_bsize
    BinaryPrimitives.WriteUInt32BigEndian(sb.Slice(32, 4), 12u); // sb_bsize_shift (1<<12==4096)
    // pad1 @36
    // sb_master_dir (gfs2_inum: formal_ino, no_addr) @40
    BinaryPrimitives.WriteUInt64BigEndian(sb.Slice(40, 8), 0xCAFEBABE_DEADBEEFUL);
    BinaryPrimitives.WriteUInt64BigEndian(sb.Slice(48, 8), 5UL); // master at block 5
    // sb_root_dir @56
    BinaryPrimitives.WriteUInt64BigEndian(sb.Slice(56, 8), 0x1111_2222_3333_4444UL);
    BinaryPrimitives.WriteUInt64BigEndian(sb.Slice(64, 8), rootBlock);

    // sb_lockproto[64] @72
    Encoding.ASCII.GetBytes(lockProto).CopyTo(sb.Slice(72, Math.Min(63, lockProto.Length)));
    // sb_locktable[64] @136
    Encoding.ASCII.GetBytes(lockTable).CopyTo(sb.Slice(136, Math.Min(63, lockTable.Length)));

    // sb_uuid[16] @200 — recognisable pattern.
    for (var i = 0; i < 16; i++) sb[200 + i] = (byte)(0xA0 + i);

    // ── Root dinode @ block 8 ──────────────────────────────────────────
    var rootOff = rootBlock * BlockSize;
    var root = image.AsSpan((int)rootOff, (int)BlockSize);

    BinaryPrimitives.WriteUInt32BigEndian(root[..4], MetaMagic);
    BinaryPrimitives.WriteUInt32BigEndian(root.Slice(4, 4), MetaTypeDi);

    // di_num @16 (16 bytes)
    BinaryPrimitives.WriteUInt64BigEndian(root.Slice(16, 8), 0x1111_2222_3333_4444UL);
    BinaryPrimitives.WriteUInt64BigEndian(root.Slice(24, 8), rootBlock);

    // di_mode @32 — S_IFDIR (0x4000) | 0755
    BinaryPrimitives.WriteUInt32BigEndian(root.Slice(32, 4), 0x41EDu);
    BinaryPrimitives.WriteUInt32BigEndian(root.Slice(36, 4), 0u); // uid
    BinaryPrimitives.WriteUInt32BigEndian(root.Slice(40, 4), 0u); // gid
    BinaryPrimitives.WriteUInt32BigEndian(root.Slice(44, 4), 2u); // nlink
    BinaryPrimitives.WriteUInt64BigEndian(root.Slice(48, 8), 232UL); // di_size = header only
    BinaryPrimitives.WriteUInt64BigEndian(root.Slice(56, 8), 1UL); // di_blocks
    BinaryPrimitives.WriteUInt64BigEndian(root.Slice(64, 8), 1_700_000_000UL); // atime — 2023
    BinaryPrimitives.WriteUInt64BigEndian(root.Slice(72, 8), 1_700_000_500UL); // mtime
    BinaryPrimitives.WriteUInt64BigEndian(root.Slice(80, 8), 1_700_000_100UL); // ctime
    // di_height @114 = 0
    BinaryPrimitives.WriteUInt16BigEndian(root.Slice(114, 2), 0);
    // di_depth @122 = 0
    BinaryPrimitives.WriteUInt16BigEndian(root.Slice(122, 2), 0);
    // di_entries @124 = 1 (only "hello"; "." and ".." not needed for our walker test)
    BinaryPrimitives.WriteUInt32BigEndian(root.Slice(124, 4), 1u);

    // ── One inline gfs2_dirent at offset 232 ───────────────────────────
    var de = root[232..];
    // de_inum: formal=0xBEEF_..., addr=fileBlock
    BinaryPrimitives.WriteUInt64BigEndian(de.Slice(0, 8), 0xBEEF_CAFE_1234_5678UL);
    BinaryPrimitives.WriteUInt64BigEndian(de.Slice(8, 8), fileBlock);
    // de_hash @16
    BinaryPrimitives.WriteUInt32BigEndian(de.Slice(16, 4), 0u);
    // de_rec_len @20 — must be 8-aligned, ≥ 32 + nameLen.
    var nameBytes = Encoding.UTF8.GetBytes(fileName);
    var recLen = 32 + nameBytes.Length;
    recLen = (recLen + 7) & ~7; // 8-align
    BinaryPrimitives.WriteUInt16BigEndian(de.Slice(20, 2), (ushort)recLen);
    // de_name_len @22
    BinaryPrimitives.WriteUInt16BigEndian(de.Slice(22, 2), (ushort)nameBytes.Length);
    // de_type @24 — DT_REG = 8
    BinaryPrimitives.WriteUInt16BigEndian(de.Slice(24, 2), 8);
    // name @32
    nameBytes.CopyTo(de.Slice(32, nameBytes.Length));

    // ── File dinode @ block 9 ──────────────────────────────────────────
    var fileOff = fileBlock * BlockSize;
    var f = image.AsSpan((int)fileOff, (int)BlockSize);

    BinaryPrimitives.WriteUInt32BigEndian(f[..4], MetaMagic);
    BinaryPrimitives.WriteUInt32BigEndian(f.Slice(4, 4), MetaTypeDi);
    BinaryPrimitives.WriteUInt64BigEndian(f.Slice(16, 8), 0xBEEF_CAFE_1234_5678UL); // formal
    BinaryPrimitives.WriteUInt64BigEndian(f.Slice(24, 8), fileBlock);
    BinaryPrimitives.WriteUInt32BigEndian(f.Slice(32, 4), 0x81A4u); // S_IFREG | 0644
    BinaryPrimitives.WriteUInt32BigEndian(f.Slice(44, 4), 1u); // nlink
    var bodyBytes = Encoding.UTF8.GetBytes(fileBody);
    BinaryPrimitives.WriteUInt64BigEndian(f.Slice(48, 8), (ulong)bodyBytes.Length); // di_size
    BinaryPrimitives.WriteUInt64BigEndian(f.Slice(56, 8), 0UL); // di_blocks (inline)
    BinaryPrimitives.WriteUInt64BigEndian(f.Slice(64, 8), 1_700_000_000UL);
    BinaryPrimitives.WriteUInt64BigEndian(f.Slice(72, 8), 1_700_000_600UL); // mtime
    BinaryPrimitives.WriteUInt64BigEndian(f.Slice(80, 8), 1_700_000_200UL);
    BinaryPrimitives.WriteUInt16BigEndian(f.Slice(114, 2), 0); // di_height
    // Inline data @232
    bodyBytes.CopyTo(f.Slice(232, bodyBytes.Length));

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Gfs2.Gfs2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Gfs2"));
    Assert.That(d.DisplayName, Is.EqualTo("GFS2 (Global File System 2)"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.Extensions, Does.Contain(".gfs2"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(65536));
    Assert.That(d.MagicSignatures[0].Bytes,
      Is.EqualTo(new byte[] { 0x01, 0x16, 0x19, 0x70 }));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.85).Within(0.01));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface_AndEntries() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Gfs2.Gfs2FormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.gfs2"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("superblock.bin"));
    Assert.That(names, Does.Contain("hello"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeaderAndFileContent() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Gfs2.Gfs2FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "gfs2_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);

      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "superblock.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "hello")), Is.True);

      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      Assert.That(meta, Does.Contain("superblock_valid=True"));
      Assert.That(meta, Does.Contain("block_size=4096"));
      Assert.That(meta, Does.Contain("block_size_shift=12"));
      Assert.That(meta, Does.Contain("root_inode_block=8"));
      Assert.That(meta, Does.Contain("master_inode_block=5"));
      Assert.That(meta, Does.Contain("lock_proto=lock_dlm"));
      Assert.That(meta, Does.Contain("lock_table=mycluster:myfs"));
      Assert.That(meta, Does.Contain("root_entry_count=1"));

      var body = File.ReadAllText(Path.Combine(outDir, "hello"));
      Assert.That(body, Is.EqualTo("Hello, GFS2!"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyStream_DoesNotThrow() {
    using var ms = new MemoryStream(Array.Empty<byte>());
    var d = new FileSystem.Gfs2.Gfs2FormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
    ms.Position = 0;
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.gfs2"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("superblock.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_FallsBackToPartial() {
    // 128 KB of zeros — no GFS2 magic anywhere.
    var img = new byte[128 * 1024];
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Gfs2.Gfs2FormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("superblock.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Defragment_Throws_NotSupported() {
    var d = new FileSystem.Gfs2.Gfs2FormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal());
    var ex = Assert.Throws<NotSupportedException>(() => d.Defragment(ms));
    Assert.That(ex!.Message, Does.Contain("read-only"));
  }

  [Test, Category("ErrorHandling")]
  public void DefragmentWithOptions_Throws_NotSupported() {
    var d = new FileSystem.Gfs2.Gfs2FormatDescriptor();
    using var ms = new MemoryStream(BuildMinimal());
    var ex = Assert.Throws<NotSupportedException>(() => d.Defragment(ms, new DefragOptions()));
    Assert.That(ex!.Message, Does.Contain("read-only"));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesUuid_And_LockMetadata() {
    var img = BuildMinimal(lockProto: "lock_nolock", lockTable: "node1:vol1");
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Gfs2.Gfs2Reader(ms);
    Assert.That(r.SuperblockValid, Is.True);
    Assert.That(r.BlockSize, Is.EqualTo(4096u));
    Assert.That(r.LockProto, Is.EqualTo("lock_nolock"));
    Assert.That(r.LockTable, Is.EqualTo("node1:vol1"));
    Assert.That(r.UuidHex.Length, Is.EqualTo(32));
    Assert.That(r.UuidHex, Does.StartWith("A0A1A2A3"));
  }

  [Test, Category("HappyPath")]
  public void Reader_Skips_DotEntries_And_ZeroInodes() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var r = new FileSystem.Gfs2.Gfs2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello"));
    Assert.That(r.Entries[0].IsDirectory, Is.False);
    Assert.That(r.Entries[0].Size, Is.EqualTo("Hello, GFS2!".Length));
  }
}
