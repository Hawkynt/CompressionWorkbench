#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using FileFormat.Mca;

namespace Compression.Tests.Mca;

[TestFixture]
public class McaTests {

  // Build a minimal .mca file with one chunk at region (0,0) compressed with zlib.
  private static byte[] MakeMinimalMca() {
    // Chunk 0's data at sector 2 (byte offset 8192). One sector = 4096 bytes.
    using var ms = new MemoryStream();

    // 8 KiB header (all zero initially).
    ms.SetLength(8192);

    // Location entry for chunk 0 (position 0 in the table): sectorOffset=2, sectorCount=1.
    Span<byte> locBuf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(locBuf, (2u << 8) | 1u);
    ms.Position = 0;
    ms.Write(locBuf);

    // Jump to sector 2 (offset 8192). Write chunk header: 4-byte BE length, 1-byte compression.
    ms.Position = 8192;

    // Compress a simple NBT payload (just a TAG_End byte for testing — real NBT is a compound).
    byte[] nbtPayload = [0x00];  // TAG_End
    using var compressedMs = new MemoryStream();
    using (var zlib = new ZLibStream(compressedMs, CompressionMode.Compress, leaveOpen: true))
      zlib.Write(nbtPayload);
    var compressed = compressedMs.ToArray();

    Span<byte> chunkLen = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(chunkLen, compressed.Length + 1);
    ms.Write(chunkLen);
    ms.WriteByte(0x02);  // compression type 2 = zlib
    ms.Write(compressed);

    // Pad to sector boundary (4096).
    while (ms.Position % 4096 != 0) ms.WriteByte(0);
    return ms.ToArray();
  }

  [Test]
  public void ReaderFindsChunkAtOrigin() {
    var data = MakeMinimalMca();
    var reader = new McaReader(data);
    Assert.That(reader.Chunks, Has.Count.EqualTo(1));
    Assert.That(reader.Chunks[0].RegionX, Is.EqualTo(0));
    Assert.That(reader.Chunks[0].RegionZ, Is.EqualTo(0));
    Assert.That(reader.Chunks[0].CompressionType, Is.EqualTo(2));
  }

  [Test]
  public void ExtractChunkNbtDecompresses() {
    var data = MakeMinimalMca();
    var reader = new McaReader(data);
    var nbt = reader.ExtractChunkNbt(reader.Chunks[0]);
    Assert.That(nbt, Is.EqualTo(new byte[] { 0x00 }));
  }

  [Test]
  public void DescriptorListNamesChunkByCoordinate() {
    var data = MakeMinimalMca();
    using var ms = new MemoryStream(data);
    var entries = new McaFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("chunk_0_0.nbt"));
  }
}
