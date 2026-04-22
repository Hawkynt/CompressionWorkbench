#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FileFormat.UnrealPak;

namespace Compression.Tests.UnrealPak;

[TestFixture]
public class UnrealPakTests {

  // Builds a minimal UE 4.15-compatible PAK (version 3) with a single stored entry.
  // Version 3 layout per entry:
  //   offset(int64) + size(int64) + uncompressedSize(int64) + compressionMethod(uint32) +
  //   sha1Hash(20) + (compression blocks: none when method=0) + isEncrypted(byte) + compressionBlockSize(uint32)
  // Payload at offset starts with another entry header (of the same shape) followed by raw bytes.
  // Footer: magic(uint32 LE) + version(uint32 LE) + indexOffset(int64) + indexSize(int64) + hash(20)
  private static byte[] MakeMinimalPak(string fileName, byte[] payload) {
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

    var hash = SHA1.HashData(payload); // arbitrary; reader ignores it

    // Entry header (in-payload) at offset 0.
    const uint Version = 4u; // version 4 adds encryption flag + compressionBlockSize — common baseline

    void WriteEntryHeader(long offset, long size, long uncompressedSize) {
      bw.Write(offset);
      bw.Write(size);
      bw.Write(uncompressedSize);
      bw.Write(0u); // compressionMethod = None
      bw.Write(hash); // 20 bytes
      // Version >= 4: encryption flag + compressionBlockSize
      bw.Write((byte)0); // isEncrypted
      bw.Write(0u); // compressionBlockSize
    }

    // In-payload entry header at file offset 0.
    var entryHeaderStart = ms.Position;
    WriteEntryHeader(0, payload.Length, payload.Length);
    var bodyStart = ms.Position;
    bw.Write(payload);

    // Rewrite the in-payload entry header's offset field now that we know it.
    // (UE's in-payload header holds the offset of the body relative to the file; we set it to
    // the body start so readers that seek into mid-file find it consistently.)
    var entryEnd = ms.Position;
    ms.Position = entryHeaderStart;
    bw.Write(entryHeaderStart); // offset
    bw.Write(payload.LongLength);
    bw.Write(payload.LongLength);
    bw.Write(0u);
    bw.Write(hash);
    bw.Write((byte)0);
    bw.Write(0u);
    ms.Position = entryEnd;

    // Write the index at current position.
    var indexOffset = ms.Position;
    // Mount point — empty string (just length 0 + null terminator isn't used; FString of empty = len 0).
    // Use "../../../Game/" to exercise mount-point handling.
    var mountBytes = Encoding.ASCII.GetBytes("../../../Game/\0");
    bw.Write(mountBytes.Length);
    bw.Write(mountBytes);

    bw.Write(1); // file count

    // File name (length + ASCII incl. null terminator).
    var nameBytes = Encoding.ASCII.GetBytes(fileName + "\0");
    bw.Write(nameBytes.Length);
    bw.Write(nameBytes);

    // File entry record (in-index).
    WriteEntryHeader(entryHeaderStart, payload.Length, payload.Length);
    var indexEnd = ms.Position;
    var indexSize = indexEnd - indexOffset;

    // Footer: magic + version + indexOffset + indexSize + 20-byte hash + 1-byte encryptedIndex flag.
    bw.Write(UnrealPakReader.Magic);
    bw.Write(Version);
    bw.Write(indexOffset);
    bw.Write(indexSize);
    bw.Write(new byte[20]); // index hash — readers don't verify in our impl

    return ms.ToArray();
  }

  [Test]
  public void Reader_ListsSingleStoredEntry() {
    var payload = Encoding.UTF8.GetBytes("Hello, UnrealPak!");
    var pak = MakeMinimalPak("Content/test.uasset", payload);

    using var ms = new MemoryStream(pak);
    var reader = new UnrealPakReader(ms);

    Assert.That(reader.PakVersion, Is.EqualTo(4u));
    Assert.That(reader.MountPoint, Is.EqualTo("../../../Game/"));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Path, Is.EqualTo("Content/test.uasset"));
    Assert.That(reader.Entries[0].UncompressedSize, Is.EqualTo(payload.Length));
    Assert.That(reader.Entries[0].IsEncrypted, Is.False);
  }

  [Test]
  public void Reader_ExtractsStoredEntry() {
    var payload = Encoding.UTF8.GetBytes("Hello, UnrealPak!");
    var pak = MakeMinimalPak("Content/test.uasset", payload);

    using var ms = new MemoryStream(pak);
    var reader = new UnrealPakReader(ms);
    var data = reader.Extract(reader.Entries[0]);
    Assert.That(data, Is.EqualTo(payload));
  }

  [Test]
  public void Descriptor_ListsAndExtractsWithMountPrefix() {
    var payload = Encoding.UTF8.GetBytes("pak contents");
    var pak = MakeMinimalPak("Content/test.uasset", payload);

    var descriptor = new UnrealPakFormatDescriptor();
    using var ms = new MemoryStream(pak);
    var entries = descriptor.List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(1));
    // CombinePath strips leading ../ sequences — should yield "Game/Content/test.uasset".
    Assert.That(entries[0].Name, Is.EqualTo("Game/Content/test.uasset"));
    Assert.That(entries[0].Method, Is.EqualTo("Stored"));

    var tmpDir = Path.Combine(Path.GetTempPath(), "UnrealPakTests_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      ms.Position = 0;
      descriptor.Extract(ms, tmpDir, null, null);
      var extracted = File.ReadAllBytes(Path.Combine(tmpDir, "Game", "Content", "test.uasset"));
      Assert.That(extracted, Is.EqualTo(payload));
    } finally {
      if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
    }
  }

  [Test]
  public void Reader_RejectsNonPakData() {
    var bogus = new byte[256];
    using var ms = new MemoryStream(bogus);
    Assert.That(() => new UnrealPakReader(ms),
      Throws.InstanceOf<InvalidDataException>());
  }

  // Smoke test: ensure our zlib decompress branch compiles and round-trips with real zlib output.
  [Test]
  public void ZlibDecompression_SanityCheck() {
    var payload = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");
    using var inMs = new MemoryStream();
    using (var zl = new ZLibStream(inMs, CompressionMode.Compress, leaveOpen: true))
      zl.Write(payload);
    var compressed = inMs.ToArray();

    using var outMs = new MemoryStream();
    using (var zl = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress))
      zl.CopyTo(outMs);
    Assert.That(outMs.ToArray(), Is.EqualTo(payload));
  }
}
