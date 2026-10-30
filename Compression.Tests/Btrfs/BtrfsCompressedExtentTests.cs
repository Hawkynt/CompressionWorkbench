using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Core.Dictionary.Lzo;
using FileSystem.Btrfs;

namespace Compression.Tests.Btrfs;

/// <summary>
/// Compressed file extents, read from volumes the Linux kernel's own btrfs driver
/// wrote with <c>compress-force=zlib|lzo|zstd</c> (see <c>ReferenceVectors/README.md</c>),
/// plus the extent framing in isolation.
/// </summary>
/// <remarks>
/// The reader used to skip every compressed extent, so a compressed file came back
/// short or as zeros with no error. The images also carry a hole with no extent item
/// (NO_HOLES), a trailing hole, a preallocated extent and a compressed extent that a
/// later in-place write split into two references with a non-zero offset.
/// </remarks>
[TestFixture]
public sealed class BtrfsCompressedExtentTests {

  private static MemoryStream Image(string name) {
    using var resource = typeof(BtrfsCompressedExtentTests).Assembly
      .GetManifestResourceStream("BtrfsVectors." + name)
      ?? throw new FileNotFoundException($"embedded reference vector {name} is missing");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  private static Dictionary<string, (string Sha256, long Size)> Manifest() {
    using var resource = typeof(BtrfsCompressedExtentTests).Assembly.GetManifestResourceStream("BtrfsVectors.manifest.txt")!;
    using var text = new StreamReader(resource);
    var files = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
    while (text.ReadLine() is { } line) {
      if (line.Length == 0) continue;
      var parts = line.Split(' ', 3);
      files[parts[2]] = (parts[0], long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }
    return files;
  }

  [TestCase("btrfs-zlib.img.gz")]
  [TestCase("btrfs-lzo.img.gz")]
  [TestCase("btrfs-zstd.img.gz")]
  [Category("HappyPath")]
  public void GivenAKernelCompressedVolume_WhenEveryFileIsExtracted_ThenItMatchesWhatTheKernelReadBack(string vector) {
    using var image = Image(vector);
    using var reader = new BtrfsReader(image);
    var expected = Manifest();
    var files = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name.Replace('\\', '/'), StringComparer.Ordinal);

    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys));
    Assert.Multiple(() => {
      foreach (var (path, (sha, size)) in expected) {
        var bytes = reader.Extract(files[path]);
        Assert.That(bytes.LongLength, Is.EqualTo(size), $"{vector}: {path} length");
        Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes)), Is.EqualTo(sha), $"{vector}: {path} content");
      }
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAKernelCompressedVolume_WhenStreamedOut_ThenTheStreamMatchesTheArray() {
    using var image = Image("btrfs-zstd.img.gz");
    using var reader = new BtrfsReader(image);
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory)) {
      using var ms = new MemoryStream();
      reader.ExtractTo(entry, ms);
      Assert.That(ms.ToArray(), Is.EqualTo(reader.Extract(entry)), entry.Name);
    }
  }

  // ── Framing in isolation ────────────────────────────────────────────────

  private static byte[] Text(int length) {
    var r = new Random(7);
    var words = new[] { "alpha ", "beta ", "gamma ", "delta\n" };
    var sb = new System.Text.StringBuilder();
    while (sb.Length < length) sb.Append(words[r.Next(words.Length)]);
    return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, length));
  }

  /// <summary>Frames <paramref name="data" /> the way the btrfs LZO format lays it out.</summary>
  private static byte[] FrameLzo(byte[] data, int sector) {
    var ms = new MemoryStream();
    ms.Write(new byte[4]);
    for (var off = 0; off < data.Length; off += sector) {
      var left = sector - (int)(ms.Length % sector);
      if (left < 4) ms.Write(new byte[left]);
      var seg = Lzo1xCompressor.Compress(data.AsSpan(off, Math.Min(sector, data.Length - off)));
      var len = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)seg.Length);
      ms.Write(len);
      ms.Write(seg);
    }
    var framed = ms.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)framed.Length);
    return framed;
  }

  [Test, Category("HappyPath")]
  public void GivenLzoSegmentsAcrossSectors_WhenDecompressed_ThenEverySegmentIsJoined() {
    var data = Text(5 * 4096 + 123);
    var framed = FrameLzo(data, 4096);
    Assert.That(BtrfsCompression.Decompress(BtrfsCompression.Lzo, framed, data.Length, 4096), Is.EqualTo(data));
  }

  [Test, Category("Boundary")]
  public void GivenALzoSegmentHeaderThatWouldStraddleASector_WhenDecompressed_ThenThePaddingIsSkipped() {
    // A first segment of incompressible bytes whose LZO stream ends 1-3 bytes short of the
    // sector end: the next header must start on the following sector.
    var r = new Random(5);
    for (var n = 3900; n <= 4096; n++) {
      var head = new byte[n];
      r.NextBytes(head);
      var seg = Lzo1xCompressor.Compress(head);
      var left = 4096 - (8 + seg.Length);
      if (left is < 1 or > 3) continue;

      var tail = Text(3000);
      var tailSeg = Lzo1xCompressor.Compress(tail);
      var framed = new byte[4096 + 4 + tailSeg.Length];
      BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)framed.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(4), (uint)seg.Length);
      seg.CopyTo(framed, 8);
      BinaryPrimitives.WriteUInt32LittleEndian(framed.AsSpan(4096), (uint)tailSeg.Length);
      tailSeg.CopyTo(framed, 4100);
      var expected = head.Concat(tail).ToArray();
      Assert.That(BtrfsCompression.Decompress(BtrfsCompression.Lzo, framed, expected.Length, 4096), Is.EqualTo(expected));
      return;
    }
    Assert.Fail("no segment length landed within three bytes of the sector end");
  }

  [Test, Category("HappyPath")]
  public void GivenAZlibStreamWithSectorPadding_WhenDecompressed_ThenThePaddingIsIgnored() {
    var data = Text(70000);
    var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
    var framed = ms.ToArray().Concat(new byte[4096 - (int)(ms.Length % 4096)]).ToArray();
    Assert.That(BtrfsCompression.Decompress(BtrfsCompression.Zlib, framed, data.Length, 4096), Is.EqualTo(data));
  }

  [Test, Category("Exception")]
  public void GivenAnUnknownMethod_WhenDecompressed_ThenItIsRefused() =>
    Assert.Throws<NotSupportedException>(() => BtrfsCompression.Decompress(4, new byte[16], 16, 4096));

  [Test, Category("Exception")]
  public void GivenALzoHeaderLongerThanTheExtent_WhenDecompressed_ThenItIsRejected() {
    var framed = FrameLzo(Text(100), 4096);
    BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)framed.Length + 1);
    Assert.Throws<InvalidDataException>(() => BtrfsCompression.Decompress(BtrfsCompression.Lzo, framed, 100, 4096));
  }

  [Test, Category("Boundary")]
  public void GivenATailStreamShorterThanRamBytes_WhenDecompressed_ThenTheRestOfTheSectorIsZero() {
    // A file's last extent: the stream ends at end-of-file, ram_bytes is sector-rounded.
    var data = Text(1000);
    var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
    var decoded = BtrfsCompression.Decompress(BtrfsCompression.Zlib, ms.ToArray(), 4096, 4096);
    Assert.That(decoded, Is.EqualTo(data.Concat(new byte[3096]).ToArray()));
  }

  [Test, Category("Exception")]
  public void GivenATruncatedZlibStream_WhenDecompressed_ThenItIsRejected() {
    var data = Text(20000);
    var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
    var cut = ms.ToArray().AsSpan(0, (int)ms.Length / 2).ToArray();
    Assert.Catch<Exception>(() => BtrfsCompression.Decompress(BtrfsCompression.Zlib, cut, 20480, 4096));
  }

  [Test, Category("Exception")]
  public void GivenANonZlibPayloadMarkedZlib_WhenDecompressed_ThenItIsRejected() =>
    Assert.Throws<InvalidDataException>(() => BtrfsCompression.Decompress(BtrfsCompression.Zlib, new byte[] { 0x12, 0x34, 0, 0 }, 4, 4096));
}
