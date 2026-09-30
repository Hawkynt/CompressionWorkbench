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

  // Reference streams written by lz4-java 1.8.0 `new LZ4BlockOutputStream(out)` (64 KiB blocks,
  // fast compressor, XXH32 seed 0x9747B28C) - the writer Minecraft uses for compression id 4. The
  // payload is `(byte)(i * 7 % 13 + 'a')` for i in [0, n).
  private const string Lz4JavaRaw13 =
    "TFo0QmxvY2sWDQAAAA0AAAC/fyACYWhiaWNqZGtlbGZtZ0xaNEJsb2NrFgAAAAAAAAAAAAAAAA==";
  private const string Lz4JavaCompressed70000 =
    "TFo0QmxvY2smFwEAAAAAAQDsARQA32FoYmljamRrZWxmbWcNAP//////////////////////////////////////////////" +
    "////////////////////////////////////////////////////////////////////////////////////////////////" +
    "////////////////////////////////////////////////////////////////////////////////////////////////" +
    "////////////////////////////////////////////////////////////////////////////////////////////////" +
    "///////bUG1nYWhiTFo0QmxvY2smKAAAAHARAAAu3TMG32ljamRrZWxmbWdhaGINAP//////////////////////XFBpY2pk" +
    "a0xaNEJsb2NrFgAAAAAAAAAAAAAAAA==";

  private static byte[] ReferencePayload(int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)(i * 7 % 13 + 'a');
    return data;
  }

  [Test]
  public void Lz4Decompress_GivenLz4JavaRawBlock_WhenDecoded_ThenPayloadAndMaskedChecksumAccepted() {
    var decoded = McaLz4BlockStream.Decompress(Convert.FromBase64String(Lz4JavaRaw13));
    Assert.That(decoded, Is.EqualTo(ReferencePayload(13)));
  }

  [Test]
  public void Lz4Decompress_GivenLz4JavaTwoCompressedBlocks_WhenDecoded_ThenPayloadMatches() {
    var stream = Convert.FromBase64String(Lz4JavaCompressed70000);
    Assert.That(stream[8], Is.EqualTo(0x26), "the reference stream holds compressed 64 KiB blocks");
    Assert.That(McaLz4BlockStream.Decompress(stream), Is.EqualTo(ReferencePayload(70000)));
  }

  [Test]
  public void Lz4Compress_GivenIncompressibleBlock_WhenEncoded_ThenBytesMatchLz4Java() {
    // A raw block has no encoder freedom, so the whole stream must be byte-identical.
    var encoded = McaLz4BlockStream.Compress(ReferencePayload(13), Compression.Core.Dictionary.Lz4.Lz4CompressionLevel.Fast);
    Assert.That(encoded, Is.EqualTo(Convert.FromBase64String(Lz4JavaRaw13)));
  }

  [TestCase(1)]
  [TestCase(65535)]
  [TestCase(65536)]
  [TestCase(65537)]
  [TestCase(200000)]
  public void Lz4Compress_GivenAnyLength_WhenEncoded_ThenEveryBlockChecksumFitsTwentyEightBits(int length) {
    var encoded = McaLz4BlockStream.Compress(ReferencePayload(length), Compression.Core.Dictionary.Lz4.Lz4CompressionLevel.Fast);
    var offset = 0;
    while (true) {
      var compressedLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(offset + 9));
      var checksum = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(offset + 17));
      Assert.That(checksum & 0xF0000000u, Is.Zero, $"block at {offset}");
      if (compressedLength == 0) break;
      offset += 21 + compressedLength;
    }
    Assert.That(McaLz4BlockStream.Decompress(encoded), Is.EqualTo(ReferencePayload(length)));
  }

  [Test]
  public void Lz4Decompress_GivenTrailingBytesAfterEndMarker_WhenDecoded_ThenRejected() {
    var stream = Convert.FromBase64String(Lz4JavaRaw13).Concat(new byte[] { 0 }).ToArray();
    Assert.Throws<InvalidDataException>(() => McaLz4BlockStream.Decompress(stream));
  }

  [Test]
  public void Lz4Decompress_GivenTruncatedStream_WhenDecoded_ThenRejected() {
    var stream = Convert.FromBase64String(Lz4JavaRaw13);
    Assert.Throws<InvalidDataException>(() => McaLz4BlockStream.Decompress(stream.AsSpan(0, stream.Length - 1)));
  }

  [TestCase("chunk_32_0.nbt")]
  [TestCase("chunk_0_32.nbt")]
  [TestCase("chunk_-1_0.nbt")]
  [TestCase("PROBE.TXT")]
  [TestCase("chunk_0_0.dat")]
  public void Writer_GivenNameOutsideChunkGrid_WhenCreating_ThenRefusesAsArgument(string name) {
    using var output = new MemoryStream();
    Assert.Throws<ArgumentException>(() => new McaFormatDescriptor().Create(output,
      [ArchiveInputInfo.InMemory(name, [0])], new FormatCreateOptions("zlib")));
  }

  [Test]
  public void Writer_GivenGridCorners_WhenCreating_ThenAllFourRoundTrip() {
    string[] names = ["chunk_0_0.nbt", "chunk_31_0.nbt", "chunk_0_31.nbt", "chunk_31_31.nbt"];
    using var output = new MemoryStream();
    new McaFormatDescriptor().Create(output,
      names.Select((n, i) => ArchiveInputInfo.InMemory(n, [(byte)i, 0x0A])).ToList(), new FormatCreateOptions("zlib"));
    output.Position = 0;
    var entries = new McaFormatDescriptor().List(output, null);
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(names));
    for (var i = 0; i < names.Length; ++i) {
      output.Position = 0;
      Assert.That(new McaFormatDescriptor().ExtractEntryToMemory(output, names[i], null), Is.EqualTo(new byte[] { (byte)i, 0x0A }));
    }
  }

  [Test]
  public void Writer_GivenDuplicateCoordinate_WhenCreating_ThenRefusesAsArgument() {
    using var output = new MemoryStream();
    Assert.Throws<ArgumentException>(() => new McaFormatDescriptor().Create(output,
      [ArchiveInputInfo.InMemory("chunk_1_1.nbt", [0]), ArchiveInputInfo.InMemory("CHUNK_1_1.NBT", [1])],
      new FormatCreateOptions("zlib")));
  }

  [Test]
  public void List_GivenRegionShorterThanHeader_WhenListed_ThenReportsPartialInsteadOfThrowing() {
    using var input = new MemoryStream(new byte[100]);
    var entries = new McaFormatDescriptor().List(input, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "metadata.ini" }));
    input.Position = 0;
    var ini = System.Text.Encoding.UTF8.GetString(new McaFormatDescriptor().ExtractEntryToMemory(input, "metadata.ini", null));
    Assert.That(ini, Does.Contain("parse_status=partial"));
  }

  [Test]
  public void List_GivenOneLocationPointingPastEnd_WhenListed_ThenKeepsIntactChunksAndStrictReaderRejects() {
    var data = MakeMinimalMca();
    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (50u << 8) | 1u); // chunk (1,0) beyond EOF
    using var input = new MemoryStream(data);
    var entries = new McaFormatDescriptor().List(input, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "chunk_0_0.nbt", "metadata.ini" }));
    Assert.Throws<InvalidDataException>(() => _ = new McaReader(data));
  }

  [Test]
  public void Reader_GivenUnpaddedFinalSector_WhenParsed_ThenChunkStillReadable() {
    var data = MakeMinimalMca();
    var chunkLength = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(8192));
    var trimmed = data.AsSpan(0, 8192 + 4 + chunkLength).ToArray();
    var reader = new McaReader(trimmed);
    Assert.That(reader.ExtractChunkNbt(reader.Chunks[0]), Is.EqualTo(new byte[] { 0x00 }));
  }

  [Test]
  public void Reader_GivenChunkLengthBeyondFile_WhenParsedStrictly_ThenRejected() {
    var data = MakeMinimalMca();
    BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8192), 4092);
    var trimmed = data.AsSpan(0, 8192 + 100).ToArray();
    Assert.Throws<InvalidDataException>(() => _ = new McaReader(trimmed));
  }
}
