#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using FileFormat.Mca;
using Compression.Registry;

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

  [TestCase("gzip", 1)]
  [TestCase("zlib", 2)]
  [TestCase("stored", 3)]
  [TestCase("lz4", 4)]
  public void WriterRoundTripsAllCompressionMethodsAndTimestamp(string method, byte compressionType) {
    byte[] payload = [0x0A, 0x00, 0x00, 0x00];
    using var output = new MemoryStream();
    var options = new FormatCreateOptions(method) { Level = 8 };
    options.FormatSpecific["Timestamp.3.7"] = "123456789";
    new McaFormatDescriptor().Create(output, [ArchiveInputInfo.InMemory("chunk_3_7.nbt", payload)], options);

    var reader = new McaReader(output.ToArray());
    Assert.That(reader.Chunks, Has.Count.EqualTo(1));
    Assert.That(reader.Chunks[0].CompressionType, Is.EqualTo(compressionType));
    Assert.That(reader.Chunks[0].Timestamp, Is.EqualTo(123456789));
    Assert.That(reader.ExtractChunkNbt(reader.Chunks[0]), Is.EqualTo(payload));
    if (method == "lz4")
      Assert.That(output.ToArray().AsSpan(8197, 8).ToArray(), Is.EqualTo("LZ4Block"u8.ToArray()));
    Assert.That(output.Length % 4096, Is.Zero);
  }

  [Test]
  public void Lz4UsesLz4JavaBlockStreamEndMarker() {
    var bytes = McaLz4BlockStream.Compress(ReadOnlySpan<byte>.Empty, Compression.Core.Dictionary.Lz4.Lz4CompressionLevel.Fast);
    Assert.That(bytes, Is.EqualTo(new byte[] {
      0x4C, 0x5A, 0x34, 0x42, 0x6C, 0x6F, 0x63, 0x6B, 0x16,
      0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    }));
    Assert.That(McaLz4BlockStream.Decompress(bytes), Is.Empty);

    var repeated = Enumerable.Repeat((byte)0x5A, 4096).ToArray();
    var compressed = McaLz4BlockStream.Compress(repeated, Compression.Core.Dictionary.Lz4.Lz4CompressionLevel.Fast);
    Assert.That(compressed.AsSpan(0, 8).ToArray(), Is.EqualTo("LZ4Block"u8.ToArray()));
    Assert.That(compressed[8] & 0xF0, Is.EqualTo(0x20));
    Assert.That(McaLz4BlockStream.Decompress(compressed), Is.EqualTo(repeated));
    compressed[17] ^= 1;
    Assert.Throws<InvalidDataException>(() => McaLz4BlockStream.Decompress(compressed));
  }

  [Test]
  public void WriterRejectsInvalidChunkCoordinates() {
    using var output = new MemoryStream();
    Assert.Throws<InvalidDataException>(() => new McaFormatDescriptor().Create(output,
      [ArchiveInputInfo.InMemory("chunk_32_0.nbt", [0])], new FormatCreateOptions("zlib")));
  }
}
