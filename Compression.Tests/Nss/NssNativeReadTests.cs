using System.Buffers.Binary;
using System.Text;
using FileSystem.Nss;

namespace Compression.Tests.Nss;

[TestFixture]
public sealed class NssNativeReadTests {
  private const int BlockSize = 4096;

  [Test, Category("HappyPath")]
  public void Reader_ReconstructsNestedDirectoryAndExtractsNativePayload() {
    var payload = Enumerable.Range(0, 6000).Select(i => (byte)(i * 17 + 3)).ToArray();
    var image = BuildNativeImage(payload);

    using var stream = new MemoryStream(image, writable: false);
    var reader = new NssReader(stream);

    var native = reader.NativeEntries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
    Assert.Multiple(() => {
      Assert.That(native.Keys, Does.Contain("docs"));
      Assert.That(native.Keys, Does.Contain("docs/note.bin"));
      Assert.That(native["docs"].IsDirectory, Is.True);
      Assert.That(native["docs/note.bin"].IsDirectory, Is.False);
      Assert.That(native["docs/note.bin"].Size, Is.EqualTo(payload.Length));
    });

    Assert.That(reader.ExtractNative(native["docs/note.bin"]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListsAndExtractsNativeNestedFile() {
    var payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("native-nss-payload-", 300)));
    var image = BuildNativeImage(payload);
    var descriptor = new NssFormatDescriptor();

    using var listStream = new MemoryStream(image, writable: false);
    var listed = descriptor.List(listStream, null);
    Assert.That(listed.Select(entry => entry.Name), Does.Contain("docs/note.bin"));

    var output = Path.Combine(Path.GetTempPath(), "nss_native_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(output);
    try {
      using var extractStream = new MemoryStream(image, writable: false);
      descriptor.Extract(extractStream, output, null, ["docs/note.bin"]);
      var extracted = Path.Combine(output, "docs", "note.bin");
      Assert.That(File.Exists(extracted), Is.True);
      Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(payload));
    } finally {
      try { Directory.Delete(output, recursive: true); } catch { /* best effort */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsMismatchedDirectoryAndObjectNames() {
    var image = BuildNativeImage([1, 2, 3, 4]);
    var record = 5 * BlockSize + 0x28;
    Encoding.Unicode.GetBytes("evil.bin").CopyTo(image.AsSpan(record + 142));

    using var stream = new MemoryStream(image, writable: false);
    var reader = new NssReader(stream);
    Assert.That(reader.NativeEntries.Select(entry => entry.Name), Does.Not.Contain("docs/note.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsCyclicParentRelationships() {
    var image = BuildNativeImage([1, 2, 3, 4]);
    var docsEntry = 4 * BlockSize + 48;
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(docsEntry + 24, 8), 0x101);

    using var stream = new MemoryStream(image, writable: false);
    var reader = new NssReader(stream);

    Assert.That(reader.NativeEntries, Is.Empty);
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsExtentOutsideImage() {
    var image = BuildNativeImage([1, 2, 3, 4]);
    var record = 5 * BlockSize + 0x28;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(record + 92, 4), uint.MaxValue);

    using var stream = new MemoryStream(image, writable: false);
    var reader = new NssReader(stream);

    Assert.That(reader.NativeEntries.Select(entry => entry.Name), Does.Not.Contain("docs/note.bin"));
  }

  private static byte[] BuildNativeImage(byte[] payload) {
    const int blocks = 32;
    const int dirBlock = 4;
    const int leafBlock = 5;
    const int dataBlock = 20;
    var image = new byte[blocks * BlockSize];

    Encoding.ASCII.GetBytes("NSS Pool").CopyTo(image.AsSpan(0, 8));

    var dir = image.AsSpan(dirBlock * BlockSize, BlockSize);
    Encoding.ASCII.GetBytes("DirH").CopyTo(dir);
    BinaryPrimitives.WriteUInt16LittleEndian(dir.Slice(6, 2), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(dir.Slice(8, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(dir.Slice(10, 2), 0xC000);
    WriteDirEntry(dir, relative: 0, zid: 0x100, parent: 0x7f, name: "docs");
    WriteDirEntry(dir, relative: 64, zid: 0x101, parent: 0x100, name: "note.bin");
    BinaryPrimitives.WriteUInt32LittleEndian(dir.Slice(BlockSize - 4, 4), 0);
    BinaryPrimitives.WriteUInt32LittleEndian(dir.Slice(BlockSize - 8, 4), 64);

    var leaf = image.AsSpan(leafBlock * BlockSize, BlockSize);
    Encoding.ASCII.GetBytes("LEAF").CopyTo(leaf);
    BinaryPrimitives.WriteUInt16LittleEndian(leaf.Slice(4, 2), 0x0003);
    BinaryPrimitives.WriteUInt16LittleEndian(leaf.Slice(8, 2), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(leaf.Slice(10, 2), 0xC000);
    var record = leaf.Slice(0x28, 272);
    BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(2, 2), 0x444E);
    BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(8, 8), 0x101);
    BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(24, 8), checked((ulong)payload.Length));
    var payloadBlocks = checked((uint)((payload.Length + BlockSize - 1) / BlockSize));
    BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(88, 4), payloadBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(92, 4), dataBlock - 8);
    BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(140, 2), 8);
    Encoding.Unicode.GetBytes("note.bin").CopyTo(record.Slice(142));
    BinaryPrimitives.WriteUInt16LittleEndian(leaf.Slice(BlockSize - 2, 2), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(leaf.Slice(BlockSize - 4, 2), ushort.MaxValue);

    payload.CopyTo(image.AsSpan(dataBlock * BlockSize));
    return image;
  }

  private static void WriteDirEntry(
      Span<byte> block,
      uint relative,
      ulong zid,
      ulong parent,
      string name) {
    var entry = block.Slice(checked(48 + (int)relative), 64);
    BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(8, 8), zid);
    BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(24, 8), parent);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.Slice(42, 2), checked((ushort)name.Length));
    Encoding.Unicode.GetBytes(name).CopyTo(entry.Slice(44));
  }
}
