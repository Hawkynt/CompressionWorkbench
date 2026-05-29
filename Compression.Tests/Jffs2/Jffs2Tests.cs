using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.Jffs2;

[TestFixture]
public class Jffs2Tests {

  /// <summary>Build a minimal JFFS2 fixture: one cleanmarker + one inode + one dirent.</summary>
  private static byte[] BuildMinimal() {
    // Build each node into a list, then concatenate.
    var parts = new List<byte[]>();

    // Cleanmarker: 12 bytes.
    var cm = new byte[12];
    BinaryPrimitives.WriteUInt16LittleEndian(cm.AsSpan(0, 2), 0x1985);
    BinaryPrimitives.WriteUInt16LittleEndian(cm.AsSpan(2, 2), 0x2003);
    BinaryPrimitives.WriteUInt32LittleEndian(cm.AsSpan(4, 4), 12);
    parts.Add(cm);

    // Inode: 68 bytes fixed header is enough for our scanner.
    var ino = new byte[68];
    BinaryPrimitives.WriteUInt16LittleEndian(ino.AsSpan(0, 2), 0x1985);
    BinaryPrimitives.WriteUInt16LittleEndian(ino.AsSpan(2, 2), 0xE002);
    BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(4, 4), 68);
    BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(12, 4), 42); // ino
    BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(16, 4), 1);  // version
    BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(20, 4), 0x81A4); // mode 0644
    BinaryPrimitives.WriteUInt16LittleEndian(ino.AsSpan(24, 2), 1000); // uid
    BinaryPrimitives.WriteUInt16LittleEndian(ino.AsSpan(26, 2), 1001); // gid
    BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(28, 4), 12345); // isize
    BinaryPrimitives.WriteUInt32LittleEndian(ino.AsSpan(36, 4), 0x60000000); // mtime
    parts.Add(ino);

    // Dirent: header(40) + name.
    var nameBytes = Encoding.UTF8.GetBytes("README");
    var de = new byte[40 + nameBytes.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(de.AsSpan(0, 2), 0x1985);
    BinaryPrimitives.WriteUInt16LittleEndian(de.AsSpan(2, 2), 0xE001);
    BinaryPrimitives.WriteUInt32LittleEndian(de.AsSpan(4, 4), (uint)(40 + nameBytes.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(de.AsSpan(12, 4), 1);   // pino
    BinaryPrimitives.WriteUInt32LittleEndian(de.AsSpan(16, 4), 1);   // version
    BinaryPrimitives.WriteUInt32LittleEndian(de.AsSpan(20, 4), 42);  // ino
    de[28] = (byte)nameBytes.Length; // nsize
    de[29] = 1; // type (regular)
    nameBytes.CopyTo(de.AsSpan(40));
    // Align to 4 for the next node.
    var pad = (4 - (de.Length % 4)) % 4;
    if (pad > 0) de = [.. de, .. new byte[pad]];
    parts.Add(de);

    var totalLen = parts.Sum(p => p.Length);
    var img = new byte[totalLen];
    var pos = 0;
    foreach (var p in parts) {
      p.CopyTo(img, pos);
      pos += p.Length;
    }
    return img;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Jffs2"));
    Assert.That(d.Extensions, Does.Contain(".jffs2"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures[0].Confidence, Is.LessThanOrEqualTo(0.35));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.jffs2"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("dirents.txt"));
    Assert.That(names, Does.Contain("inodes.txt"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesFiles() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "jffs2_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);

      Assert.That(File.Exists(Path.Combine(outDir, "FULL.jffs2")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("dirent_count=1"));
      Assert.That(meta, Does.Contain("inode_count=1"));
      Assert.That(meta, Does.Contain("cleanmarker_count=1"));

      var dirents = File.ReadAllText(Path.Combine(outDir, "dirents.txt"));
      Assert.That(dirents, Does.Contain("README"));
      Assert.That(dirents, Does.Contain("\t42\t"));

      var inodes = File.ReadAllText(Path.Combine(outDir, "inodes.txt"));
      Assert.That(inodes, Does.Contain("42\t1\t1000\t1001"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyInput_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[8]);
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.jffs2"));
  }

  // ── Writer / Create tests ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Create_SingleFile_RoundTrips() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var content = Encoding.UTF8.GetBytes("Hello, JFFS2!");
    var tmpDir = Path.Combine(Path.GetTempPath(), "jffs2_create_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var filePath = Path.Combine(tmpDir, "hello.txt");
      File.WriteAllBytes(filePath, content);

      using var output = new MemoryStream();
      var inputs = new List<ArchiveInputInfo> {
        new(filePath, "hello.txt", false)
      };
      d.Create(output, inputs, new FormatCreateOptions());

      // Verify the created image has the JFFS2 magic
      var img = output.ToArray();
      Assert.That(img.Length, Is.GreaterThanOrEqualTo(128 * 1024), "Image should be at least one erase block");
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(0, 2)), Is.EqualTo(0x1985), "JFFS2 magic at offset 0");

      // Read back and verify
      using var readStream = new MemoryStream(img);
      var entries = d.List(readStream, null);
      var names = entries.Select(e => e.Name).ToList();
      Assert.That(names, Does.Contain("hello.txt"), "Listed entries should include our file");

      // Extract and verify content
      var outDir = Path.Combine(tmpDir, "extracted");
      Directory.CreateDirectory(outDir);
      readStream.Position = 0;
      d.Extract(readStream, outDir, null, ["hello.txt"]);
      var extracted = File.ReadAllBytes(Path.Combine(outDir, "hello.txt"));
      Assert.That(extracted, Is.EqualTo(content), "Round-tripped content must match");
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Create_MultipleFiles_RoundTrips() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "jffs2_multi_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var files = new Dictionary<string, byte[]> {
        ["alpha.txt"] = Encoding.UTF8.GetBytes("File Alpha"),
        ["beta.bin"] = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0xFD },
        ["gamma.dat"] = Encoding.UTF8.GetBytes("Gamma data with more content here"),
      };

      var inputs = new List<ArchiveInputInfo>();
      foreach (var (name, data) in files) {
        var path = Path.Combine(tmpDir, name);
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, name, false));
      }

      using var output = new MemoryStream();
      d.Create(output, inputs, new FormatCreateOptions());
      var img = output.ToArray();

      // List and verify all files appear
      using var readStream = new MemoryStream(img);
      var entries = d.List(readStream, null);
      var entryNames = entries.Select(e => e.Name).ToHashSet();
      foreach (var name in files.Keys)
        Assert.That(entryNames, Does.Contain(name), $"Missing entry: {name}");

      // Extract and verify each file's content
      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      readStream.Position = 0;
      d.Extract(readStream, outDir, null, null);

      foreach (var (name, expected) in files) {
        var extractedPath = Path.Combine(outDir, name);
        Assert.That(File.Exists(extractedPath), Is.True, $"{name} should exist");
        Assert.That(File.ReadAllBytes(extractedPath), Is.EqualTo(expected), $"{name} content mismatch");
      }
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Create_EmptyFile_RoundTrips() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "jffs2_empty_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var filePath = Path.Combine(tmpDir, "empty.txt");
      File.WriteAllBytes(filePath, []);

      using var output = new MemoryStream();
      d.Create(output, [new ArchiveInputInfo(filePath, "empty.txt", false)], new FormatCreateOptions());
      var img = output.ToArray();

      using var readStream = new MemoryStream(img);
      var entries = d.List(readStream, null);
      Assert.That(entries.Select(e => e.Name), Does.Contain("empty.txt"));

      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      readStream.Position = 0;
      d.Extract(readStream, outDir, null, ["empty.txt"]);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "empty.txt")), Is.EqualTo(Array.Empty<byte>()));
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Create_WriterImageHasCorrectNodeStructure() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "jffs2_struct_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var filePath = Path.Combine(tmpDir, "test.txt");
      File.WriteAllBytes(filePath, Encoding.UTF8.GetBytes("test data"));

      using var output = new MemoryStream();
      d.Create(output, [new ArchiveInputInfo(filePath, "test.txt", false)], new FormatCreateOptions());
      var img = output.ToArray();

      // Verify via scanner: should have cleanmarker + root dir inode + file inode + dirent
      using var readStream = new MemoryStream(img);
      var entries = d.List(readStream, null);
      var meta = entries.FirstOrDefault(e => e.Name == "metadata.ini");
      Assert.That(meta, Is.Not.Null);

      // Extract metadata to check node counts
      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      readStream.Position = 0;
      d.Extract(readStream, outDir, null, ["metadata.ini"]);
      var metaText = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(metaText, Does.Contain("cleanmarker_count=1"));
      Assert.That(metaText, Does.Contain("inode_count=2")); // root + file
      Assert.That(metaText, Does.Contain("dirent_count=1"));
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Create_ImageSizeIsEraseBlockAligned() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "jffs2_align_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var filePath = Path.Combine(tmpDir, "x.txt");
      File.WriteAllBytes(filePath, Encoding.UTF8.GetBytes("x"));

      using var output = new MemoryStream();
      d.Create(output, [new ArchiveInputInfo(filePath, "x.txt", false)], new FormatCreateOptions());
      var img = output.ToArray();
      Assert.That(img.Length % (128 * 1024), Is.EqualTo(0), "Image size should be a multiple of 128 KiB");
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Create_TrailingBytesAre0xFF() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "jffs2_ff_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var filePath = Path.Combine(tmpDir, "small.txt");
      File.WriteAllBytes(filePath, Encoding.UTF8.GetBytes("hi"));

      using var output = new MemoryStream();
      d.Create(output, [new ArchiveInputInfo(filePath, "small.txt", false)], new FormatCreateOptions());
      var img = output.ToArray();

      // The last 1024 bytes should be 0xFF (erased flash)
      var tail = img.AsSpan(img.Length - 1024);
      foreach (var b in tail)
        Assert.That(b, Is.EqualTo(0xFF), "Trailing bytes should be 0xFF (erased flash)");
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  // ── Modify (Add/Remove) tests ─────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Modify_Add_NewFile() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "jffs2_add_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      // Create initial image with one file
      var f1Path = Path.Combine(tmpDir, "first.txt");
      File.WriteAllBytes(f1Path, Encoding.UTF8.GetBytes("first"));
      using var image = new MemoryStream();
      d.Create(image, [new ArchiveInputInfo(f1Path, "first.txt", false)], new FormatCreateOptions());

      // Add a second file
      var f2Path = Path.Combine(tmpDir, "second.txt");
      File.WriteAllBytes(f2Path, Encoding.UTF8.GetBytes("second"));
      image.Position = 0;
      d.Add(image, [new ArchiveInputInfo(f2Path, "second.txt", false)]);

      // Verify both files exist
      image.Position = 0;
      var entries = d.List(image, null);
      var names = entries.Select(e => e.Name).ToHashSet();
      Assert.That(names, Does.Contain("first.txt"));
      Assert.That(names, Does.Contain("second.txt"));

      // Verify content
      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      image.Position = 0;
      d.Extract(image, outDir, null, ["first.txt", "second.txt"]);
      Assert.That(File.ReadAllText(Path.Combine(outDir, "first.txt")), Is.EqualTo("first"));
      Assert.That(File.ReadAllText(Path.Combine(outDir, "second.txt")), Is.EqualTo("second"));
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("HappyPath")]
  public void Modify_Remove_File() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "jffs2_rm_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      // Create image with two files
      var f1Path = Path.Combine(tmpDir, "keep.txt");
      var f2Path = Path.Combine(tmpDir, "remove.txt");
      File.WriteAllBytes(f1Path, Encoding.UTF8.GetBytes("kept"));
      File.WriteAllBytes(f2Path, Encoding.UTF8.GetBytes("removed"));
      using var image = new MemoryStream();
      d.Create(image, [
        new ArchiveInputInfo(f1Path, "keep.txt", false),
        new ArchiveInputInfo(f2Path, "remove.txt", false),
      ], new FormatCreateOptions());

      // Remove one file
      image.Position = 0;
      d.Remove(image, ["remove.txt"]);

      // Verify only keep.txt remains
      image.Position = 0;
      var entries = d.List(image, null);
      var names = entries.Select(e => e.Name).ToHashSet();
      Assert.That(names, Does.Contain("keep.txt"));
      Assert.That(names, Does.Not.Contain("remove.txt"));

      // Verify content
      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      image.Position = 0;
      d.Extract(image, outDir, null, ["keep.txt"]);
      Assert.That(File.ReadAllText(Path.Combine(outDir, "keep.txt")), Is.EqualTo("kept"));
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  // ── Defragment test ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Defragment_PreservesFiles() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "jffs2_defrag_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var f1Path = Path.Combine(tmpDir, "a.txt");
      var f2Path = Path.Combine(tmpDir, "b.txt");
      File.WriteAllBytes(f1Path, Encoding.UTF8.GetBytes("aaa"));
      File.WriteAllBytes(f2Path, Encoding.UTF8.GetBytes("bbb"));
      using var image = new MemoryStream();
      d.Create(image, [
        new ArchiveInputInfo(f1Path, "a.txt", false),
        new ArchiveInputInfo(f2Path, "b.txt", false),
      ], new FormatCreateOptions());

      image.Position = 0;
      d.Defragment(image);

      // Verify files still round-trip
      image.Position = 0;
      var entries = d.List(image, null);
      var names = entries.Select(e => e.Name).ToHashSet();
      Assert.That(names, Does.Contain("a.txt"));
      Assert.That(names, Does.Contain("b.txt"));

      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      image.Position = 0;
      d.Extract(image, outDir, null, ["a.txt", "b.txt"]);
      Assert.That(File.ReadAllText(Path.Combine(outDir, "a.txt")), Is.EqualTo("aaa"));
      Assert.That(File.ReadAllText(Path.Combine(outDir, "b.txt")), Is.EqualTo("bbb"));
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  // ── ExtentMap test ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void ExtentMap_EmitsExpectedTiles() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "jffs2_extent_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var filePath = Path.Combine(tmpDir, "test.txt");
      File.WriteAllBytes(filePath, Encoding.UTF8.GetBytes("extent map test"));

      using var output = new MemoryStream();
      d.Create(output, [new ArchiveInputInfo(filePath, "test.txt", false)], new FormatCreateOptions());

      output.Position = 0;
      var extents = ((IFilesystemExtentMap)d).EnumerateExtents(output).ToList();

      // Should have at least: cleanmarker, root inode, file inode, dirent, free space
      Assert.That(extents.Count, Is.GreaterThanOrEqualTo(4));

      // Verify we have a cleanmarker tile
      Assert.That(extents.Any(e => e.Kind == DefragBlockKind.MetadataReserved && e.FileName != null && e.FileName.Contains("cleanmarker")),
        Is.True, "Should have a cleanmarker tile");

      // Verify we have at least one Used (inode) tile
      Assert.That(extents.Any(e => e.Kind == DefragBlockKind.Used),
        Is.True, "Should have at least one Used tile for inode data");

      // Verify we have a Free tile (trailing 0xFF space)
      Assert.That(extents.Any(e => e.Kind == DefragBlockKind.Free),
        Is.True, "Should have trailing Free space");
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }

  // ── Capability flag test ──────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_HasCreateAndModifyCapabilities() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(d is IArchiveCreatable, Is.True);
    Assert.That(d is IArchiveModifiable, Is.True);
    Assert.That(d is IArchiveDefragmentable, Is.True);
    Assert.That(d is IFilesystemExtentMap, Is.True);
  }

  // ── Large file test ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Create_LargeFile_RoundTrips() {
    var d = new FileSystem.Jffs2.Jffs2FormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), "jffs2_large_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      // Create a file larger than a single node would typically hold
      var data = new byte[4096];
      for (var i = 0; i < data.Length; i++)
        data[i] = (byte)(i & 0xFF);

      var filePath = Path.Combine(tmpDir, "large.bin");
      File.WriteAllBytes(filePath, data);

      using var output = new MemoryStream();
      d.Create(output, [new ArchiveInputInfo(filePath, "large.bin", false)], new FormatCreateOptions());

      var outDir = Path.Combine(tmpDir, "out");
      Directory.CreateDirectory(outDir);
      output.Position = 0;
      d.Extract(output, outDir, null, ["large.bin"]);
      var extracted = File.ReadAllBytes(Path.Combine(outDir, "large.bin"));
      Assert.That(extracted, Is.EqualTo(data));
    } finally {
      try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
    }
  }
}
