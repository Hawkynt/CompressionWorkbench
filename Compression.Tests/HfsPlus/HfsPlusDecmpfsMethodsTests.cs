using System.Buffers.Binary;
using System.IO.Compression;
using Compression.Registry;
using FileFormat.Lzfse;
using FileSystem.HfsPlus;
using LzbitmapCodec = Compression.Core.Dictionary.Lzbitmap.Lzbitmap;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Every decmpfs method, read and written: 1 (uncompressed in the attribute), 3/4 zlib, 5
/// (zeros, as libfshfs reads it), 7/8 LZVN, 9/10 raw, 11/12 LZFSE, 13/14 LZBITMAP, and the
/// dataless placeholders xnu defines. Writing goes through <see cref="HfsPlusWriter.TransparentCompression"/>
/// and is read back by our reader here; <see cref="HfsPlusLibfshfsOracleTests"/> holds the
/// independent reader's verdict.
/// </summary>
[TestFixture]
public sealed class HfsPlusDecmpfsMethodsTests {

  private const int Chunk = 65536;

  private static byte[] Attribute(uint method, long size, ReadOnlySpan<byte> payload = default) {
    var attribute = new byte[16 + payload.Length];
    "fpmc"u8.CopyTo(attribute);
    BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), method);
    BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(8), (ulong)size);
    payload.CopyTo(attribute.AsSpan(16));
    return attribute;
  }

  internal static byte[] Text(int length, int seed = 1) {
    var words = "transparent compression keeps the data fork empty and the bytes in an attribute "u8.ToArray();
    var random = new Random(seed);
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = random.Next(9) == 0 ? (byte)random.Next(256) : words[(i * 3 + i / 11) % words.Length];
    return data;
  }

  internal static byte[] Noise(int length, int seed = 2) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  // ── Reading the methods keramics' volume does not hold ─────────────

  [Test, Category("HappyPath")]
  public void GivenMethodOne_WhenDecoded_ThenTheBytesFollowTheHeader()
    => Assert.That(HfsPlusDecmpfs.Decode(Attribute(1, 5, "hello"u8), () => []), Is.EqualTo("hello"u8.ToArray()));

  [Test, Category("ExceptionalCase")]
  public void GivenMethodOneShorterThanItsSize_WhenDecoded_ThenItIsInvalid()
    => Assert.Throws<InvalidDataException>(() => HfsPlusDecmpfs.Decode(Attribute(1, 6, "hello"u8), () => []));

  [Test, Category("HappyPath")]
  public void GivenMethodFive_WhenDecoded_ThenItIsZerosOfTheRecordedSize()
    => Assert.That(HfsPlusDecmpfs.Decode(Attribute(5, 100, [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), () => []), Is.EqualTo(new byte[100]));

  [Test, Category("HappyPath")]
  public void GivenRawInline_WhenDecoded_ThenTheBytesFollowTheMarker()
    => Assert.That(HfsPlusDecmpfs.Decode(Attribute(9, 3, [0xCC, 7, 8, 9]), () => []), Is.EqualTo(new byte[] { 7, 8, 9 }));

  [Test, Category("ExceptionalCase")]
  public void GivenRawInlineWithoutItsMarker_WhenDecoded_ThenItIsInvalid()
    => Assert.Throws<InvalidDataException>(() => HfsPlusDecmpfs.Decode(Attribute(9, 3, [0xFF, 7, 8, 9]), () => []));

  [Test, Category("HappyPath")]
  public void GivenLzbitmapInline_WhenDecoded_ThenTheStreamIsTheContent() {
    var content = Text(2000);
    Assert.That(HfsPlusDecmpfs.Decode(Attribute(13, content.Length, LzbitmapCodec.Compress(content)), () => []), Is.EqualTo(content));
  }

  [TestCase(0xFF, TestName = "GivenALzbitmapChunkMarkedFF_WhenDecoded_ThenItIsStored")]
  [TestCase(0x0F, TestName = "GivenALzbitmapChunkWhoseLowNibbleIsF_WhenDecoded_ThenItIsStored")]
  [Category("BoundaryCase")]
  public void LzbitmapStored(int marker)
    => Assert.That(HfsPlusDecmpfs.Decode(Attribute(13, 2, [(byte)marker, 1, 2]), () => []), Is.EqualTo(new byte[] { 1, 2 }));

  [Test, Category("ExceptionalCase")]
  public void GivenALzbitmapChunkThatIsNeitherStreamNorStored_WhenDecoded_ThenItIsInvalid()
    => Assert.Throws<InvalidDataException>(() => HfsPlusDecmpfs.Decode(Attribute(13, 2, [0x00, 1, 2]), () => []));

  /// <summary>Method 10 laid out as linux-apfs-rw reads it: a zlib-style resource fork of 0xCC blocks.</summary>
  [Test, Category("HappyPath")]
  public void GivenRawChunksInAClassicResourceFork_WhenDecoded_ThenTheyAreTheContent() {
    var content = Text(Chunk + 10);
    byte[][] blocks = [[0xCC, .. content[..Chunk]], [0xCC, .. content[Chunk..]]];
    var table = 4 + 8 * blocks.Length;
    var body = new MemoryStream();
    var header = new byte[table];
    BinaryPrimitives.WriteInt32LittleEndian(header, blocks.Length);
    for (int i = 0, at = table; i < blocks.Length; at += blocks[i].Length, ++i) {
      BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4 + 8 * i), at);
      BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8 + 8 * i), blocks[i].Length);
    }
    body.Write(header);
    foreach (var block in blocks) body.Write(block);
    var fork = new byte[0x104 + body.Length];
    BinaryPrimitives.WriteInt32BigEndian(fork, 0x100);
    BinaryPrimitives.WriteInt32BigEndian(fork.AsSpan(0x100), (int)body.Length);
    body.ToArray().CopyTo(fork, 0x104);
    Assert.That(HfsPlusDecmpfs.Decode(Attribute(10, content.Length), () => fork), Is.EqualTo(content));
  }

  [TestCase(0x80000001u, TestName = "GivenADatalessFile_WhenSized_ThenItHasNoLocalBytes")]
  [TestCase(0x80000002u, TestName = "GivenADatalessPackage_WhenSized_ThenItHasNoLocalBytes")]
  [Category("BoundaryCase")]
  public void Dataless(uint method) {
    var attribute = Attribute(method, 123_456);
    Assert.Multiple(() => {
      Assert.That(HfsPlusDecmpfs.IsDataless(attribute), Is.True);
      Assert.That(HfsPlusDecmpfs.TryReadSize(attribute, out var size), Is.True);
      Assert.That(size, Is.Zero, "the recorded size is the provider's, not this volume's");
      Assert.That(HfsPlusDecmpfs.Decode(attribute, () => throw new AssertionException("no fork is read")), Is.Empty);
    });
  }

  // ── Writing: the encoder's shape ──────────────────────────────────

  [TestCase(HfsPlusCompression.Zlib, 3u, TestName = "GivenZlib_WhenASmallFileIsEncoded_ThenItIsMethodThreeInline")]
  [TestCase(HfsPlusCompression.Lzvn, 7u, TestName = "GivenLzvn_WhenASmallFileIsEncoded_ThenItIsMethodSevenInline")]
  [TestCase(HfsPlusCompression.Lzfse, 11u, TestName = "GivenLzfse_WhenASmallFileIsEncoded_ThenItIsMethodElevenInline")]
  [TestCase(HfsPlusCompression.Lzbitmap, 13u, TestName = "GivenLzbitmap_WhenASmallFileIsEncoded_ThenItIsMethodThirteenInline")]
  [TestCase(HfsPlusCompression.Raw, 9u, TestName = "GivenRaw_WhenASmallFileIsEncoded_ThenItIsMethodNineInline")]
  [TestCase(HfsPlusCompression.InlineUncompressed, 1u, TestName = "GivenInlineUncompressed_WhenASmallFileIsEncoded_ThenItIsMethodOne")]
  [Category("HappyPath")]
  public void SmallFileInline(HfsPlusCompression compression, uint method) {
    var content = Text(1500);
    var encoding = HfsPlusDecmpfs.Encode(content, compression)!;
    Assert.Multiple(() => {
      Assert.That(HfsPlusDecmpfs.Method(encoding.Attribute), Is.EqualTo(method));
      Assert.That(encoding.ResourceFork, Is.Null);
      Assert.That(encoding.Attribute.Length, Is.LessThanOrEqualTo(HfsPlusDecmpfs.MaxAttributeSize));
      Assert.That(HfsPlusDecmpfs.Decode(encoding.Attribute, () => []), Is.EqualTo(content));
    });
  }

  [TestCase(HfsPlusCompression.Zlib, 4u, TestName = "GivenZlib_WhenALargeFileIsEncoded_ThenItIsMethodFourInTheResourceFork")]
  [TestCase(HfsPlusCompression.Lzvn, 8u, TestName = "GivenLzvn_WhenALargeFileIsEncoded_ThenItIsMethodEightInTheResourceFork")]
  [TestCase(HfsPlusCompression.Lzfse, 12u, TestName = "GivenLzfse_WhenALargeFileIsEncoded_ThenItIsMethodTwelveInTheResourceFork")]
  [TestCase(HfsPlusCompression.Lzbitmap, 14u, TestName = "GivenLzbitmap_WhenALargeFileIsEncoded_ThenItIsMethodFourteenInTheResourceFork")]
  [TestCase(HfsPlusCompression.Raw, 10u, TestName = "GivenRaw_WhenALargeFileIsEncoded_ThenItIsMethodTenInTheResourceFork")]
  [Category("HappyPath")]
  public void LargeFileChunked(HfsPlusCompression compression, uint method) {
    var content = Text(3 * Chunk + 999);
    var encoding = HfsPlusDecmpfs.Encode(content, compression)!;
    Assert.Multiple(() => {
      Assert.That(HfsPlusDecmpfs.Method(encoding.Attribute), Is.EqualTo(method));
      Assert.That(encoding.Attribute, Has.Length.EqualTo(16), "the attribute is the bare header");
      Assert.That(encoding.ResourceFork, Is.Not.Null);
      Assert.That(HfsPlusDecmpfs.Decode(encoding.Attribute, () => encoding.ResourceFork!), Is.EqualTo(content));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenZlibChunks_WhenEncoded_ThenTheForkHasMacOSResourceLayout() {
    var fork = HfsPlusDecmpfs.Encode(Text(2 * Chunk), HfsPlusCompression.Zlib)!.ResourceFork!;
    var mapOffset = BinaryPrimitives.ReadInt32BigEndian(fork.AsSpan(4));
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadInt32BigEndian(fork), Is.EqualTo(0x100));
      Assert.That(BinaryPrimitives.ReadInt32BigEndian(fork.AsSpan(8)), Is.EqualTo(mapOffset - 0x100));
      Assert.That(BinaryPrimitives.ReadInt32BigEndian(fork.AsSpan(12)), Is.EqualTo(50));
      Assert.That(fork.Length, Is.EqualTo(mapOffset + 50));
      Assert.That(fork.AsSpan(16, 0x100 - 16).ToArray(), Is.All.Zero);
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(fork.AsSpan(0x104)), Is.EqualTo(2), "block count");
      // The 50-byte map macOS wrote for keramics' compressed2.
      Assert.That(Convert.ToHexStringLower(fork.AsSpan(mapOffset)),
        Is.EqualTo("000000000000000000000000000000000000000000000000001c00320000636d70660000000a0001ffff0000000000000000"));
    });
  }

  [TestCase(0, TestName = "GivenAnEmptyFile_WhenEncoded_ThenItStaysUncompressed")]
  [Category("BoundaryCase")]
  public void EmptyFile(int length)
    => Assert.That(HfsPlusDecmpfs.Encode(new byte[length], HfsPlusCompression.Lzfse), Is.Null);

  [TestCase(HfsPlusCompression.Zlib, TestName = "GivenIncompressibleDataBeyondTheAttribute_WhenZlibEncoded_ThenItStaysUncompressed")]
  [TestCase(HfsPlusCompression.Lzvn, TestName = "GivenIncompressibleDataBeyondTheAttribute_WhenLzvnEncoded_ThenItStaysUncompressed")]
  [TestCase(HfsPlusCompression.Lzfse, TestName = "GivenIncompressibleDataBeyondTheAttribute_WhenLzfseEncoded_ThenItStaysUncompressed")]
  [TestCase(HfsPlusCompression.Lzbitmap, TestName = "GivenIncompressibleDataBeyondTheAttribute_WhenLzbitmapEncoded_ThenItStaysUncompressed")]
  [Category("BoundaryCase")]
  public void IncompressibleLarge(HfsPlusCompression compression)
    => Assert.That(HfsPlusDecmpfs.Encode(Noise(Chunk + 5), compression), Is.Null);

  [Test, Category("BoundaryCase")]
  public void GivenIncompressibleDataThatFitsTheAttribute_WhenEncoded_ThenItIsInlineAsAStoredChunk() {
    // macOS stores keramics' 19-byte compressed1 the same way: 0xFF and the bytes, inline.
    var content = Noise(1000);
    var encoding = HfsPlusDecmpfs.Encode(content, HfsPlusCompression.Zlib)!;
    Assert.Multiple(() => {
      Assert.That(encoding.Attribute[16], Is.EqualTo(0xFF));
      Assert.That(encoding.Attribute, Has.Length.EqualTo(16 + 1 + content.Length));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenIncompressibleChunksUnderRaw_WhenEncoded_ThenTheyAreStoredBehindCC() {
    var content = Noise(Chunk + 5);
    var encoding = HfsPlusDecmpfs.Encode(content, HfsPlusCompression.Raw)!;
    Assert.Multiple(() => {
      Assert.That(HfsPlusDecmpfs.Method(encoding.Attribute), Is.EqualTo(10u));
      Assert.That(encoding.ResourceFork![12], Is.EqualTo(0xCC), "first chunk after the three-entry table");
      Assert.That(HfsPlusDecmpfs.Decode(encoding.Attribute, () => encoding.ResourceFork), Is.EqualTo(content));
    });
  }

  [TestCase(3786, true, TestName = "GivenTheLargestFileMethodOneHolds_WhenEncoded_ThenItIsInline")]
  [TestCase(3787, false, TestName = "GivenOneByteMoreThanMethodOneHolds_WhenEncoded_ThenItStaysUncompressed")]
  [Category("BoundaryCase")]
  public void MethodOneLimit(int length, bool encoded)
    => Assert.That(HfsPlusDecmpfs.Encode(Text(length), HfsPlusCompression.InlineUncompressed) is not null, Is.EqualTo(encoded));

  [TestCase(Chunk, TestName = "GivenExactlyOneChunkOfNoise_WhenRawEncoded_ThenOneChunkIsTabled")]
  [TestCase(Chunk + 1, TestName = "GivenOneByteIntoASecondChunk_WhenRawEncoded_ThenTwoChunksAreTabled")]
  [TestCase(Chunk - 1, TestName = "GivenOneByteShortOfAChunk_WhenRawEncoded_ThenOneChunkIsTabled")]
  [Category("BoundaryCase")]
  public void ChunkBoundary(int length) {
    var encoding = HfsPlusDecmpfs.Encode(Noise(length), HfsPlusCompression.Raw)!;
    var chunks = (length + Chunk - 1) / Chunk;
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(encoding.ResourceFork), Is.EqualTo(4 * (chunks + 1)));
      Assert.That(HfsPlusDecmpfs.Decode(encoding.Attribute, () => encoding.ResourceFork!), Is.EqualTo(Noise(length)));
    });
  }

  // ── Writing whole volumes ─────────────────────────────────────────

  internal static (string Name, byte[] Content)[] Files() => [
    ("tiny.txt", Text(19, 3)),
    ("inline.txt", Text(3000, 4)),
    ("one-chunk.bin", Text(Chunk, 5)),
    ("chunk-plus-one.bin", Text(Chunk + 1, 6)),
    ("large.bin", Text(5 * Chunk + 321, 7)),
    ("noise.bin", Noise(2 * Chunk + 3, 8)),
    ("empty.txt", []),
    ("dir/nested.txt", Text(5000, 9)),
  ];

  internal static byte[] CompressedVolume(HfsPlusCompression compression, uint blockSize = 4096) {
    var w = new HfsPlusWriter { TransparentCompression = compression };
    foreach (var (name, content) in Files()) w.AddFile(name, content);
    return w.Build(blockSize);
  }

  [TestCase(HfsPlusCompression.Zlib, TestName = "GivenAZlibVolume_WhenRead_ThenEveryFileIsItsContent")]
  [TestCase(HfsPlusCompression.Lzvn, TestName = "GivenALzvnVolume_WhenRead_ThenEveryFileIsItsContent")]
  [TestCase(HfsPlusCompression.Lzfse, TestName = "GivenALzfseVolume_WhenRead_ThenEveryFileIsItsContent")]
  [TestCase(HfsPlusCompression.Lzbitmap, TestName = "GivenALzbitmapVolume_WhenRead_ThenEveryFileIsItsContent")]
  [TestCase(HfsPlusCompression.Raw, TestName = "GivenARawVolume_WhenRead_ThenEveryFileIsItsContent")]
  [TestCase(HfsPlusCompression.InlineUncompressed, TestName = "GivenAMethodOneVolume_WhenRead_ThenEveryFileIsItsContent")]
  [Category("RoundTrip")]
  public void VolumeRoundTrip(HfsPlusCompression compression) {
    var reader = new HfsPlusReader(new MemoryStream(CompressedVolume(compression)));
    Assert.Multiple(() => {
      foreach (var (name, content) in Files()) {
        var entry = reader.Entries.Single(e => e.FullPath == name);
        Assert.That(entry.Size, Is.EqualTo(content.Length), name);
        Assert.That(reader.Extract(entry), Is.EqualTo(content), name);
      }
    });
  }

  [Test, Category("HappyPath")]
  public void GivenALzfseVolume_WhenTheRecordsAreInspected_ThenCompressedFilesLookAsMacOSStoresThem() {
    var image = CompressedVolume(HfsPlusCompression.Lzfse);
    var reader = new HfsPlusReader(new MemoryStream(image));
    Assert.Multiple(() => {
      foreach (var (name, method) in new[] { ("tiny.txt", 11u), ("inline.txt", 11u), ("large.bin", 12u) }) {
        var record = HfsPlusRemoveExtentsTests.FileRecordOffset(image, name);
        Assert.That(BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(record + 2)) & 0x0004, Is.Not.Zero, $"{name}: kHFSHasAttributesMask");
        Assert.That(image[record + 32 + 9] & 0x20, Is.Not.Zero, $"{name}: UF_COMPRESSED");
        Assert.That(BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(record + 88)), Is.Zero, $"{name}: empty data fork");
        Assert.That(HfsPlusDecmpfs.Method(reader.Entries.Single(e => e.FullPath == name).Decmpfs!), Is.EqualTo(method), name);
      }
      var noise = HfsPlusRemoveExtentsTests.FileRecordOffset(image, "noise.bin");
      Assert.That(image[noise + 32 + 9] & 0x20, Is.Zero, "incompressible data stays in the data fork");
      Assert.That(BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(noise + 88)), Is.EqualTo((ulong)(2 * Chunk + 3)));
    });
  }

  [TestCase(4096u, TestName = "GivenACompressedVolumeWithFourKiBBlocks_WhenChecked_ThenTheBitmapAgrees")]
  [TestCase(16384u, TestName = "GivenACompressedVolumeWithSixteenKiBBlocks_WhenChecked_ThenItStillReads")]
  [Category("HappyPath")]
  public void BitmapConsistent(uint blockSize) {
    var image = CompressedVolume(HfsPlusCompression.Zlib, blockSize);
    if (blockSize == 4096) HfsPlusRemoveExtentsTests.AssertBitmapConsistent(image);
    var reader = new HfsPlusReader(new MemoryStream(image));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.FullPath == "large.bin")), Is.EqualTo(Files().Single(f => f.Name == "large.bin").Content));
  }

  [Test, Category("HappyPath")]
  public void GivenManyCompressedFiles_WhenWritten_ThenTheAttributesTreeGrowsAnIndexAndEveryFileReads() {
    var w = new HfsPlusWriter { TransparentCompression = HfsPlusCompression.Zlib };
    for (var i = 0; i < 40; ++i) w.AddFile($"f{i:D2}.txt", Noise(3000, i));   // stored inline: ~3 KB attributes each
    var image = w.Build();
    var reader = new HfsPlusReader(new MemoryStream(image));
    var attributes = (int)(BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(1024 + 352 + 16)) * 4096);
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(attributes + 14)), Is.GreaterThan(1), "tree depth");
      for (var i = 0; i < 40; ++i)
        Assert.That(reader.Extract(reader.Entries.Single(e => e.FullPath == $"f{i:D2}.txt")), Is.EqualTo(Noise(3000, i)));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheDescriptorOption_WhenAVolumeIsCreated_ThenItsFilesAreCompressedAndRead() {
    var descriptor = new HfsPlusFormatDescriptor();
    var content = Text(200_000, 11);
    using var output = new MemoryStream();
    descriptor.Create(output, [ArchiveInputInfo.InMemory("big.txt", content)],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["Compression"] = "Lzbitmap" } });
    output.Position = 0;
    var reader = new HfsPlusReader(output, leaveOpen: true);
    var entry = reader.Entries.Single(e => e.FullPath == "big.txt");
    Assert.Multiple(() => {
      Assert.That(HfsPlusDecmpfs.Method(entry.Decmpfs!), Is.EqualTo(14u));
      Assert.That(reader.Extract(entry), Is.EqualTo(content));
    });
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAnUnknownCompressionOption_WhenCreating_ThenItIsRejected() {
    using var output = new MemoryStream();
    Assert.Throws<ArgumentException>(() => new HfsPlusFormatDescriptor().Create(output,
      [ArchiveInputInfo.InMemory("a.txt", [1])],
      new FormatCreateOptions { FormatSpecific = new Dictionary<string, string> { ["Compression"] = "Brotli" } }));
  }

  [Test, Category("HappyPath")]
  public void GivenAStreamingFile_WhenCompressed_ThenItIsReadFromTheStreamAndStored() {
    var content = Text(150_000, 12);
    var w = new HfsPlusWriter { TransparentCompression = HfsPlusCompression.Lzvn };
    w.AddStreamingFile("streamed.bin", content.Length, () => new MemoryStream(content));
    using var output = new MemoryStream();
    w.BuildToStreaming(output);
    output.Position = 0;
    var reader = new HfsPlusReader(output, leaveOpen: true);
    var entry = reader.Entries.Single();
    Assert.Multiple(() => {
      Assert.That(HfsPlusDecmpfs.Method(entry.Decmpfs!), Is.EqualTo(8u));
      Assert.That(reader.Extract(entry), Is.EqualTo(content));
    });
  }
}
