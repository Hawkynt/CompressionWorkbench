using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Lz4;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// Compressed, checksummed and inline extents: volumes bcachefs-tools and the
/// Linux kernel wrote with compression on, read back against the tree they were
/// made from, and the decoder's refusals and failure modes on constructed extents.
/// </summary>
/// <remarks>
/// Provenance of the images is in <c>ReferenceVectors/README.md</c>; the layout
/// rules are in <c>docs/BCACHEFS-ON-DISK.md</c>, "Compressed and checksummed data".
/// </remarks>
[TestFixture]
public sealed class BcacheFsCompressedDataTests {

  private const int Sector = 512;

  private static MemoryStream Image(string name) {
    using var resource = typeof(BcacheFsCompressedDataTests).Assembly
      .GetManifestResourceStream("BcacheFsVectors." + name)
      ?? throw new FileNotFoundException($"embedded reference vector {name} is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  private static Dictionary<string, (string Sha256, long Size)> Manifest() {
    using var resource = typeof(BcacheFsCompressedDataTests).Assembly
      .GetManifestResourceStream("BcacheFsVectors.manifest-compressed.txt")!;
    using var text = new StreamReader(resource);
    var files = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
    while (text.ReadLine() is { Length: > 0 } line) {
      var parts = line.Split(' ', 3);
      files[parts[2]] = (parts[0], long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }
    return files;
  }

  public static IEnumerable<TestCaseData> Volumes() {
    yield return new TestCaseData("bcachefs-tools-1.39-lz4-crc32c.img.gz", (int)BcacheFsCompressionType.Lz4, (int)BcacheFsChecksumType.Crc32C)
      .SetName("lz4 + crc32c, written by bcachefs-tools 1.39");
    yield return new TestCaseData("bcachefs-tools-1.39-gzip-crc64.img.gz", (int)BcacheFsCompressionType.Gzip, (int)BcacheFsChecksumType.Crc64)
      .SetName("gzip + crc64, written by bcachefs-tools 1.39");
    yield return new TestCaseData("bcachefs-tools-1.39-zstd-xxhash.img.gz", (int)BcacheFsCompressionType.Zstd, (int)BcacheFsChecksumType.XxHash)
      .SetName("zstd + xxhash, written by bcachefs-tools 1.39");
    yield return new TestCaseData("linux-6.8-zstd.img.gz", (int)BcacheFsCompressionType.Zstd, (int)BcacheFsChecksumType.Crc32C)
      .SetName("zstd + crc32c, written by the Linux 6.8 kernel");
  }

  private static IEnumerable<BcacheFsExtentCrc> Encodings(BcacheFsReader reader)
    => reader.Entries.SelectMany(e => e.Extents).Select(x => x.Stored?.Crc).OfType<BcacheFsExtentCrc>();

  // ── Real volumes ─────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  [TestCaseSource(nameof(Volumes))]
  public void GivenACompressedVolume_WhenRead_ThenEveryFileMatchesTheTreeItWasMadeFrom(
      string name, int compressionType, int checksumType) {
    var compression = (BcacheFsCompressionType)compressionType;
    var checksum = (BcacheFsChecksumType)checksumType;
    var manifest = Manifest();
    using var image = Image(name);
    using var reader = new BcacheFsReader(image);
    Assert.That(reader.Valid, Is.True, reader.Status);

    var encodings = Encodings(reader).ToList();
    Assert.Multiple(() => {
      Assert.That(encodings.Any(c => c.CompressionType == compression), Is.True,
        $"precondition: the volume holds {compression} extents");
      Assert.That(encodings.All(c => c.ChecksumType == checksum), Is.True,
        $"precondition: every extent is checksummed with {checksum}");
    });

    var files = reader.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.Multiple(() => {
      Assert.That(files.Keys, Is.EquivalentTo(manifest.Keys));
      foreach (var (file, (sha256, size)) in manifest) {
        if (!files.TryGetValue(file, out var entry)) continue;
        Assert.That(entry.Unreadable, Is.Null, $"{file} is readable");
        Assert.That(entry.Size, Is.EqualTo(size), $"{file} length");
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.Read(entry))), Is.EqualTo(sha256),
          $"{file} must read back byte for byte");
      }
    });
  }

  [Test, Category("HappyPath")]
  public void GivenACompressedVolume_WhenListedForLayout_ThenCompressedExtentsReportTheirStoredSize() {
    using var image = Image("bcachefs-tools-1.39-zstd-xxhash.img.gz");
    var blocks = new BcacheFsFormatDescriptor().EnumerateExtents(image).Where(b => b.FileName == "text/lorem.txt").ToList();
    image.Position = 0;
    using var reader = new BcacheFsReader(image);
    var lorem = reader.Entries.Single(e => e.Name == "text/lorem.txt");

    Assert.That(blocks.Sum(b => b.Length), Is.LessThan(lorem.Size / 2),
      "compressed text occupies well under half its length on the device");
  }

  [Test, Category("ErrorHandling")]
  [TestCase("bcachefs-tools-1.39-lz4-crc32c.img.gz")]
  [TestCase("bcachefs-tools-1.39-gzip-crc64.img.gz")]
  [TestCase("bcachefs-tools-1.39-zstd-xxhash.img.gz")]
  public void GivenACompressedExtentWithAFlippedByte_WhenRead_ThenTheChecksumMismatchIsReported(string name) {
    using var image = Image(name);
    BcacheFsReader.Entry victim;
    BcacheFsStoredExtent stored;
    using (var reader = new BcacheFsReader(image)) {
      victim = reader.Entries.First(e => e.Extents.Any(x => x.Stored?.Compressed == true));
      stored = victim.Extents.First(x => x.Stored?.Compressed == true).Stored!;
    }

    image.Position = stored.RegionSector * Sector + 5;
    var original = image.ReadByte();
    image.Position--;
    image.WriteByte((byte)(original ^ 0x40));

    image.Position = 0;
    using var damaged = new BcacheFsReader(image);
    var entry = damaged.Entries.Single(e => e.Name == victim.Name);
    var error = Assert.Throws<InvalidDataException>(() => damaged.Read(entry));
    Assert.That(error!.Message, Does.Contain("checksum mismatch"));
  }

  // ── Constructed extents ──────────────────────────────────────────────────

  private static byte[] Text(int sectors) {
    var data = new byte[sectors * Sector];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)("compressible "[i % 13] + i / 4096);
    return data;
  }

  /// <summary>An encoded region on an otherwise empty device, at sector 8.</summary>
  private static (MemoryStream Device, BcacheFsStoredExtent Stored) Region(
      byte[] encoded, int uncompressedSectors, uint offset, BcacheFsCompressionType compression,
      BcacheFsChecksumType checksum) {
    var compressedSectors = (encoded.Length + Sector - 1) / Sector;
    var region = new byte[compressedSectors * Sector];
    encoded.CopyTo(region, 0);
    Assert.That(BcacheFsChecksumCodec.TryCompute(checksum, region, out var sum), Is.True);
    var device = new MemoryStream(new byte[(8 + compressedSectors) * Sector]);
    device.Position = 8 * Sector;
    device.Write(region);
    var crc = new BcacheFsExtentCrc((uint)compressedSectors, (uint)uncompressedSectors, offset, 0, checksum, compression, sum);
    return (device, new BcacheFsStoredExtent(8, crc, null));
  }

  private static byte[] Padded(byte[] lz4Block) {
    // What the kernel leaves after the block: zeroes up to the block boundary.
    var result = new byte[(lz4Block.Length + Sector - 1) / Sector * Sector + Sector];
    lz4Block.CopyTo(result, 0);
    return result;
  }

  [Test, Category("BoundaryValue")]
  [TestCase(0u, 16, TestName = "Live data is the whole decoded region")]
  [TestCase(3u, 5, TestName = "Live data starts part way into the decoded region")]
  [TestCase(15u, 1, TestName = "Live data is the last decoded sector")]
  public void GivenADeflateExtent_WhenTheLiveSliceIsRead_ThenItIsThatSliceOfTheDecodedData(uint offset, int live) {
    var data = Text(16);
    var (device, stored) = Region(DeflateCompressor.Compress(data), 16, offset,
      BcacheFsCompressionType.Gzip, BcacheFsChecksumType.Crc32CNonzero);

    var bytes = BcacheFsExtentData.ReadLive(device, stored, live);

    Assert.That(bytes, Is.EqualTo(data.AsSpan((int)offset * Sector, live * Sector).ToArray()));
  }

  [Test, Category("HappyPath")]
  [TestCase(2, TestName = "LZ4 with crc64_nonzero")]
  [TestCase(5, TestName = "LZ4 with crc32c")]
  [TestCase(0, TestName = "LZ4 without a checksum")]
  public void GivenAnLz4BlockFollowedByPadding_WhenRead_ThenThePaddingIsNotParsed(int checksumType) {
    var checksum = (BcacheFsChecksumType)checksumType;
    var data = Text(32);
    var (device, stored) = Region(Padded(Lz4BlockCompressor.Compress(data)), 32, 0,
      BcacheFsCompressionType.Lz4, checksum);

    Assert.That(BcacheFsExtentData.ReadLive(device, stored, 32), Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void GivenALiveSliceBeyondTheDecodedRegion_WhenRead_ThenItIsRefused() {
    var (device, stored) = Region(DeflateCompressor.Compress(Text(4)), 4, 2,
      BcacheFsCompressionType.Gzip, BcacheFsChecksumType.Crc32C);

    Assert.Throws<InvalidDataException>(() => BcacheFsExtentData.ReadLive(device, stored, 3));
  }

  [Test, Category("ErrorHandling")]
  public void GivenAnExtentThatDecodesShort_WhenRead_ThenItIsRefused() {
    // Eight sectors of data claiming to decode to sixteen.
    var (device, stored) = Region(DeflateCompressor.Compress(Text(8)), 16, 0,
      BcacheFsCompressionType.Gzip, BcacheFsChecksumType.Crc32C);

    Assert.Throws<InvalidDataException>(() => BcacheFsExtentData.ReadLive(device, stored, 16));
  }

  [Test, Category("ErrorHandling")]
  public void GivenAZstdLengthPrefixLongerThanTheRegion_WhenRead_ThenItIsRefused() {
    var encoded = new byte[Sector];
    encoded[0] = 0xFF; encoded[1] = 0xFF;                                       // 65535 bytes claimed
    var (device, stored) = Region(encoded, 4, 0, BcacheFsCompressionType.Zstd, BcacheFsChecksumType.Crc32C);

    Assert.Throws<InvalidDataException>(() => BcacheFsExtentData.ReadLive(device, stored, 4));
  }

  [Test, Category("HappyPath")]
  public void GivenInlineData_WhenRead_ThenTheBytesComeFirstAndTheRestOfTheSectorIsZero() {
    var inline = "inline!\0"u8.ToArray();
    var bytes = BcacheFsExtentData.ReadLive(Stream.Null, new BcacheFsStoredExtent(0, null, inline), 1);

    Assert.Multiple(() => {
      Assert.That(bytes, Has.Length.EqualTo(Sector));
      Assert.That(bytes.AsSpan(0, inline.Length).ToArray(), Is.EqualTo(inline));
      Assert.That(bytes.Skip(inline.Length).All(b => b == 0), Is.True);
    });
  }

  private static BcacheFsExtentCrc Crc(BcacheFsChecksumType checksum, BcacheFsCompressionType compression,
      uint compressed = 4, uint uncompressed = 8, uint offset = 0)
    => new(compressed, uncompressed, offset, 0, checksum, compression, default);

  [Test, Category("ErrorHandling")]
  [TestCase(3, "encrypted", TestName = "chacha20_poly1305_80 is refused as encrypted")]
  [TestCase(4, "encrypted", TestName = "chacha20_poly1305_128 is refused as encrypted")]
  [TestCase(9, "checksum type 9", TestName = "An unknown checksum type is refused")]
  public void GivenAnExtentWithAChecksumThisReaderCannotCompute_WhenLocated_ThenItIsRefusedWithTheReason(
      int checksumType, string expected) {
    var checksum = (BcacheFsChecksumType)checksumType;
    Assert.Multiple(() => {
      Assert.That(BcacheFsExtentData.CanDecode(Crc(checksum, BcacheFsCompressionType.Zstd), out var reason), Is.False);
      Assert.That(reason, Does.Contain(expected));
    });
  }

  [Test, Category("ErrorHandling")]
  [TestCase(6)]
  [TestCase(15)]
  public void GivenAnUnknownCompressionType_WhenLocated_ThenItIsRefusedWithTheReason(int compressionType) {
    var compression = (BcacheFsCompressionType)compressionType;
    Assert.Multiple(() => {
      Assert.That(BcacheFsExtentData.CanDecode(Crc(BcacheFsChecksumType.Crc32C, compression), out var reason), Is.False);
      Assert.That(reason, Does.Contain($"compression type {(int)compression}"));
    });
  }

  [Test, Category("BoundaryValue")]
  [TestCase(7u, true, TestName = "Offset on the last decoded sector is accepted")]
  [TestCase(8u, false, TestName = "Offset one past the decoded region is refused")]
  public void GivenAnExtentOffset_WhenLocated_ThenItMustLieInsideTheDecodedRegion(uint offset, bool accepted)
    => Assert.That(BcacheFsExtentData.CanDecode(
      Crc(BcacheFsChecksumType.Crc32C, BcacheFsCompressionType.Lz4, offset: offset), out _), Is.EqualTo(accepted));

  [Test, Category("ErrorHandling")]
  public void GivenAnUncompressedExtentWhoseSizesDisagree_WhenLocated_ThenItIsRefused()
    => Assert.That(BcacheFsExtentData.CanDecode(
      Crc(BcacheFsChecksumType.Crc32C, BcacheFsCompressionType.None, compressed: 4, uncompressed: 8), out _), Is.False);

  [Test, Category("HappyPath")]
  [TestCase(5, TestName = "incompressible is stored plain and accepted")]
  [TestCase(1, TestName = "lz4_old decodes as lz4 and is accepted")]
  public void GivenACompressionTypeDecodedAsAnotherOrAsPlain_WhenLocated_ThenItIsAccepted(int compressionType)
    => Assert.That(BcacheFsExtentData.CanDecode(
      Crc(BcacheFsChecksumType.Crc32C, (BcacheFsCompressionType)compressionType, compressed: 8, uncompressed: 8),
      out var reason), Is.True, reason);
}
