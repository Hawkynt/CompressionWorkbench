using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;

namespace Compression.Tests.Yaffs2;

[TestFixture]
public class Yaffs2Tests {
  private const int ChunkSize = 2048;
  private const int SpareSize = 64;
  private const int Stride = ChunkSize + SpareSize;

  private static void WriteObjectHeader(Span<byte> chunk, int type, int parentId, string name, long size) {
    chunk.Clear();
    BinaryPrimitives.WriteInt32LittleEndian(chunk.Slice(0, 4), type);
    BinaryPrimitives.WriteInt32LittleEndian(chunk.Slice(4, 4), parentId);
    // checksum u16 at offset 8 — leave zero
    var nameBytes = Encoding.UTF8.GetBytes(name);
    nameBytes.CopyTo(chunk.Slice(12, Math.Min(256, nameBytes.Length)));
    // file size at offset 296
    BinaryPrimitives.WriteInt32LittleEndian(chunk.Slice(296, 4), (int)size);
  }

  private static void WriteSpare(Span<byte> spare, int objId, int chunkId, uint nBytes) {
    spare.Clear();
    // seq_number u32 at 0
    BinaryPrimitives.WriteUInt32LittleEndian(spare.Slice(0, 4), 1);
    BinaryPrimitives.WriteInt32LittleEndian(spare.Slice(4, 4), objId);
    BinaryPrimitives.WriteInt32LittleEndian(spare.Slice(8, 4), chunkId);
    BinaryPrimitives.WriteUInt32LittleEndian(spare.Slice(12, 4), nBytes);
  }

  /// <summary>Minimal image: 1 directory header + 1 file header + 1 data chunk with payload.</summary>
  private static byte[] BuildMinimal(out byte[] fileData) {
    fileData = Encoding.UTF8.GetBytes("hello yaffs2");
    // 3 chunks total.
    var image = new byte[Stride * 3];

    // Chunk 0: directory header (type=3, parent=1, name="docs")
    WriteObjectHeader(image.AsSpan(0, ChunkSize), type: 3, parentId: 1, name: "docs", size: 0);
    WriteSpare(image.AsSpan(ChunkSize, SpareSize), objId: 100, chunkId: 0, nBytes: 0);

    // Chunk 1: file header (type=1, parent=100, name="hello.txt", size=fileData.Length)
    WriteObjectHeader(image.AsSpan(Stride, ChunkSize), type: 1, parentId: 100, name: "hello.txt", size: fileData.Length);
    WriteSpare(image.AsSpan(Stride + ChunkSize, SpareSize), objId: 101, chunkId: 0, nBytes: 0);

    // Chunk 2: data chunk for file 101.
    fileData.CopyTo(image.AsSpan(2 * Stride));
    WriteSpare(image.AsSpan(2 * Stride + ChunkSize, SpareSize), objId: 101, chunkId: 1, nBytes: (uint)fileData.Length);

    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Yaffs2"));
    Assert.That(d.Extensions, Does.Contain(".yaffs2"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal(out _);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.yaffs2"));
    Assert.That(names, Does.Contain("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesFilesAndReconstructsData() {
    var img = BuildMinimal(out var fileData);
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "yaffs2_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.yaffs2")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("chunk_size=2048"));
      Assert.That(meta, Does.Contain("spare_size=64"));

      // We expect a reconstructed file under files/.
      var filesDir = Path.Combine(outDir, "files");
      Assert.That(Directory.Exists(filesDir), Is.True);
      var extracted = Directory.GetFiles(filesDir, "*", SearchOption.AllDirectories);
      Assert.That(extracted, Is.Not.Empty);
      // Exactly one file, should match our payload.
      var payload = File.ReadAllBytes(extracted[0]);
      Assert.That(payload, Is.EqualTo(fileData));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_EmptyInput_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[0]);
    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.yaffs2"));
  }

  // ── Capability checks ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsCreatable() {
    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsModifiable() {
    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsDefragmentable() {
    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsExtentMap() {
    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
  }

  // ── Writer round-trip tests ────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleFile_RoundTrips() {
    var payload = "Hello YAFFS2!"u8.ToArray();
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    w.AddFile("test.txt", payload);
    var image = w.Build();

    // Verify via scanner
    var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(image);
    Assert.That(scan.ParseOk, Is.True);
    Assert.That(scan.ChunkSize, Is.EqualTo(ChunkSize));
    Assert.That(scan.SpareSize, Is.EqualTo(SpareSize));

    // Find the file object
    var fileObj = scan.Objects.FirstOrDefault(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.File && o.Name == "test.txt");
    Assert.That(fileObj, Is.Not.Null);
    Assert.That(scan.DataChunks.ContainsKey(fileObj!.ObjectId), Is.True);

    // Verify data round-trips
    var chunks = scan.DataChunks[fileObj.ObjectId];
    var data = chunks.SelectMany(c => image.Skip((int)c.Offset).Take(c.Length)).ToArray();
    Assert.That(data[..payload.Length], Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_ThreeFiles_RoundTrip() {
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    var file1 = "alpha content"u8.ToArray();
    var file2 = new byte[3000]; // spans 2 chunks
    for (var i = 0; i < file2.Length; i++) file2[i] = (byte)(i & 0xFF);
    var file3 = "tiny"u8.ToArray();

    w.AddFile("alpha.txt", file1);
    w.AddFile("bigfile.bin", file2);
    w.AddFile("small.dat", file3);
    var image = w.Build();

    var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(image);
    Assert.That(scan.ParseOk, Is.True);

    var fileNames = scan.Objects
      .Where(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.File)
      .Select(o => o.Name)
      .ToHashSet();
    Assert.That(fileNames, Does.Contain("alpha.txt"));
    Assert.That(fileNames, Does.Contain("bigfile.bin"));
    Assert.That(fileNames, Does.Contain("small.dat"));
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

      var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
      using var ms = new MemoryStream();
      d.Create(ms, inputs, new FormatCreateOptions());
      ms.Position = 0;

      // Verify via scanner that files exist
      var image = ms.ToArray();
      var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(image);
      Assert.That(scan.ParseOk, Is.True);
      var fileNames = scan.Objects
        .Where(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.File)
        .Select(o => o.Name)
        .ToHashSet();
      Assert.That(fileNames, Does.Contain("readme.txt"));
      Assert.That(fileNames, Does.Contain("data.bin"));
    } finally {
      File.Delete(tmp1);
      File.Delete(tmp2);
    }
  }

  // ── Modify (Add/Remove) tests ─────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Modify_Add_AppendsFile() {
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    w.AddFile("original.txt", "original"u8.ToArray());
    var image = w.Build();

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "added content"u8.ToArray());
      d.Add(ms, [new ArchiveInputInfo(tmp, "added.txt", false)]);
    } finally {
      File.Delete(tmp);
    }

    ms.Position = 0;
    var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(ms.ToArray());
    var fileNames = scan.Objects
      .Where(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.File)
      .Select(o => o.Name)
      .ToHashSet();
    Assert.That(fileNames, Does.Contain("original.txt"));
    Assert.That(fileNames, Does.Contain("added.txt"));
  }

  [Test, Category("HappyPath")]
  public void Modify_Remove_RemovesFile() {
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    w.AddFile("keep.txt", "keep me"u8.ToArray());
    w.AddFile("delete.txt", "remove me"u8.ToArray());
    var image = w.Build();

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    // Use an expandable MemoryStream — YAFFS2 in-place delete is log-structured
    // and appends a tombstone header at the tail (image grows by one stride).
    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;
    d.Remove(ms, ["delete.txt"]);

    ms.Position = 0;
    var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(ms.ToArray());
    var fileNames = scan.Objects
      .Where(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.File)
      .Select(o => o.Name)
      .ToHashSet();
    Assert.That(fileNames, Does.Contain("keep.txt"));
    Assert.That(fileNames, Does.Not.Contain("delete.txt"));
  }

  // ── Extent map test ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void ExtentMap_ReturnsChunkExtents() {
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    w.AddFile("a.txt", "some data"u8.ToArray());
    w.AddFile("b.txt", new byte[2048]);
    var image = w.Build();

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    using var ms = new MemoryStream(image);
    var extents = d.EnumerateExtents(ms).ToList();

    Assert.That(extents.Where(e => e.Kind == DefragBlockKind.MetadataReserved), Is.Not.Empty,
      "Expected metadata-reserved regions (object headers)");
    Assert.That(extents.Where(e => e.Kind == DefragBlockKind.Used), Is.Not.Empty,
      "Expected file data regions");
  }

  // ── Defrag test ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Defragment_PreservesAllFiles() {
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    w.AddFile("one.txt", "first"u8.ToArray());
    w.AddFile("two.txt", "second"u8.ToArray());
    var image = w.Build();

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    using var ms = new MemoryStream(image);
    d.Defragment(ms);

    ms.Position = 0;
    var scan = FileSystem.Yaffs2.Yaffs2Scanner.Scan(ms.ToArray());
    var fileNames = scan.Objects
      .Where(o => o.Type == FileSystem.Yaffs2.Yaffs2Scanner.YObjectType.File)
      .Select(o => o.Name)
      .ToHashSet();
    Assert.That(fileNames, Does.Contain("one.txt"));
    Assert.That(fileNames, Does.Contain("two.txt"));
  }

  // ── OpenEntry tests ───────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void OpenEntry_ReturnsBoundedStream_ReadPastSizeReturnsZero() {
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    var content = "Hello YAFFS2 OpenEntry"u8.ToArray();
    w.AddFile("hello.txt", content);
    var image = w.Build();

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    using var ms = new MemoryStream(image);
    using var s = d.OpenEntry(ms, "hello.txt", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>(), "OpenEntry must return BoundedEntryStream");
    Assert.That(s.Length, Is.EqualTo(content.Length));

    var buf = new byte[128];
    var n = s.Read(buf, 0, buf.Length);
    Assert.That(n, Is.EqualTo(content.Length));
    Assert.That(buf.AsSpan(0, n).ToArray(), Is.EqualTo(content));

    Assert.That(s.Read(buf, 0, buf.Length), Is.EqualTo(0), "read past LogicalSize returns 0 (EOF)");
  }

  [Test, Category("HappyPath")]
  public void OpenEntry_AcceptsFilesPrefix() {
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    var content = "prefix-test"u8.ToArray();
    w.AddFile("hello.txt", content);
    var image = w.Build();

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    using var ms = new MemoryStream(image);
    using var s = d.OpenEntry(ms, "files/hello.txt", null);
    Assert.That(s.Length, Is.EqualTo(content.Length));
    var buf = new byte[64];
    var n = s.Read(buf, 0, buf.Length);
    Assert.That(buf.AsSpan(0, n).ToArray(), Is.EqualTo(content));
  }

  [Test, Category("Sad")]
  public void OpenEntry_UnknownName_ReturnsEmptyBoundedStream() {
    var w = new FileSystem.Yaffs2.Yaffs2Writer();
    w.AddFile("real.txt", "x"u8.ToArray());
    var image = w.Build();

    var d = new FileSystem.Yaffs2.Yaffs2FormatDescriptor();
    using var ms = new MemoryStream(image);
    using var s = d.OpenEntry(ms, "does-not-exist", null);
    Assert.That(s, Is.InstanceOf<BoundedEntryStream>());
    Assert.That(s.Length, Is.EqualTo(0));
  }
}
