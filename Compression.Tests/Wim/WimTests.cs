using System.Buffers.Binary;
using System.Text;
using FileFormat.Wim;

namespace Compression.Tests.Wim;

[TestFixture]
public class WimTests {
  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  /// <summary>
  /// Reads every file back out of a WIM by name.
  /// </summary>
  /// <remarks>
  /// By name rather than by position in the lookup table: a WIM stores one copy
  /// of each distinct content and an empty file gets no entry at all, so the
  /// table is not a list of the files put in. What has to come back is the
  /// files, which the image's directory tree names.
  /// </remarks>
  private static Dictionary<string, byte[]> ReadBack(byte[] image) {
    using var ms = new MemoryStream(image);
    using var reader = new WimReader(ms);

    var seen = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var file in reader.GetNamedFiles())
      seen[file.FileName] = file.ResourceIndex < 0 ? [] : reader.ReadResource(file.ResourceIndex);
    return seen;
  }

  private static byte[] Build(
    IReadOnlyList<byte[]> resources,
    uint compressionType,
    int chunkSize = WimConstants.DefaultChunkSize) {
    using var ms = new MemoryStream();
    new WimWriter(ms, compressionType, chunkSize).Write(resources);
    return ms.ToArray();
  }

  private static byte[] RoundTrip(
    IReadOnlyList<byte[]> resources,
    uint compressionType = WimConstants.CompressionXpress,
    int chunkSize = WimConstants.DefaultChunkSize) {
    var seen = ReadBack(Build(resources, compressionType, chunkSize));
    Assert.That(seen.Count, Is.EqualTo(resources.Count));
    return resources.Count == 0 ? [] : seen["resource_0"];
  }

  private static void RoundTripAll(
    IReadOnlyList<byte[]> resources,
    uint compressionType = WimConstants.CompressionXpress,
    int chunkSize = WimConstants.DefaultChunkSize) {
    var seen = ReadBack(Build(resources, compressionType, chunkSize));
    Assert.That(seen.Count, Is.EqualTo(resources.Count));

    for (var i = 0; i < resources.Count; ++i)
      Assert.That(seen["resource_" + i], Is.EqualTo(resources[i]),
        $"Resource {i} did not round-trip correctly.");
  }

  // -------------------------------------------------------------------------
  // RoundTrip_EmptyResource
  // -------------------------------------------------------------------------

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyResource() {
    var result = RoundTrip([[]], WimConstants.CompressionNone);
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
  public void Reader_ImageCount_CountsImagesNotFiles() {
    // A WIM holds images, and an image holds any number of files. Counting one
    // image per file made every container we wrote claim several images while
    // describing one, which is the first thing a reader checks.
    var resources = new byte[][] {
      [1, 2, 3],
      [4, 5, 6],
    };
    using var ms = new MemoryStream();
    var writer = new WimWriter(ms, WimConstants.CompressionXpress);
    writer.Write(resources);

    ms.Seek(0, SeekOrigin.Begin);
    using var reader = new WimReader(ms);
    Assert.That(reader.Header.ImageCount, Is.EqualTo(1u));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void TheSameContentTwice_IsStoredOnce() {
    // Contents are addressed by the hash of their bytes, so two files that are
    // copies of each other are one resource with two names pointing at it.
    var shared = Encoding.UTF8.GetBytes("the very same bytes, twice over");
    var image = Build([shared, (byte[])shared.Clone(), Encoding.UTF8.GetBytes("different")],
      WimConstants.CompressionNone);

    using var ms = new MemoryStream(image);
    using var reader = new WimReader(ms);

    var payloads = reader.Resources.Count(r => !r.IsMetadata);
    Assert.That(payloads, Is.EqualTo(2), "the two copies should share one resource");

    var seen = ReadBack(image);
    Assert.That(seen["resource_0"], Is.EqualTo(shared));
    Assert.That(seen["resource_1"], Is.EqualTo(shared));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void AnEmptyFile_KeepsItsNameWithoutAResource() {
    // Nothing is stored for an empty file — it carries an all-zero hash instead
    // of a pointer — but it is still a file in the image and has to come back.
    var image = Build([[], Encoding.UTF8.GetBytes("not empty")], WimConstants.CompressionNone);

    using var ms = new MemoryStream(image);
    using var reader = new WimReader(ms);
    Assert.That(reader.Resources.Count(r => !r.IsMetadata), Is.EqualTo(1));

    var seen = ReadBack(image);
    Assert.That(seen.Keys, Does.Contain("resource_0"));
    Assert.That(seen["resource_0"], Is.Empty);
    Assert.That(seen["resource_1"], Is.EqualTo(Encoding.UTF8.GetBytes("not empty")));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void APathInAName_BecomesDirectoriesInTheImage() {
    using var ms = new MemoryStream();
    new WimWriter(ms, WimConstants.CompressionNone).Write([
      ("top.txt", "at the top"u8.ToArray()),
      ("nested/deeper/leaf.txt", "further down"u8.ToArray()),
    ]);

    var seen = ReadBack(ms.ToArray());
    Assert.That(seen["top.txt"], Is.EqualTo("at the top"u8.ToArray()));
    Assert.That(seen["nested/deeper/leaf.txt"], Is.EqualTo("further down"u8.ToArray()));
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

  /// <summary>
  /// An image asking for LZMS gets one, and it survives a round trip.
  /// </summary>
  /// <remarks>
  /// <para>This used to be a test that the writer refused LZMS, because what we
  /// wrote under that name was a private arrangement no reader opened. The
  /// format has since been derived from wimlib's own streams — the derivation is
  /// in <c>docs/LZMS-ON-DISK.md</c> — and the interop fixture checks the result
  /// against the tools that own the format. This one checks the container's own
  /// shape: an LZMS image is the later version with 128 KB chunks, and writing
  /// the ordinary version alongside LZMS would mark it as ours at a glance.</para>
  /// </remarks>
  [Test]
  public void Lzms_IsWrittenInTheContainerThatCarriesIt() {
    var files = new List<(string, byte[])> {
      ("A.TXT", System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("a line of text\n", 500)))),
    };

    using var stream = new MemoryStream();
    new WimWriter(stream, WimConstants.CompressionLzms).Write(files);

    var bytes = stream.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)),
      Is.EqualTo(WimConstants.VersionSolid), "an LZMS image carries the later version");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)),
      Is.EqualTo((uint)WimConstants.SolidChunkSize), "and its chunk size");

    stream.Position = 0;
    using var reader = new WimReader(stream);
    var entry = reader.GetNamedFiles().Single(e => e.FileName.EndsWith("A.TXT"));
    Assert.That(reader.ReadResource(entry.ResourceIndex), Is.EqualTo(files[0].Item2).AsCollection,
      "and it reads back as what went in");
  }

  /// <summary>
  /// The LZMS range coder's flush leaves one word of slack after its two shift-outs.
  /// </summary>
  /// <remarks>
  /// Taking a chunk wimlib wrote, decoding it to its items and writing those items
  /// back gives wimlib's bytes exactly - but only with one word here. With two, the
  /// result carried two extra zero bytes at offset eight and was otherwise identical.
  /// A chunk with either amount verifies, so nothing but a byte-for-byte comparison
  /// against wimlib catches it, and this is what pins it in the ordinary suite.
  /// </remarks>
  [Test]
  public void Lzms_TheCoderFlushLeavesOneWordOfSlack() {
    var encoder = new Compression.Core.Dictionary.Lzms.LzmsRangeEncoder();
    encoder.WriteBit(new Compression.Core.Dictionary.Lzms.LzmsProbability(), 0);

    Assert.That(encoder.Finish(), Has.Count.EqualTo(3),
      "two words shifted out and one of slack");
  }
}
