using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using FileFormat.Lzfse;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Files stored with HFS+ transparent compression (a "com.apple.decmpfs" attribute and, for
/// the chunked methods, the resource fork) used to list and extract as 0 bytes. Judged against
/// the six afsctool-compressed files macOS wrote into keramics' <c>hfsplus.raw</c>, with the
/// sizes and hashes libfshfs 20260922 decodes (HfsPlus/ReferenceVectors/README.md; the live
/// comparison is <see cref="HfsPlusLibfshfsOracleTests"/>), and against attributes built here
/// for the boundaries that volume does not reach.
/// </summary>
[TestFixture]
public sealed class HfsPlusDecmpfsTests {

  private const string ShortSha256 = "a78f2707f267bc43180b87ad4f4834996e9adb55d03e6cb84b888c58ecc1cc5f";
  private const string LicenseSha256 = "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30";
  private const int ChunkSize = 65536;

  /// <summary>keramics' hfsplus.raw, decompressed from its embedded gzip.</summary>
  internal static byte[] KeramicsRaw() {
    using var resource = typeof(HfsPlusDecmpfsTests).Assembly.GetManifestResourceStream("HfsPlusVectors.hfsplus.raw.gz")
                         ?? throw new InvalidOperationException("hfsplus.raw.gz is not embedded.");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    using var raw = new MemoryStream();
    gzip.CopyTo(raw);
    return raw.ToArray();
  }

  private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

  [Test, Category("HappyPath")]
  public void GivenTheKeramicsVolume_WhenEmbedded_ThenItIsTheUpstreamFile() {
    var raw = KeramicsRaw();
    Assert.Multiple(() => {
      Assert.That(raw, Has.Length.EqualTo(4153344));
      Assert.That(Sha256(raw), Is.EqualTo("9e5bbcbc64b44d9b22b192d9ab90f2f65e39360f191d0fc93922627262c2e771"));
    });
  }

  [TestCase("testdir1/compressed1", 19, ShortSha256, TestName = "GivenZlibInline_WhenExtracted_ThenItIsWhatLibfshfsDecodes")]
  [TestCase("testdir1/compressed2", 11358, LicenseSha256, TestName = "GivenZlibInTheResourceFork_WhenExtracted_ThenItIsWhatLibfshfsDecodes")]
  [TestCase("testdir1/compressed3", 19, ShortSha256, TestName = "GivenLzvnInline_WhenExtracted_ThenItIsWhatLibfshfsDecodes")]
  [TestCase("testdir1/compressed4", 11358, LicenseSha256, TestName = "GivenLzvnInTheResourceFork_WhenExtracted_ThenItIsWhatLibfshfsDecodes")]
  [TestCase("testdir1/compressed5", 19, ShortSha256, TestName = "GivenLzfseInline_WhenExtracted_ThenItIsWhatLibfshfsDecodes")]
  [TestCase("testdir1/compressed6", 11358, LicenseSha256, TestName = "GivenLzfseInTheResourceFork_WhenExtracted_ThenItIsWhatLibfshfsDecodes")]
  [Category("HappyPath")]
  public void KeramicsCompressedFile(string path, long size, string sha256) {
    var reader = new HfsPlusReader(new MemoryStream(KeramicsRaw()));
    var entry = reader.Entries.Single(e => e.FullPath == path);
    var bytes = reader.Extract(entry);
    Assert.Multiple(() => {
      Assert.That(entry.Size, Is.EqualTo(size));
      Assert.That(bytes, Has.Length.EqualTo(size));
      Assert.That(Sha256(bytes), Is.EqualTo(sha256));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheKeramicsVolume_WhenTheCompressedLicenceIsRead_ThenItEqualsTheUncompressedCopy() {
    var reader = new HfsPlusReader(new MemoryStream(KeramicsRaw()));
    var plain = reader.Extract(reader.Entries.Single(e => e.FullPath == "testdir1/TestFile2"));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.FullPath == "testdir1/compressed6")), Is.EqualTo(plain));
  }

  [Test, Category("HappyPath")]
  public void GivenTheKeramicsVolume_WhenListedAndExtractedThroughTheDescriptor_ThenTheCompressedFilesHaveTheirContent() {
    var descriptor = new HfsPlusFormatDescriptor();
    var raw = KeramicsRaw();
    var listed = descriptor.List(new MemoryStream(raw), null).Where(e => e.Name.Contains("compressed", StringComparison.Ordinal)).ToList();
    var output = Path.Combine(Path.GetTempPath(), "cwb_decmpfs_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      descriptor.Extract(new MemoryStream(raw), output, null, null);
      Assert.Multiple(() => {
        Assert.That(listed.Select(e => e.OriginalSize), Is.EqualTo(new long[] { 19, 11358, 19, 11358, 19, 11358 }));
        Assert.That(File.ReadAllText(Path.Combine(output, "testdir1", "compressed3")), Is.EqualTo("My compressed file\n"));
        Assert.That(Sha256(File.ReadAllBytes(Path.Combine(output, "testdir1", "compressed4"))), Is.EqualTo(LicenseSha256));
      });
    } finally {
      try { Directory.Delete(output, recursive: true); } catch { /* best effort */ }
    }
  }

  // ── Built attributes: the boundaries ───────────────────────────────

  private static byte[] Attribute(uint method, long size, ReadOnlySpan<byte> payload = default) {
    var attribute = new byte[16 + payload.Length];
    "fpmc"u8.CopyTo(attribute);
    BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), method);
    BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(8), (ulong)size);
    payload.CopyTo(attribute.AsSpan(16));
    return attribute;
  }

  private static byte[] Pattern(int length) {
    var bytes = new byte[length];
    for (var i = 0; i < bytes.Length; ++i) bytes[i] = (byte)(i * 31 / 7);
    return bytes;
  }

  /// <summary>A chunk-offset table (methods 8, 10, 12) followed by the chunks.</summary>
  private static byte[] ChunkTable(IReadOnlyList<byte[]> chunks) {
    var table = 4 * (chunks.Count + 1);
    using var fork = new MemoryStream();
    var offset = table;
    var header = new byte[table];
    for (var i = 0; i <= chunks.Count; ++i) {
      BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4 * i), offset);
      if (i < chunks.Count) offset += chunks[i].Length;
    }
    fork.Write(header);
    foreach (var chunk in chunks) fork.Write(chunk);
    return fork.ToArray();
  }

  private static byte[] ZlibCompress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
    return output.ToArray();
  }

  private static byte[] LzfseCompress(byte[] data) {
    using var output = new MemoryStream();
    LzfseStream.Compress(new MemoryStream(data), output);
    return output.ToArray();
  }

  /// <summary>A classic resource fork holding one 'cmpf' resource: the zlib block table of method 4.</summary>
  private static byte[] ZlibResourceFork(IReadOnlyList<byte[]> blocks) {
    const int dataOffset = 0x100;
    var tableLength = 4 + 8 * blocks.Count;
    var body = new MemoryStream();
    var table = new byte[tableLength];
    BinaryPrimitives.WriteInt32LittleEndian(table, blocks.Count);
    var at = tableLength;
    for (var i = 0; i < blocks.Count; ++i) {
      BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(4 + 8 * i), at);
      BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(8 + 8 * i), blocks[i].Length);
      at += blocks[i].Length;
    }
    body.Write(table);
    foreach (var block in blocks) body.Write(block);
    var fork = new byte[dataOffset + 4 + body.Length];
    BinaryPrimitives.WriteInt32BigEndian(fork, dataOffset);
    BinaryPrimitives.WriteInt32BigEndian(fork.AsSpan(dataOffset), (int)body.Length);
    body.ToArray().CopyTo(fork, dataOffset + 4);
    return fork;
  }

  [Test, Category("BoundaryCase")]
  public void GivenStoredMarkers_WhenDecoded_ThenTheRestIsTheContent() {
    var content = "stored"u8.ToArray();
    Assert.Multiple(() => {
      Assert.That(HfsPlusDecmpfs.Decode(Attribute(3, 6, [0xFF, .. content]), () => []), Is.EqualTo(content));
      Assert.That(HfsPlusDecmpfs.Decode(Attribute(7, 6, [0x06, .. content]), () => []), Is.EqualTo(content));
      Assert.That(HfsPlusDecmpfs.Decode(Attribute(11, 6, [0xFF, .. content]), () => []), Is.EqualTo(content));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenZlibInline_WhenDecoded_ThenItIsTheContent() {
    var content = Pattern(3000);
    Assert.That(HfsPlusDecmpfs.Decode(Attribute(3, content.Length, ZlibCompress(content)), () => []), Is.EqualTo(content));
  }

  [TestCase(ChunkSize, TestName = "GivenExactlyOneFullChunk_WhenLzfseChunksAreDecoded_ThenItIsTheContent")]
  [TestCase(ChunkSize + 1, TestName = "GivenOneByteIntoTheSecondChunk_WhenLzfseChunksAreDecoded_ThenItIsTheContent")]
  [TestCase(2 * ChunkSize + 777, TestName = "GivenThreeChunks_WhenLzfseChunksAreDecoded_ThenItIsTheContent")]
  [Category("BoundaryCase")]
  public void LzfseChunks(int length) {
    var content = Pattern(length);
    var chunks = content.Chunk(ChunkSize).Select(LzfseCompress).ToList();
    Assert.That(HfsPlusDecmpfs.Decode(Attribute(12, length), () => ChunkTable(chunks)), Is.EqualTo(content));
  }

  [Test, Category("BoundaryCase")]
  public void GivenStoredLzvnChunksAcrossABoundary_WhenDecoded_ThenItIsTheContent() {
    var content = Pattern(ChunkSize + 100);
    var chunks = content.Chunk(ChunkSize).Select(static c => (byte[])[0x06, .. c]).ToList();
    Assert.That(HfsPlusDecmpfs.Decode(Attribute(8, content.Length), () => ChunkTable(chunks)), Is.EqualTo(content));
  }

  [Test, Category("BoundaryCase")]
  public void GivenZlibBlocksAcrossABoundary_WhenDecoded_ThenItIsTheContent() {
    var content = Pattern(ChunkSize + 4096);
    var blocks = content.Chunk(ChunkSize).Select(static c => ZlibCompress(c)).ToList();
    Assert.That(HfsPlusDecmpfs.Decode(Attribute(4, content.Length), () => ZlibResourceFork(blocks)), Is.EqualTo(content));
  }

  [TestCase(0u, TestName = "GivenMethodZero_WhenDecoded_ThenItIsUndefined")]
  [TestCase(2u, TestName = "GivenMethodTwo_WhenDecoded_ThenItIsUndefined")]
  [TestCase(6u, TestName = "GivenMethodSix_WhenDecoded_ThenItIsUndefined")]
  [TestCase(15u, TestName = "GivenMethodFifteen_WhenDecoded_ThenItIsUndefined")]
  [TestCase(0x80000003u, TestName = "GivenAnUnknownDatalessLikeMethod_WhenDecoded_ThenItIsUndefined")]
  [Category("ExceptionalCase")]
  public void UnsupportedMethod(uint method)
    => Assert.Throws<NotSupportedException>(() => HfsPlusDecmpfs.Decode(Attribute(method, 4, [1, 2, 3, 4]), () => []));

  [Test, Category("ExceptionalCase")]
  public void GivenAChunkTableShorterThanTheChunksItNeeds_WhenDecoded_ThenItIsInvalid()
    => Assert.Throws<InvalidDataException>(() => HfsPlusDecmpfs.Decode(Attribute(12, 2 * ChunkSize), () => new byte[8]));

  [Test, Category("ExceptionalCase")]
  public void GivenAChunkOffsetPastTheFork_WhenDecoded_ThenItIsInvalid() {
    var fork = ChunkTable([[0x06, 1, 2]]);
    BinaryPrimitives.WriteInt32LittleEndian(fork.AsSpan(4), 1000);
    Assert.Throws<InvalidDataException>(() => HfsPlusDecmpfs.Decode(Attribute(8, 2), () => fork));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenContentShorterThanTheHeaderSays_WhenDecoded_ThenItIsInvalid()
    => Assert.Throws<InvalidDataException>(() => HfsPlusDecmpfs.Decode(Attribute(3, 10, [0xFF, 1, 2]), () => []));

  [TestCase(new byte[] { 0x66, 0x70, 0x6D }, TestName = "GivenAnAttributeShorterThanItsHeader_WhenSized_ThenItHasNoSize")]
  [TestCase(new byte[] { 0x63, 0x6D, 0x70, 0x66, 3, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 }, TestName = "GivenTheMagicInTheWrongByteOrder_WhenSized_ThenItHasNoSize")]
  [Category("ExceptionalCase")]
  public void NoHeader(byte[] attribute) => Assert.That(HfsPlusDecmpfs.TryReadSize(attribute, out _), Is.False);

  [Test, Category("BoundaryCase")]
  public void GivenAnEmptyCompressedFile_WhenSized_ThenItIsEmpty() {
    Assert.Multiple(() => {
      Assert.That(HfsPlusDecmpfs.TryReadSize(Attribute(3, 0), out var size), Is.True);
      Assert.That(size, Is.Zero);
      Assert.That(HfsPlusDecmpfs.Decode(Attribute(3, 0, [0xFF]), () => []), Is.Empty);
    });
  }
}
