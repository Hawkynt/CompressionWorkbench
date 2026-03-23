using System.Text;
using FileFormat.Wim;

namespace Compression.Tests.Wim;

[TestFixture]
public class WimTests {
  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  private static byte[] RoundTrip(
    IReadOnlyList<byte[]> resources,
    uint compressionType = WimConstants.CompressionXpress,
    int chunkSize = WimConstants.DefaultChunkSize) {
    using var ms = new MemoryStream();
    var writer = new WimWriter(ms, compressionType, chunkSize);
    writer.Write(resources);

    ms.Seek(0, SeekOrigin.Begin);
    using var reader = new WimReader(ms);
    Assert.That(reader.Resources.Count, Is.EqualTo(resources.Count));

    // Return the first resource (or empty for zero-resource WIMs).
    return reader.Resources.Count == 0 ? [] : reader.ReadResource(0);
  }

  private static void RoundTripAll(
    IReadOnlyList<byte[]> resources,
    uint compressionType = WimConstants.CompressionXpress,
    int chunkSize = WimConstants.DefaultChunkSize) {
    using var ms = new MemoryStream();
    var writer = new WimWriter(ms, compressionType, chunkSize);
    writer.Write(resources);

    ms.Seek(0, SeekOrigin.Begin);
    using var reader = new WimReader(ms);
    Assert.That(reader.Resources.Count, Is.EqualTo(resources.Count));

    for (var i = 0; i < resources.Count; ++i) {
      var result = reader.ReadResource(i);
      Assert.That(result, Is.EqualTo(resources[i]),
        $"Resource {i} did not round-trip correctly.");
    }
  }

  // -------------------------------------------------------------------------
  // RoundTrip_EmptyResource
  // -------------------------------------------------------------------------

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyResource() {
    var result = RoundTrip([[]]);
    Assert.That(result, Is.Empty);
  }

  // -------------------------------------------------------------------------
  // RoundTrip_SingleResource_Xpress
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleResource_Xpress() {
    var data = Encoding.UTF8.GetBytes("Hello, WIM XPRESS world! AAAAAAAAAAAAAAAA");
    var result = RoundTrip([data], WimConstants.CompressionXpress);
    Assert.That(result, Is.EqualTo(data));
  }

  // -------------------------------------------------------------------------
  // RoundTrip_SingleResource_XpressHuffman
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleResource_XpressHuffman() {
    var data = Encoding.UTF8.GetBytes("Hello, WIM XPRESS Huffman world! BBBBBBBBBBBBBBB");
    var result = RoundTrip([data], WimConstants.CompressionXpressHuffman);
    Assert.That(result, Is.EqualTo(data));
  }

  // -------------------------------------------------------------------------
  // RoundTrip_SingleResource_Lzx
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleResource_Lzx() {
    var data = Encoding.UTF8.GetBytes("Hello, WIM LZX world! CCCCCCCCCCCCCCCCCCCCCC");
    var result = RoundTrip([data], WimConstants.CompressionLzx);
    Assert.That(result, Is.EqualTo(data));
  }

  // -------------------------------------------------------------------------
  // RoundTrip_MultipleResources
  // -------------------------------------------------------------------------

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MultipleResources() {
    var resources = new byte[][] {
      Encoding.UTF8.GetBytes("First resource — alpha alpha alpha."),
      Encoding.UTF8.GetBytes("Second resource — beta beta beta."),
      Encoding.UTF8.GetBytes("Third resource — gamma gamma gamma."),
    };
    RoundTripAll(resources, WimConstants.CompressionXpress);
  }

  // -------------------------------------------------------------------------
  // RoundTrip_LargeResource (64 KB+ — forces multiple chunks)
  // -------------------------------------------------------------------------

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeResource() {
    // 96 KB of patterned data: forces at least 3 chunks with default 32 KB chunk size.
    var data = new byte[96 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 251);

    var result = RoundTrip([data], WimConstants.CompressionXpress);
    Assert.That(result, Is.EqualTo(data));
  }

  // -------------------------------------------------------------------------
  // RoundTrip_ResourceLargerThanChunk
  // -------------------------------------------------------------------------

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_ResourceLargerThanChunk() {
    // Use a small chunk size to force multiple chunks on modest data.
    var chunkSize = 1024;
    var rng = new Random(42);
    var data = new byte[4 * 1024];
    rng.NextBytes(data);

    var result = RoundTrip([data], WimConstants.CompressionXpress, chunkSize);
    Assert.That(result, Is.EqualTo(data));
  }

  // -------------------------------------------------------------------------
  // Reader_InvalidMagic_Throws
  // -------------------------------------------------------------------------

  [Category("Exception")]
  [Test]
  public void Reader_InvalidMagic_Throws() {
    // Write garbage data that does not start with the WIM magic.
    var bad = new byte[WimConstants.HeaderSize];
    new Random(1).NextBytes(bad);
    bad[0] = (byte)'B';
    bad[1] = (byte)'A';
    bad[2] = (byte)'D';

    using var ms = new MemoryStream(bad);
    Assert.Throws<InvalidDataException>(() => new WimReader(ms));
  }

  // -------------------------------------------------------------------------
  // Writer_NullStream_Throws
  // -------------------------------------------------------------------------

  [Category("Exception")]
  [Test]
  public void Writer_NullStream_Throws() {
    Assert.Throws<ArgumentNullException>(() => new WimWriter(null!));
  }

  // -------------------------------------------------------------------------
  // Additional: uncompressed round-trip
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Uncompressed() {
    var data = Encoding.UTF8.GetBytes("No compression here.");
    var result = RoundTrip([data], WimConstants.CompressionNone);
    Assert.That(result, Is.EqualTo(data));
  }

  // -------------------------------------------------------------------------
  // Additional: header fields are round-tripped correctly
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Reader_CompressionType_MatchesWriter() {
    var data = Encoding.UTF8.GetBytes("test");
    using var ms = new MemoryStream();

    var writer = new WimWriter(ms, WimConstants.CompressionLzx);
    writer.Write([data]);

    ms.Seek(0, SeekOrigin.Begin);
    using var reader = new WimReader(ms);
    Assert.That(reader.Header.CompressionType, Is.EqualTo(WimConstants.CompressionLzx));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Reader_ImageCount_MatchesResourceCount() {
    var resources = new byte[][] {
      [1, 2, 3],
      [4, 5, 6],
    };
    using var ms = new MemoryStream();
    var writer = new WimWriter(ms, WimConstants.CompressionXpress);
    writer.Write(resources);

    ms.Seek(0, SeekOrigin.Begin);
    using var reader = new WimReader(ms);
    Assert.That(reader.Header.ImageCount, Is.EqualTo(2u));
  }

  // -------------------------------------------------------------------------
  // Large random data with LZX
  // -------------------------------------------------------------------------

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeResource_Lzx() {
    var rng = new Random(99);
    var data = new byte[64 * 1024 + 13]; // slightly over 64 KB
    rng.NextBytes(data);

    var result = RoundTrip([data], WimConstants.CompressionLzx);
    Assert.That(result, Is.EqualTo(data));
  }

  // -------------------------------------------------------------------------
  // Large random data with XpressHuffman and multiple chunks
  // -------------------------------------------------------------------------

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeResource_XpressHuffman() {
    var data = new byte[70 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 128);

    var result = RoundTrip([data], WimConstants.CompressionXpressHuffman);
    Assert.That(result, Is.EqualTo(data));
  }

  // -------------------------------------------------------------------------
  // LZMS round-trip tests
  // -------------------------------------------------------------------------

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleResource_Lzms() {
    var data = Encoding.UTF8.GetBytes("Hello, WIM LZMS world! DDDDDDDDDDDDDDDDDDDD");
    var result = RoundTrip([data], WimConstants.CompressionLzms);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeResource_Lzms() {
    var data = new byte[64 * 1024 + 7];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 200);

    var result = RoundTrip([data], WimConstants.CompressionLzms);
    Assert.That(result, Is.EqualTo(data));
  }
}
