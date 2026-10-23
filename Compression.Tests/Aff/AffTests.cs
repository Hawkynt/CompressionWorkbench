using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Aff;

namespace Compression.Tests.Aff;

[TestFixture]
public class AffTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-aff-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  /// <summary>Random sectors, a zero run, compressible text: exercises every page kind.</summary>
  internal static byte[] SampleImage(int size, int seed) {
    var data = new byte[size];
    var random = new Random(seed);
    random.NextBytes(data.AsSpan(0, size / 3));
    var text = Encoding.ASCII.GetBytes("forensic evidence block ");
    for (var i = 2 * size / 3; i < size; ++i) data[i] = text[i % text.Length];
    return data;
  }

  private ArchiveInputInfo Input(string name, byte[] data) {
    var path = Path.Combine(this._tmp, "in-" + Guid.NewGuid().ToString("N"));
    File.WriteAllBytes(path, data);
    return new ArchiveInputInfo(path, name, false);
  }

  private static byte[] Write(byte[] image, int pageSize = AffImage.DefaultPageSize) {
    using var ms = new MemoryStream();
    AffImage.Write(ms, image, pageSize: pageSize);
    return ms.ToArray();
  }

  private Dictionary<string, byte[]> Extract(byte[] aff) {
    var dir = Path.Combine(this._tmp, "out-" + Guid.NewGuid().ToString("N"));
    new AffFormatDescriptor().Extract(new MemoryStream(aff), dir, null, null);
    return Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(4096)]
  [TestCase(65536 * 3 + 100)]
  public void GivenAnImage_WhenWrittenWithSmallPages_ThenItRoundTrips(int size) {
    var image = SampleImage(size, size);
    var aff = Write(image, pageSize: 65536);
    Assert.That(aff.AsSpan(0, 8).SequenceEqual("AFF10\r\n\0"u8), Is.True);
    Assert.That(AffImage.Read(new MemoryStream(aff)).ReadVerifiedImage(), Is.EqualTo(image));
  }

  [Test]
  public void GivenTheDescriptor_WhenCreatingAndExtracting_ThenDiskAndMetadataAppear() {
    var image = SampleImage(1 << 20, 3);
    using var ms = new MemoryStream();
    new AffFormatDescriptor().Create(ms, [this.Input("disk.img", image)], new FormatCreateOptions());
    var files = this.Extract(ms.ToArray());
    Assert.That(files["disk.img"], Is.EqualTo(image));
    var metadata = Encoding.UTF8.GetString(files["metadata.ini"]);
    Assert.That(metadata, Does.Contain("imagesize=1048576"));
    Assert.That(metadata, Does.Contain("pagesize=16777216"));
  }

  [Test]
  public void GivenTheQuadEncoding_WhenEncoded_ThenLowWordComesFirstBigEndian() {
    var q = AffImage.EncodeQuad(0x0000_0001_0000_0002);
    Assert.That(q, Is.EqualTo(new byte[] { 0, 0, 0, 2, 0, 0, 0, 1 }));
    Assert.That(AffImage.DecodeQuad(q), Is.EqualTo(0x0000_0001_0000_0002));
  }

  [Test]
  public void GivenAZeroPage_WhenWritten_ThenItIsStoredAsAFourByteCount() {
    var aff = Write(new byte[1000], pageSize: 4096);
    var image = AffImage.Read(new MemoryStream(aff));
    var page = image.Find("page0")!;
    Assert.That(page.Argument & AffImage.PageAlgorithmMask, Is.EqualTo(AffImage.PageAlgorithmZero));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(page.Data), Is.EqualTo(1000));
    Assert.That(image.ReadVerifiedImage(), Is.EqualTo(new byte[1000]));
  }

  [Test]
  public void GivenAnImageWithAcquisitionMetadata_WhenDiskReplaced_ThenMetadataSurvivesAndHashesFollow() {
    using var ms = new MemoryStream();
    AffImage.Write(ms, SampleImage(10000, 1), [new AffSegment("acquisition_notes", 0, "case 42"u8.ToArray())], pageSize: 4096);
    var replacement = SampleImage(20000, 2);
    new AffFormatDescriptor().Add(ms, [this.Input("disk.img", replacement)]);
    var edited = AffImage.Read(new MemoryStream(ms.ToArray()));
    Assert.That(edited.ReadVerifiedImage(), Is.EqualTo(replacement));
    Assert.That(edited.PageSize, Is.EqualTo(4096));
    Assert.That(Encoding.ASCII.GetString(edited.Find("acquisition_notes")!.Data), Is.EqualTo("case 42"));
  }

  [Test]
  public void GivenAMissingMiddlePage_WhenRead_ThenItIsZeroFilled() {
    var image = SampleImage(3 * 4096, 4);
    var aff = AffImage.Read(new MemoryStream(Write(image, 4096)));
    // Rebuild by hand: header + pagesize + imagesize + page0 + page2.
    var rebuilt = new MemoryStream();
    rebuilt.Write(AffImage.FileMagic);
    WriteSeg(rebuilt, "pagesize", 4096, []);
    WriteSeg(rebuilt, "imagesize", AffImage.QuadwordFlag, AffImage.EncodeQuad(image.Length));
    WriteSeg(rebuilt, "page0", aff.Find("page0")!.Argument, aff.Find("page0")!.Data);
    WriteSeg(rebuilt, "page2", aff.Find("page2")!.Argument, aff.Find("page2")!.Data);
    var read = AffImage.Read(new MemoryStream(rebuilt.ToArray())).ReadVerifiedImage();
    Assert.That(read.AsSpan(0, 4096).SequenceEqual(image.AsSpan(0, 4096)), Is.True);
    Assert.That(read.AsSpan(4096, 4096).ContainsAnyExcept((byte)0), Is.False);
    Assert.That(read.AsSpan(8192).SequenceEqual(image.AsSpan(8192)), Is.True);
  }

  [Test]
  public void GivenAnEmptyNameSegment_WhenRead_ThenItIsIgnored() {
    var rebuilt = new MemoryStream();
    rebuilt.Write(AffImage.FileMagic);
    WriteSeg(rebuilt, "", 0, new byte[37]);
    WriteSeg(rebuilt, "pagesize", 4096, []);
    WriteSeg(rebuilt, "page0", 0, "hello"u8.ToArray());
    var image = AffImage.Read(new MemoryStream(rebuilt.ToArray()));
    Assert.That(image.Segments.Select(s => s.Name), Is.EqualTo(new[] { "pagesize", "page0" }));
    Assert.That(image.ReadVerifiedImage(), Is.EqualTo("hello"u8.ToArray()));
  }

  [Test]
  public void GivenAStoredMd5Mismatch_WhenExtracted_ThenRefused() {
    var aff = Write(SampleImage(5000, 5), 4096);
    var md5Index = aff.AsSpan().LastIndexOf("md5"u8);
    aff[md5Index + 3] ^= 0xFF; // first byte of the md5 payload
    Assert.Throws<InvalidDataException>(() => this.Extract(aff));
  }

  [Test]
  public void GivenABrokenTail_WhenRead_ThenInvalidDataAndListFallsBack() {
    var aff = Write(SampleImage(5000, 6), 4096);
    var tail = aff.AsSpan().IndexOf("ATT\0"u8);
    aff[tail] = (byte)'X';
    Assert.Throws<InvalidDataException>(() => AffImage.Read(new MemoryStream(aff)));
    var listed = new AffFormatDescriptor().List(new MemoryStream(aff), null);
    Assert.That(listed.Select(e => e.Name), Does.Contain("FULL.aff"));
  }

  [Test]
  public void GivenATruncatedFile_WhenRead_ThenInvalidData() {
    var aff = Write(SampleImage(5000, 7), 4096);
    Assert.Throws<InvalidDataException>(() => AffImage.Read(new MemoryStream(aff[..^5])));
  }

  [Test]
  public void GivenNoHeader_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => AffImage.Read(new MemoryStream("AFF11\r\n\0"u8.ToArray())));

  [Test]
  public void GivenAnEncryptedSegment_WhenExtracted_ThenNotSupported() {
    var rebuilt = new MemoryStream();
    rebuilt.Write(AffImage.FileMagic);
    WriteSeg(rebuilt, "pagesize", 4096, []);
    WriteSeg(rebuilt, "affkey_aes256", 0, new byte[52]);
    WriteSeg(rebuilt, "page0/aes256", 0, new byte[16]);
    Assert.Throws<NotSupportedException>(() => this.Extract(rebuilt.ToArray()));
  }

  [Test]
  public void GivenTheImage_WhenRemoved_ThenRefused()
    => Assert.Throws<NotSupportedException>(() => new AffFormatDescriptor().Remove(new MemoryStream(Write([1, 2, 3])), ["disk.img"]));

  private static void WriteSeg(Stream s, string name, uint arg, byte[] data) {
    var n = Encoding.ASCII.GetBytes(name);
    var head = new byte[16];
    "AFF\0"u8.CopyTo(head);
    BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (uint)n.Length);
    BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), (uint)data.Length);
    BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), arg);
    s.Write(head);
    s.Write(n);
    s.Write(data);
    var tail = new byte[8];
    "ATT\0"u8.CopyTo(tail);
    BinaryPrimitives.WriteUInt32BigEndian(tail.AsSpan(4), (uint)(24 + n.Length + data.Length));
    s.Write(tail);
  }
}
