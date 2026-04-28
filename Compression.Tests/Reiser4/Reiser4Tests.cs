using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Reiser4;

[TestFixture]
public class Reiser4Tests {

  /// <summary>Synthesises a minimal Reiser4 image with master + format40 superblocks.</summary>
  private static byte[] BuildMinimal(string label = "TESTFS") {
    var image = new byte[80 * 1024]; // 80 KB — covers offset 65536 master + 4 KB format40

    // Master superblock at offset 65536.
    var msbOff = 65536;
    Encoding.ASCII.GetBytes("ReIsEr4").CopyTo(image.AsSpan(msbOff, 7));
    // ms_magic [16 bytes] — bytes 7..15 stay NUL.
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(msbOff + 16, 2), 0); // disk_plugin_id = format40
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(msbOff + 18, 2), 4096); // blksize
    // UUID 16 bytes at +20 — recognisable pattern.
    for (var i = 0; i < 16; i++) image[msbOff + 20 + i] = (byte)(0xA0 + i);
    // Label 16 bytes at +36 (NUL-padded).
    Encoding.ASCII.GetBytes(label).CopyTo(image.AsSpan(msbOff + 36));

    // Format40 superblock at master_block + blocksize = 65536 + 4096 = 69632.
    var f40 = 65536 + 4096;
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(f40 + 0, 8), 12345UL); // sb_block_count
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(f40 + 8, 8), 6789UL);  // sb_free_blocks
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(f40 + 16, 8), 1024UL); // sb_root_block
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(f40 + 24, 8), 42UL);   // sb_oid[0] root_dir
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(f40 + 32, 8), 999UL);  // sb_oid[1] oid_max
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(f40 + 40, 8), 7UL);    // sb_flushes
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(f40 + 48, 4), 0xCAFEBABEu); // sb_mkfs_id
    Encoding.ASCII.GetBytes("ReIsEr40FoRmAt").CopyTo(image.AsSpan(f40 + 52));     // sb_magic
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(f40 + 68, 2), 4);        // tree_height
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(f40 + 70, 2), 0);        // tail policy
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(f40 + 80, 4), 0x40);     // sb_version
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Reiser4.Reiser4FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Reiser4"));
    Assert.That(d.DisplayName, Is.EqualTo("Reiser4"));
    Assert.That(d.Extensions, Does.Contain(".reiser4"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(65536));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.9).Within(0.01));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("ReIsEr4"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Reiser4.Reiser4FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(3));
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.reiser4"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("master_superblock.bin"));
    Assert.That(names, Does.Contain("format40_superblock.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeader() {
    var img = BuildMinimal("MYVOL");
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Reiser4.Reiser4FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "reiser4_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.reiser4")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "master_superblock.bin")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "format40_superblock.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      Assert.That(meta, Does.Contain("blocksize=4096"));
      Assert.That(meta, Does.Contain("disk_plugin_id=0"));
      Assert.That(meta, Does.Contain("label=MYVOL"));
      Assert.That(meta, Does.Contain("format40_present=True"));
      Assert.That(meta, Does.Contain("block_count=12345"));
      Assert.That(meta, Does.Contain("free_blocks=6789"));
      Assert.That(meta, Does.Contain("root_block=1024"));
      Assert.That(meta, Does.Contain("file_count=999"));
      Assert.That(meta, Does.Contain("tree_height=4"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyStream_DoesNotThrow() {
    using var ms = new MemoryStream(Array.Empty<byte>());
    var d = new FileSystem.Reiser4.Reiser4FormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.reiser4"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_FallsBackToPartial() {
    var rng = new Random(0xCAFE);
    var buf = new byte[80 * 1024];
    rng.NextBytes(buf);
    // Ensure the magic offset doesn't accidentally land on "ReIsEr4".
    buf[65536] = 0x00;
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.Reiser4.Reiser4FormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.reiser4"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("master_superblock.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_GarbageInput_WritesPartialMetadata() {
    var buf = new byte[1024]; // shorter than even the master offset
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.Reiser4.Reiser4FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "reiser4_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.reiser4")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void MasterSb_Parses_Magic_Bytes_Match() {
    Assert.That(Encoding.ASCII.GetString(FileSystem.Reiser4.Reiser4MasterSb.MasterMagic),
                Is.EqualTo("ReIsEr4"));
    Assert.That(Encoding.ASCII.GetString(FileSystem.Reiser4.Reiser4MasterSb.Format40Magic),
                Is.EqualTo("ReIsEr40FoRmAt"));
    Assert.That(FileSystem.Reiser4.Reiser4MasterSb.MasterOffset, Is.EqualTo(65536L));
  }

  // ── Writer tests ─────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_MasterSbBytesMatchSpec() {
    var w = new FileSystem.Reiser4.Reiser4Writer {
      Uuid = Enumerable.Range(0xA0, 16).Select(i => (byte)i).ToArray(),
      MkfsId = 0xDEADBEEFu,
      BlockCount = 4096,
      Label = "TESTFS",
    };
    var img = w.Build();
    Assert.That(img.LongLength, Is.EqualTo(4096L * 4096), "image size = blocks * 4096");

    // Master SB at byte offset 65536.
    Assert.That(Encoding.ASCII.GetString(img, 65536, 7), Is.EqualTo("ReIsEr4"));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(65536 + 16, 2)),
      Is.EqualTo(0), "ms_format = 0 (format40)");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(65536 + 18, 2)),
      Is.EqualTo(4096), "ms_blksize = 4096");
    Assert.That(img.AsSpan(65536 + 20, 16).ToArray(),
      Is.EqualTo(Enumerable.Range(0xA0, 16).Select(i => (byte)i).ToArray()), "UUID round-trip");
    Assert.That(Encoding.ASCII.GetString(img, 65536 + 36, 6), Is.EqualTo("TESTFS"));
  }

  [Test, Category("HappyPath")]
  public void Writer_Format40SbBytesMatchSpec() {
    var w = new FileSystem.Reiser4.Reiser4Writer {
      MkfsId = 0x12345678u,
      BlockCount = 4096,
    };
    var img = w.Build();

    // Format40 SB at byte offset 69632 (= 65536 + 4096).
    var f40 = 69632;
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(f40 + 0, 8)),
      Is.EqualTo(4096UL), "block_count");
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(f40 + 8, 8)),
      Is.EqualTo(4096UL - 25), "free_blocks = total - 25 reserved");
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(f40 + 16, 8)),
      Is.EqualTo(23UL), "root_block = 23");
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(f40 + 24, 8)),
      Is.EqualTo(0x10000UL), "next_oid = OID40_RESERVED");
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(f40 + 32, 8)),
      Is.EqualTo(1UL), "file_count = 1 (root dir)");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(f40 + 48, 4)),
      Is.EqualTo(0x12345678u), "mkfs_id round-trip");
    Assert.That(Encoding.ASCII.GetString(img, f40 + 52, 14), Is.EqualTo("ReIsEr40FoRmAt"));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(f40 + 68, 2)),
      Is.EqualTo(2), "tree_height");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(f40 + 70, 2)),
      Is.EqualTo(2), "tail_policy = smart");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(f40 + 80, 4)),
      Is.EqualTo(2u), "sb_version");
  }

  [Test, Category("HappyPath")]
  public void Writer_BitmapHasReservedBlocksMarked() {
    var w = new FileSystem.Reiser4.Reiser4Writer { MkfsId = 0xCAFEu, BlockCount = 4096 };
    var img = w.Build();

    // Block 18 (offset 73728): 4-byte adler32 then bitmap data.
    var bitmapOff = 18 * 4096;
    var bytes = img.AsSpan(bitmapOff, 4096);
    // First 25 bits set = bytes 4,5,6 = 0xFF and byte 7 = 0x01.
    Assert.That(bytes[4], Is.EqualTo((byte)0xff));
    Assert.That(bytes[5], Is.EqualTo((byte)0xff));
    Assert.That(bytes[6], Is.EqualTo((byte)0xff));
    Assert.That(bytes[7], Is.EqualTo((byte)0x01));
    // Adler32 over bytes 4..4095 must be non-zero (1 is valid for empty buffer
    // but ours has 25 set bits + filler tail).
    var adler = BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]);
    Assert.That(adler, Is.Not.EqualTo(0u));
  }

  [Test, Category("HappyPath")]
  public void Writer_RoundTrip_OurReader() {
    var w = new FileSystem.Reiser4.Reiser4Writer {
      Uuid = Enumerable.Range(0x10, 16).Select(i => (byte)i).ToArray(),
      MkfsId = 0xABCD1234u,
      BlockCount = 4096,
      Label = "ROUNDTRIP",
    };
    var img = w.Build();

    var sb = FileSystem.Reiser4.Reiser4MasterSb.TryParse(img);
    Assert.That(sb.Valid, Is.True);
    Assert.That(sb.BlockSize, Is.EqualTo(4096));
    Assert.That(sb.DiskPluginId, Is.EqualTo(0));
    Assert.That(sb.Label, Is.EqualTo("ROUNDTRIP"));
    Assert.That(sb.Format40Present, Is.True);
    Assert.That(sb.BlockCount, Is.EqualTo(4096UL));
    Assert.That(sb.FreeBlocks, Is.EqualTo(4096UL - 25));
    Assert.That(sb.RootBlock, Is.EqualTo(23UL));
    Assert.That(sb.MkfsId, Is.EqualTo(0xABCD1234u));
    Assert.That(sb.TreeHeight, Is.EqualTo(2));
    Assert.That(sb.Policy, Is.EqualTo(2));

    // Descriptor List() should expose the standard surface from our own image.
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Reiser4.Reiser4FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("master_superblock.bin"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("format40_superblock.bin"));
  }

  [Test, Category("HappyPath")]
  public void Writer_BlockCountClamped_ToMinimum() {
    var w = new FileSystem.Reiser4.Reiser4Writer { BlockCount = 100 };
    var img = w.Build();
    // Should clamp up to MinBlockCount (4096) blocks.
    Assert.That(img.LongLength, Is.EqualTo(4096L * 4096));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate() {
    var d = new FileSystem.Reiser4.Reiser4FormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_EmitsValidImage() {
    var d = new FileSystem.Reiser4.Reiser4FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [], new FormatCreateOptions());
    ms.Position = 0;
    var img = ms.ToArray();
    Assert.That(img.LongLength, Is.GreaterThanOrEqualTo(4096L * 4096));
    var sb = FileSystem.Reiser4.Reiser4MasterSb.TryParse(img);
    Assert.That(sb.Valid, Is.True);
    Assert.That(sb.Format40Present, Is.True);
  }
}
