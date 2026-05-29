using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Ocfs2;

[TestFixture]
public class Ocfs2Tests {

  /// <summary>
  /// Synthesises a minimal OCFS2 image with the superblock dinode at block 2
  /// (byte offset 8192, default 4 KB blocksize), the OCFSV2 magic at dinode
  /// offset 0, and the embedded ocfs2_super_block fields at id2 = +0xC0.
  /// </summary>
  private static byte[] BuildMinimal(string label = "TESTVOL", uint blocksizeBits = 12, uint clustersizeBits = 12) {
    var image = new byte[64 * 1024]; // 64 KB — covers offset 8192 + a 4 KB dinode block.

    var sbBlock = 8192;
    // Dinode i_signature[8] = "OCFSV2" + 2 NUL pad.
    Encoding.ASCII.GetBytes("OCFSV2").CopyTo(image.AsSpan(sbBlock, 6));
    // i_generation, i_suballoc_slot, i_suballoc_bit at +8/+12/+14 — leave zeroed.
    // i_size at +0x20 — leave zeroed.

    // ocfs2_super_block at +0xC0.
    var sb = sbBlock + 0xC0;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 0x00, 2), 1);    // s_major_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 0x02, 2), 6);    // s_minor_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 0x04, 2), 0);    // s_mnt_count
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 0x06, 2), 0);    // s_max_mnt_count
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x28, 8), 5UL);  // s_root_blkno
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x30, 8), 6UL);  // s_system_dir_blkno
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 0x38, 4), blocksizeBits);   // s_blocksize_bits
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sb + 0x3C, 4), clustersizeBits); // s_clustersize_bits
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(sb + 0x40, 2), 4);    // s_max_slots
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(sb + 0x48, 8), 100UL); // s_first_cluster_group

    // s_label[64] @ +0x50.
    Encoding.ASCII.GetBytes(label).CopyTo(image.AsSpan(sb + 0x50));

    // s_uuid[16] @ +0x90 — recognisable pattern.
    for (var i = 0; i < 16; i++) image[sb + 0x90 + i] = (byte)(0xB0 + i);

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ocfs2"));
    Assert.That(d.DisplayName, Does.StartWith("OCFS2"));
    Assert.That(d.Extensions, Does.Contain(".ocfs2"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(8192));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.85).Within(0.01));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("OCFSV2"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    var entries = d.List(ms, null);
    // The hand-built image doesn't have proper dinode structures,
    // so it falls back to triage path.
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(2));
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.ocfs2"));
    Assert.That(names, Does.Contain("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeader() {
    var img = BuildMinimal("MY-RAC-VOL");
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "ocfs2_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.ocfs2")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "superblock.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      Assert.That(meta, Does.Contain("superblock_offset=8192"));
      Assert.That(meta, Does.Contain("detected_blocksize=4096"));
      Assert.That(meta, Does.Contain("version=1.6"));
      Assert.That(meta, Does.Contain("blocksize_bits=12"));
      Assert.That(meta, Does.Contain("clustersize_bits=12"));
      Assert.That(meta, Does.Contain("blocksize=4096"));
      Assert.That(meta, Does.Contain("clustersize=4096"));
      Assert.That(meta, Does.Contain("max_slots=4"));
      Assert.That(meta, Does.Contain("root_blkno=5"));
      Assert.That(meta, Does.Contain("system_dir_blkno=6"));
      Assert.That(meta, Does.Contain("first_cluster_group=100"));
      Assert.That(meta, Does.Contain("label=MY-RAC-VOL"));
      // UUID hex starts with B0 B1 B2... at the recognisable pattern we wrote.
      Assert.That(meta, Does.Contain("uuid_hex=B0B1B2B3B4B5B6B7B8B9BABBBCBDBEBF"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyStream_DoesNotThrow() {
    using var ms = new MemoryStream(Array.Empty<byte>());
    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.ocfs2"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_FallsBackToPartial() {
    var rng = new Random(0xACE);
    var buf = new byte[32 * 1024];
    rng.NextBytes(buf);
    // Stomp every plausible block-2 offset and the first 16 KB so random
    // bytes don't accidentally produce "OCFSV2" anywhere.
    for (var i = 0; i + 6 <= buf.Length && i < 16 * 1024; i++) {
      if (buf[i] == 'O' && buf[i + 1] == 'C') buf[i] = 0;
    }
    foreach (var bs in new[] { 512, 1024, 2048, 4096 }) {
      var off = bs * 2;
      if (off + 6 <= buf.Length)
        for (var j = 0; j < 6; j++) buf[off + j] = 0;
    }
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.ocfs2"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Not.Contain("superblock.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_GarbageInput_WritesPartialMetadata() {
    var buf = new byte[1024]; // shorter than the SB offset
    using var ms = new MemoryStream(buf);
    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "ocfs2_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.ocfs2")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Superblock_Constants_Match_KernelSpec() {
    Assert.That(Encoding.ASCII.GetString(FileSystem.Ocfs2.Ocfs2Superblock.SignatureBytes),
                Is.EqualTo("OCFSV2"));
    Assert.That(FileSystem.Ocfs2.Ocfs2Superblock.SuperBlockBlkno, Is.EqualTo(2));
    Assert.That(FileSystem.Ocfs2.Ocfs2Superblock.DefaultBlockSize, Is.EqualTo(4096));
    Assert.That(FileSystem.Ocfs2.Ocfs2Superblock.DefaultSuperBlockOffset, Is.EqualTo(8192L));
    Assert.That(FileSystem.Ocfs2.Ocfs2Superblock.SuperOffsetInDinode, Is.EqualTo(0xC0));
  }

  // ── Capability checks ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsCreatable() {
    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsModifiable() {
    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsDefragmentable() {
    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsExtentMap() {
    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  // ── Writer round-trip tests ────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleFile_RoundTrips() {
    var payload = "Hello OCFS2!"u8.ToArray();
    var w = new FileSystem.Ocfs2.Ocfs2Writer();
    w.AddFile("test.txt", payload);
    var image = w.Build();

    // Verify superblock
    var sb = FileSystem.Ocfs2.Ocfs2Superblock.TryParse(image);
    Assert.That(sb.Valid, Is.True);
    Assert.That(sb.Label, Is.EqualTo("OCFS2VOL"));
    Assert.That(sb.MaxSlots, Is.EqualTo(1));
    Assert.That(sb.RootBlkno, Is.EqualTo(5));

    // Verify via descriptor List
    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    using var ms = new MemoryStream(image);
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(payload.Length));

    // Verify extraction
    ms.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), "ocfs2_rt_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "test.txt")), Is.EqualTo(payload));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_ThreeFiles_RoundTrip() {
    var w = new FileSystem.Ocfs2.Ocfs2Writer();
    var file1 = "alpha content"u8.ToArray();
    var file2 = new byte[5000]; // > 1 cluster
    for (var i = 0; i < file2.Length; i++) file2[i] = (byte)(i & 0xFF);
    var file3 = "tiny"u8.ToArray();

    w.AddFile("alpha.txt", file1);
    w.AddFile("bigfile.bin", file2);
    w.AddFile("small.dat", file3);
    var image = w.Build();

    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    using var ms = new MemoryStream(image);
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(3));

    var byName = entries.ToDictionary(e => e.Name);
    Assert.That(byName.ContainsKey("alpha.txt"), Is.True);
    Assert.That(byName.ContainsKey("bigfile.bin"), Is.True);
    Assert.That(byName.ContainsKey("small.dat"), Is.True);

    // Verify extraction
    ms.Position = 0;
    var outDir = Path.Combine(Path.GetTempPath(), "ocfs2_3f_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "alpha.txt")), Is.EqualTo(file1));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "bigfile.bin")), Is.EqualTo(file2));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "small.dat")), Is.EqualTo(file3));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_EmptyFile_RoundTrips() {
    var w = new FileSystem.Ocfs2.Ocfs2Writer();
    w.AddFile("empty.txt", []);
    var image = w.Build();

    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    using var ms = new MemoryStream(image);
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(0));
  }

  // ── Descriptor Create round-trip ───────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp1 = Path.GetTempFileName();
    var tmp2 = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp1, "first file content"u8.ToArray());
      File.WriteAllBytes(tmp2, "second"u8.ToArray());

      var inputs = new List<ArchiveInputInfo> {
        new(tmp1, "readme.txt", false),
        new(tmp2, "data.bin", false),
      };

      var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
      using var ms = new MemoryStream();
      d.Create(ms, inputs, new FormatCreateOptions());
      ms.Position = 0;

      var entries = d.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(2));
      var names = entries.Select(e => e.Name).ToHashSet();
      Assert.That(names, Does.Contain("readme.txt"));
      Assert.That(names, Does.Contain("data.bin"));
    } finally {
      File.Delete(tmp1);
      File.Delete(tmp2);
    }
  }

  // ── Modify (Add/Remove) tests ─────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Modify_Add_AppendsFile() {
    var w = new FileSystem.Ocfs2.Ocfs2Writer();
    w.AddFile("original.txt", "original"u8.ToArray());
    var image = w.Build();

    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    using var ms = new MemoryStream(image);

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "added content"u8.ToArray());
      d.Add(ms, [new ArchiveInputInfo(tmp, "added.txt", false)]);
    } finally {
      File.Delete(tmp);
    }

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("original.txt"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("added.txt"));
  }

  [Test, Category("HappyPath")]
  public void Modify_Remove_RemovesFile() {
    var w = new FileSystem.Ocfs2.Ocfs2Writer();
    w.AddFile("keep.txt", "keep me"u8.ToArray());
    w.AddFile("delete.txt", "remove me"u8.ToArray());
    var image = w.Build();

    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    using var ms = new MemoryStream(image);
    d.Remove(ms, ["delete.txt"]);

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("keep.txt"));
    Assert.That(entries.Select(e => e.Name), Does.Not.Contain("delete.txt"));
  }

  // ── Extent map test ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void ExtentMap_ReturnsMetadataAndFileExtents() {
    var w = new FileSystem.Ocfs2.Ocfs2Writer();
    w.AddFile("a.txt", "some data"u8.ToArray());
    w.AddFile("b.txt", new byte[4096]);
    var image = w.Build();

    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    using var ms = new MemoryStream(image);
    var extents = d.EnumerateExtents(ms).ToList();

    Assert.That(extents.Where(e => e.Kind == DefragBlockKind.MetadataReserved), Is.Not.Empty,
      "Expected metadata-reserved regions");
    var fileExtents = extents.Where(e => e.Kind == DefragBlockKind.Used).ToList();
    Assert.That(fileExtents, Is.Not.Empty, "Expected file data regions");
  }

  // ── Defrag test ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Defragment_PreservesAllFiles() {
    var w = new FileSystem.Ocfs2.Ocfs2Writer();
    w.AddFile("one.txt", "first"u8.ToArray());
    w.AddFile("two.txt", "second"u8.ToArray());
    var image = w.Build();

    var d = new FileSystem.Ocfs2.Ocfs2FormatDescriptor();
    using var ms = new MemoryStream(image);
    d.Defragment(ms);

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("one.txt"));
    Assert.That(names, Does.Contain("two.txt"));
  }
}
