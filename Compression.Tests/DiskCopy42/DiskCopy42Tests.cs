using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.DiskCopy42;

namespace Compression.Tests.DiskCopy42;

[TestFixture]
public class DiskCopy42Tests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb-dc42-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  internal static byte[] Random(int size, int seed) {
    var data = new byte[size];
    new Random(seed).NextBytes(data);
    return data;
  }

  private ArchiveInputInfo Input(string name, byte[] data) {
    var path = Path.Combine(this._tmp, "in-" + Guid.NewGuid().ToString("N"));
    File.WriteAllBytes(path, data);
    return new ArchiveInputInfo(path, name, false);
  }

  private static byte[] Create(params ArchiveInputInfo[] inputs) {
    using var ms = new MemoryStream();
    new DiskCopy42FormatDescriptor().Create(ms, inputs, new FormatCreateOptions());
    return ms.ToArray();
  }

  private Dictionary<string, byte[]> Extract(byte[] image) {
    var dir = Path.Combine(this._tmp, "out-" + Guid.NewGuid().ToString("N"));
    new DiskCopy42FormatDescriptor().Extract(new MemoryStream(image), dir, null, null);
    return Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
  }

  [Test]
  public void GivenTheChecksumRule_WhenAppliedToKnownWords_ThenItAddsAndRotatesRight() {
    // 0x0001 -> sum 1 -> ror -> 0x80000000; + 0x0002 -> 0x80000002 -> ror -> 0x40000001.
    Assert.That(DiskCopy42Image.Checksum([0x00, 0x01, 0x00, 0x02]), Is.EqualTo(0x40000001u));
    Assert.That(DiskCopy42Image.Checksum([]), Is.EqualTo(0u));
  }

  [TestCase(409600, 0x00, 0x02)]
  [TestCase(819200, 0x01, 0x22)]
  [TestCase(737280, 0x02, 0x22)]
  [TestCase(1474560, 0x03, 0x22)]
  public void GivenAStandardDisk_WhenCreated_ThenHeaderMatchesTheSpecAndRoundTrips(int size, int encoding, int format) {
    var data = Random(size, size);
    var tags = Random(size / 512 * 12, 7);
    var image = Create(this.Input("disk.img", data), this.Input("tags.bin", tags));

    Assert.That(image.Length, Is.EqualTo(0x54 + data.Length + tags.Length));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(0x40)), Is.EqualTo((uint)size));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(0x44)), Is.EqualTo((uint)tags.Length));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(0x48)), Is.EqualTo(DiskCopy42Image.Checksum(data)));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(0x4C)), Is.EqualTo(DiskCopy42Image.Checksum(tags.AsSpan(12))));
    Assert.That(image[0x50], Is.EqualTo(encoding));
    Assert.That(image[0x51], Is.EqualTo(format));
    Assert.That(image[0x52], Is.EqualTo(0x01));
    Assert.That(image[0x53], Is.EqualTo(0x00));

    var files = this.Extract(image);
    Assert.That(files["disk.img"], Is.EqualTo(data));
    Assert.That(files["tags.bin"], Is.EqualTo(tags));
  }

  [Test]
  public void GivenMetadata_WhenCreated_ThenNameEncodingAndFormatAreTaken() {
    var ini = Encoding.UTF8.GetBytes("[diskcopy42]\nname=System Tools\nencoding=0x01\nformat=0x24\n");
    var image = Create(this.Input("disk.img", Random(819200, 1)), this.Input("metadata.ini", ini));
    Assert.That(image[0], Is.EqualTo(12));
    Assert.That(Encoding.ASCII.GetString(image, 1, 12), Is.EqualTo("System Tools"));
    Assert.That(image[0x51], Is.EqualTo(0x24));
    var metadata = Encoding.UTF8.GetString(this.Extract(image)["metadata.ini"]);
    Assert.That(metadata, Does.Contain("name=System Tools"));
    Assert.That(metadata, Does.Contain("data_checksum=").And.Contain("(valid)"));
  }

  [Test]
  public void GivenAnOddSizeWithoutEncoding_WhenCreated_ThenRefused()
    => Assert.Throws<ArgumentException>(() => Create(this.Input("disk.img", new byte[1000])));

  [Test]
  public void GivenAnOddSizeWithEncoding_WhenCreated_ThenAccepted() {
    var ini = Encoding.UTF8.GetBytes("encoding=0x01\nformat=0x22\n");
    var image = Create(this.Input("disk.img", new byte[1000]), this.Input("metadata.ini", ini));
    Assert.That(this.Extract(image)["disk.img"], Has.Length.EqualTo(1000));
  }

  [Test]
  public void GivenAnImage_WhenDiskReplaced_ThenChecksumsAreRecomputedAndOtherHeaderBytesKept() {
    var image = Create(this.Input("disk.img", Random(409600, 2)), this.Input("tags.bin", Random(800 * 12, 3)));
    image[40] = 0xAB; // a byte inside the name field, past the name: must survive the edit
    using var ms = new MemoryStream();
    ms.Write(image);
    var replacement = Random(409600, 4);
    new DiskCopy42FormatDescriptor().Add(ms, [this.Input("disk.img", replacement)]);
    var edited = ms.ToArray();
    Assert.That(edited[40], Is.EqualTo(0xAB));
    var files = this.Extract(edited);
    Assert.That(files["disk.img"], Is.EqualTo(replacement));
    Assert.That(files["tags.bin"], Is.EqualTo(Random(800 * 12, 3)));
  }

  [Test]
  public void GivenAnImage_WhenTagsRemoved_ThenTheTagFieldsAreZero() {
    var image = Create(this.Input("disk.img", Random(409600, 5)), this.Input("tags.bin", Random(800 * 12, 6)));
    using var ms = new MemoryStream();
    ms.Write(image);
    new DiskCopy42FormatDescriptor().Remove(ms, ["tags.bin"]);
    var edited = ms.ToArray();
    Assert.That(edited.Length, Is.EqualTo(0x54 + 409600));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(edited.AsSpan(0x44)), Is.Zero);
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(edited.AsSpan(0x4C)), Is.Zero);
    Assert.That(this.Extract(edited).ContainsKey("tags.bin"), Is.False);
  }

  [Test]
  public void GivenAnImage_WhenRemovingTheDisk_ThenRefusedAndUnchanged() {
    var image = Create(this.Input("disk.img", Random(409600, 8)));
    using var ms = new MemoryStream();
    ms.Write(image);
    Assert.Throws<NotSupportedException>(() => new DiskCopy42FormatDescriptor().Remove(ms, ["disk.img"]));
    Assert.That(ms.ToArray(), Is.EqualTo(image));
  }

  [Test]
  public void GivenACorruptedSector_WhenExtracted_ThenTheChecksumRefusesItButListStillWorks() {
    var image = Create(this.Input("disk.img", Random(409600, 9)));
    image[0x54 + 1000] ^= 0x10;
    var listed = new DiskCopy42FormatDescriptor().List(new MemoryStream(image), null);
    Assert.That(listed.Select(e => e.Name), Does.Contain("disk.img"));
    Assert.Throws<InvalidDataException>(() => this.Extract(image));
  }

  [Test]
  public void GivenGarbage_WhenListed_ThenFallsBackWithoutThrowing() {
    var listed = new DiskCopy42FormatDescriptor().List(new MemoryStream(new byte[50]), null);
    Assert.That(listed.Select(e => e.Name), Is.EquivalentTo(new[] { "FULL.dc42", "metadata.ini" }));
  }

  [Test]
  public void GivenATruncatedImage_WhenExtracted_ThenInvalidData() {
    var image = Create(this.Input("disk.img", Random(409600, 10)));
    Assert.Throws<InvalidDataException>(() => this.Extract(image[..^100]));
  }

  [Test]
  public void GivenAWrongMagic_WhenExtracted_ThenInvalidData() {
    var image = Create(this.Input("disk.img", Random(409600, 11)));
    image[0x52] = 0x02;
    Assert.Throws<InvalidDataException>(() => this.Extract(image));
  }

  [Test]
  public void GivenANameLongerThan63Bytes_WhenCreated_ThenRefused() {
    var ini = Encoding.UTF8.GetBytes("name=" + new string('x', 64) + "\n");
    Assert.Throws<ArgumentException>(() => Create(this.Input("disk.img", Random(409600, 12)), this.Input("metadata.ini", ini)));
  }
}
