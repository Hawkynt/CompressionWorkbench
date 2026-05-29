using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Bfs;

[TestFixture]
public class BfsTests {

  /// <summary>Build a minimal BFS image: name + magic1 at sector 1 (offset 512) + plausible sizes.</summary>
  private static byte[] BuildMinimal(int superblockOffset = 512) {
    var image = new byte[superblockOffset + 2048];
    // name at offset 0
    Encoding.ASCII.GetBytes("testvol").CopyTo(image.AsSpan(superblockOffset));
    // magic1 '1SFB' at offset 32
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 32, 4), 0x42465331u);
    // fs_byte_order at 36
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 36, 4), 0x42494745u);
    // block_size at 40 = 2048
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 40, 4), 2048);
    // block_shift at 44 = 11
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 44, 4), 11);
    // num_blocks at 48 = 128
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(superblockOffset + 48, 8), 128);
    // used_blocks at 56 = 10
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(superblockOffset + 56, 8), 10);
    // inode_size at 64 = 1024
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 64, 4), 1024);
    // magic2 at 68
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 68, 4), 0xDD121031u);
    // blocks_per_ag at 72
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 72, 4), 128);
    // num_ags at 80
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 80, 4), 1);
    // magic3 at 112
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(superblockOffset + 112, 4), 0x15B6830Eu);
    // root_dir_ino at 116
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(superblockOffset + 116, 8), 1);
    // indices_dir_ino at 124
    BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(superblockOffset + 124, 8), 2);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Bfs"));
    Assert.That(d.Extensions, Does.Contain(".bfs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(d.MagicSignatures[0].Confidence, Is.LessThanOrEqualTo(0.35));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface_AtOffset512() {
    var img = BuildMinimal(512);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    var entries = d.List(ms, null);
    // Fallback path: hand-built minimal image doesn't have proper BFS structures
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.bfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("superblock.bin"));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface_AtOffset0() {
    var img = BuildMinimal(0);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.bfs"));
    Assert.That(names, Does.Contain("superblock.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesFiles() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "bfs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);

      Assert.That(File.Exists(Path.Combine(outDir, "FULL.bfs")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "superblock.bin")), Is.True);

      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("block_size=2048"));
      Assert.That(meta, Does.Contain("num_blocks=128"));
      Assert.That(meta, Does.Contain("magic1_ok=True"));
      Assert.That(meta, Does.Contain("magic3_ok=True"));

      var sb = File.ReadAllBytes(Path.Combine(outDir, "superblock.bin"));
      Assert.That(sb.Length, Is.EqualTo(1024));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_NoMagic_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[2048]);
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.bfs"));
  }

  // ── Capability checks (replaces Descriptor_IsHonestlyReadOnly) ─────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsCreatable() {
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsModifiable() {
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsDefragmentable() {
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsExtentMap() {
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_SupportsListExtractTest() {
    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
  }

  // ── Writer round-trip tests ────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleFile_RoundTrips() {
    var payload = "Hello BFS!"u8.ToArray();
    var w = new FileSystem.Bfs.BfsWriter();
    w.AddFile("test.txt", payload);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Bfs.BfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("test.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(payload.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_ThreeFiles_RoundTrip() {
    var w = new FileSystem.Bfs.BfsWriter();
    var file1 = "alpha content"u8.ToArray();
    var file2 = new byte[2000]; // spans 2 blocks
    for (var i = 0; i < file2.Length; i++) file2[i] = (byte)(i & 0xFF);
    var file3 = "tiny"u8.ToArray();

    w.AddFile("alpha.txt", file1);
    w.AddFile("bigfile.bin", file2);
    w.AddFile("small.dat", file3);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Bfs.BfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));

    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(byName.ContainsKey("alpha.txt"), Is.True);
    Assert.That(byName.ContainsKey("bigfile.bin"), Is.True);
    Assert.That(byName.ContainsKey("small.dat"), Is.True);

    Assert.That(r.Extract(byName["alpha.txt"]), Is.EqualTo(file1));
    Assert.That(r.Extract(byName["bigfile.bin"]), Is.EqualTo(file2));
    Assert.That(r.Extract(byName["small.dat"]), Is.EqualTo(file3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_EmptyFile_RoundTrips() {
    var w = new FileSystem.Bfs.BfsWriter();
    w.AddFile("empty.txt", []);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Bfs.BfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Size, Is.EqualTo(0));
    Assert.That(r.Extract(r.Entries[0]), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Writer_SuperblockHasValidMagics() {
    var w = new FileSystem.Bfs.BfsWriter();
    w.AddFile("x.txt", "data"u8.ToArray());
    var image = w.Build();

    // Superblock at offset 0
    var magic1 = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(32));
    Assert.That(magic1, Is.EqualTo(0x42465331u), "BFS1 magic");
    var magic2 = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(68));
    Assert.That(magic2, Is.EqualTo(0xDD121031u), "magic2");
    var magic3 = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(112));
    Assert.That(magic3, Is.EqualTo(0x15B6830Eu), "magic3");
  }

  [Test, Category("HappyPath")]
  public void Writer_SuperblockParsedByExistingReader() {
    var w = new FileSystem.Bfs.BfsWriter();
    w.AddFile("verify.txt", "test data"u8.ToArray());
    var image = w.Build();

    var sb = FileSystem.Bfs.BfsSuperblock.TryParse(image);
    Assert.That(sb.Valid, Is.True);
    Assert.That(sb.BlockSize, Is.EqualTo(1024));
    Assert.That(sb.Name, Is.EqualTo("BFS Volume"));
    Assert.That(sb.NumAgs, Is.EqualTo(1));
    Assert.That(sb.Magic1Value, Is.EqualTo(0x42465331u));
    Assert.That(sb.Magic2Value, Is.EqualTo(0xDD121031u));
    Assert.That(sb.Magic3Value, Is.EqualTo(0x15B6830Eu));
  }

  // ── Descriptor Create round-trip ───────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp1 = Path.GetTempFileName();
    var tmp2 = Path.GetTempFileName();
    var tmp3 = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp1, "first file content"u8.ToArray());
      File.WriteAllBytes(tmp2, new byte[512]);
      File.WriteAllBytes(tmp3, "third"u8.ToArray());

      var inputs = new List<ArchiveInputInfo> {
        new(tmp1, "readme.txt", false),
        new(tmp2, "data.bin", false),
        new(tmp3, "note.txt", false),
      };

      var d = new FileSystem.Bfs.BfsFormatDescriptor();
      using var ms = new MemoryStream();
      d.Create(ms, inputs, new FormatCreateOptions());
      ms.Position = 0;

      var entries = d.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(3));
      var names = entries.Select(e => e.Name).ToHashSet();
      Assert.That(names, Does.Contain("readme.txt"));
      Assert.That(names, Does.Contain("data.bin"));
      Assert.That(names, Does.Contain("note.txt"));

      // Verify extraction
      ms.Position = 0;
      var outDir = Path.Combine(Path.GetTempPath(), "bfs_create_" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(outDir);
      try {
        d.Extract(ms, outDir, null, null);
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, "readme.txt")),
          Is.EqualTo("first file content"u8.ToArray()));
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, "data.bin")),
          Is.EqualTo(new byte[512]));
        Assert.That(File.ReadAllBytes(Path.Combine(outDir, "note.txt")),
          Is.EqualTo("third"u8.ToArray()));
      } finally {
        try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
      }
    } finally {
      File.Delete(tmp1);
      File.Delete(tmp2);
      File.Delete(tmp3);
    }
  }

  // ── Modify (Add/Remove) tests ─────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Modify_Add_AppendsFile() {
    var w = new FileSystem.Bfs.BfsWriter();
    w.AddFile("original.txt", "original"u8.ToArray());
    var image = w.Build();

    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    using var ms = new MemoryStream(image);
    ms.SetLength(image.Length); // writable

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
    var w = new FileSystem.Bfs.BfsWriter();
    w.AddFile("keep.txt", "keep me"u8.ToArray());
    w.AddFile("delete.txt", "remove me"u8.ToArray());
    var image = w.Build();

    var d = new FileSystem.Bfs.BfsFormatDescriptor();
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
    var w = new FileSystem.Bfs.BfsWriter();
    w.AddFile("a.txt", "some data"u8.ToArray());
    w.AddFile("b.txt", new byte[2048]);
    var image = w.Build();

    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    using var ms = new MemoryStream(image);
    var extents = d.EnumerateExtents(ms).ToList();

    // Should have metadata regions
    Assert.That(extents.Where(e => e.Kind == DefragBlockKind.MetadataReserved), Is.Not.Empty,
      "Expected metadata-reserved regions (superblock, log, bitmap, inodes)");

    // Should have file data regions
    var fileExtents = extents.Where(e => e.Kind == DefragBlockKind.Used).ToList();
    Assert.That(fileExtents, Is.Not.Empty, "Expected file data regions");
    Assert.That(fileExtents.Select(e => e.FileName), Does.Contain("a.txt"));
    Assert.That(fileExtents.Select(e => e.FileName), Does.Contain("b.txt"));
  }

  // ── Defrag test ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Defragment_PreservesAllFiles() {
    var w = new FileSystem.Bfs.BfsWriter();
    w.AddFile("one.txt", "first"u8.ToArray());
    w.AddFile("two.txt", "second"u8.ToArray());
    var image = w.Build();

    var d = new FileSystem.Bfs.BfsFormatDescriptor();
    using var ms = new MemoryStream(image);
    d.Defragment(ms);

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("one.txt"));
    Assert.That(names, Does.Contain("two.txt"));
  }

  // ── Writer edge cases ──────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Writer_NoFiles_ProducesEmptyImage() {
    var w = new FileSystem.Bfs.BfsWriter();
    var image = w.Build();

    // Should still have a valid superblock
    var sb = FileSystem.Bfs.BfsSuperblock.TryParse(image);
    Assert.That(sb.Valid, Is.True);
    Assert.That(sb.BlockSize, Is.EqualTo(1024));

    using var ms = new MemoryStream(image);
    var r = new FileSystem.Bfs.BfsReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Writer_ImageSize_Is4MBMinimum() {
    var w = new FileSystem.Bfs.BfsWriter();
    w.AddFile("tiny.txt", "x"u8.ToArray());
    var image = w.Build();
    Assert.That(image.Length, Is.GreaterThanOrEqualTo(4 * 1024 * 1024));
  }
}
