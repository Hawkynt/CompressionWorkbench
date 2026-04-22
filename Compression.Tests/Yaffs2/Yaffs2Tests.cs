using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

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
}
