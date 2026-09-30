using System.Buffers.Binary;
using System.Reflection;
using FileFormat.Dar;

namespace Compression.Tests.Dar;

[TestFixture]
public class DarTests {

  private static readonly byte[] Name = [0xEC, 0xF8, 0xBC, 0x6A, 0x00, 0x00, 0x00, 0x00, 0x6D, 0x03];

  /// <summary>A DAR infinint: the smallest number of 4-byte groups that holds the value, announced
  /// by a length byte with one bit set (bit 7 = one group).</summary>
  private static byte[] Infinint(ulong value) {
    var groups = value > uint.MaxValue ? 2 : 1;
    var result = new byte[1 + groups * 4];
    result[0] = (byte)(0x80 >> (groups - 1));
    for (var i = 0; i < groups * 4; ++i)
      result[^(i + 1)] = (byte)(value >> (8 * i));
    return result;
  }

  private static byte[] Tlv(ushort type, byte[] value) {
    var result = new List<byte> { (byte)(type >> 8), (byte)type };
    result.AddRange(Infinint((ulong)value.Length));
    result.AddRange(value);
    return [.. result];
  }

  /// <summary>A slice: fixed header, <paramref name="extensionData"/>, a payload and an optional trailer.</summary>
  private static byte[] Slice(char flag, char extension, byte[] extensionData, char? trailer, uint magic = 123) {
    using var ms = new MemoryStream();
    Span<byte> m = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(m, magic);
    ms.Write(m);
    ms.Write(Name);
    ms.WriteByte((byte)flag);
    ms.WriteByte((byte)extension);
    ms.Write(extensionData);
    var payload = new byte[64];
    Array.Fill(payload, (byte)0xA5);
    ms.Write(payload);
    if (trailer is { } t)
      ms.WriteByte((byte)t);
    return ms.ToArray();
  }

  private static byte[] TlvList(params byte[][] tlvs) => [.. Infinint((ulong)tlvs.Length), .. tlvs.SelectMany(t => t)];

  private static string Metadata(byte[] image) {
    var dir = Path.Combine(Path.GetTempPath(), "dar_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(image);
      new DarFormatDescriptor().Extract(ms, dir, null, ["metadata.ini"]);
      return File.ReadAllText(Path.Combine(dir, "metadata.ini"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new DarFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Dar"));
    Assert.That(d.Extensions, Contains.Item(".dar"));
    // Weak magic -> extension-driven detection only.
    Assert.That(d.MagicSignatures, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void GivenASlice_WhenListed_ThenOnlyTheImageAndItsMetadataAreOffered() {
    var img = Slice('T', 'N', [], 'T');
    using var ms = new MemoryStream(img);
    var entries = new DarFormatDescriptor().List(ms, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "FULL.dar", "metadata.ini" }));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(img.Length));
  }

  [Test, Category("HappyPath")]
  public void GivenASlice_WhenExtracted_ThenTheImageIsByteIdentical() {
    var img = Slice('T', 'T', TlvList(Tlv(3, Name)), 'T');
    var dir = Path.Combine(Path.GetTempPath(), "dar_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      new DarFormatDescriptor().Extract(ms, dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "FULL.dar")), Is.EqualTo(img));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void GivenAFormat8SingleSlice_WhenParsed_ThenTheTlvDataNameAndLastSliceAreReported() {
    var meta = Metadata(Slice('T', 'T', TlvList(Tlv(3, Name)), 'T'));
    Assert.That(meta, Does.Contain("slice_magic_ok=1"));
    Assert.That(meta, Does.Contain("magic=0x0000007B"));
    Assert.That(meta, Does.Contain("internal_name=ECF8BC6A000000006D03"));
    Assert.That(meta, Does.Contain("header_flag=T"));
    Assert.That(meta, Does.Contain("extension_flag=T"));
    Assert.That(meta, Does.Contain("data_name=ECF8BC6A000000006D03"));
    Assert.That(meta, Does.Contain("last_slice=yes"));
    Assert.That(meta, Does.Contain("slice_header_length=38"));   // 16 + count 5 + type 2 + length 5 + name 10
    Assert.That(meta, Does.Contain("member_enumeration=deferred"));
    Assert.That(meta, Does.Contain("parse_status=ok"));
  }

  [TestCase('N', "no")]
  [TestCase('T', "yes")]
  public void GivenAHeaderThatDefersToTheTrailer_WhenParsed_ThenTheTrailerDecides(char trailer, string expected) {
    var meta = Metadata(Slice('E', 'T', TlvList(Tlv(2, Infinint(1500)), Tlv(1, Infinint(1200)), Tlv(3, Name)), trailer));
    Assert.That(meta, Does.Contain("header_flag=E"));
    Assert.That(meta, Does.Contain($"trailer_flag={trailer}"));
    Assert.That(meta, Does.Contain($"last_slice={expected}"));
    Assert.That(meta, Does.Contain("first_slice_size=1500"));
    Assert.That(meta, Does.Contain("slice_size=1200"));
    Assert.That(meta, Does.Contain("parse_status=ok"));
  }

  [Test]
  public void GivenAHeaderThatDefersToAMissingTrailer_WhenParsed_ThenItIsPartial() {
    var meta = Metadata(Slice('E', 'T', TlvList(Tlv(3, Name)), trailer: null));
    Assert.That(meta, Does.Contain("last_slice=unknown"));
    Assert.That(meta, Does.Contain("parse_status=partial"));
  }

  [Test]
  public void GivenASliceSizeBeyond32Bits_WhenParsed_ThenTheTwoGroupInfinintIsRead() {
    // dar -s 5G writes 40 00000001 40000000.
    var meta = Metadata(Slice('E', 'T', TlvList(Tlv(1, Infinint(5UL << 30))), 'T'));
    Assert.That(meta, Does.Contain($"slice_size={5UL << 30}"));
    Assert.That(meta, Does.Contain("parse_status=ok"));
  }

  [Test]
  public void GivenAPreFormat8SizeExtension_WhenParsed_ThenTheSliceSizeIsRead() {
    var meta = Metadata(Slice('N', 'S', Infinint(4096), trailer: null));
    Assert.That(meta, Does.Contain("extension_flag=S"));
    Assert.That(meta, Does.Contain("slice_size=4096"));
    Assert.That(meta, Does.Contain("last_slice=no"));
    Assert.That(meta, Does.Contain("slice_header_length=21"));
    Assert.That(meta, Does.Contain("parse_status=ok"));
  }

  [Test]
  public void GivenAnUnknownTlvType_WhenParsed_ThenItIsSkippedAndNamed() {
    var meta = Metadata(Slice('T', 'T', TlvList(Tlv(7, [1, 2, 3]), Tlv(3, Name)), 'T'));
    Assert.That(meta, Does.Contain("unknown_tlv_types=7"));
    Assert.That(meta, Does.Contain("data_name=ECF8BC6A000000006D03"));
    Assert.That(meta, Does.Contain("parse_status=ok"));
  }

  [TestCase('X', 'T', TestName = "GivenAnUnknownSliceFlag_WhenParsed_ThenItIsPartial")]
  [TestCase('T', 'Q', TestName = "GivenAnUnknownExtensionFlag_WhenParsed_ThenItIsPartial")]
  public void InvalidFlags_ArePartial(char flag, char extension) {
    var meta = Metadata(Slice(flag, extension, TlvList(Tlv(3, Name)), 'T'));
    Assert.That(meta, Does.Contain("parse_status=partial"));
    Assert.That(meta, Does.Contain("problem="));
  }

  [Test]
  public void GivenTheWrongMagic_WhenParsed_ThenItIsPartial() {
    // 0x7E was this reader's previous, wrong, idea of the magic.
    var meta = Metadata(Slice('T', 'N', [], 'T', magic: 0x7E));
    Assert.That(meta, Does.Contain("slice_magic_ok=0"));
    Assert.That(meta, Does.Contain("parse_status=partial"));
  }

  private static IEnumerable<TestCaseData> TruncatedHeaders() {
    yield return new TestCaseData(new byte[] { 0x80, 0x00, 0x00 }).SetName("GivenATlvCountCutShort_WhenParsed_ThenItIsPartial");
    yield return new TestCaseData(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01 }).SetName("GivenALengthByteWithTwoBits_WhenParsed_ThenItIsPartial");
    yield return new TestCaseData(TlvList(Tlv(1, [0x80, 0x00]))).SetName("GivenASliceSizeTlvWithoutAWholeInfinint_WhenParsed_ThenItIsPartial");
    yield return new TestCaseData(new byte[] { 0x80, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x80, 0x7F, 0xFF, 0xFF, 0xFF })
      .SetName("GivenATlvLongerThanTheFile_WhenParsed_ThenItIsPartial");
    yield return new TestCaseData(new byte[] { 0x80, 0xFF, 0xFF, 0xFF, 0xFF }).SetName("GivenMoreTlvsThanTheFileHolds_WhenParsed_ThenItIsPartial");
  }

  [TestCaseSource(nameof(TruncatedHeaders))]
  public void MalformedTlvLists_ArePartialAndDoNotThrow(byte[] extensionData) {
    // No payload after the extension data, so nothing past it can be mistaken for more TLVs.
    var image = Slice('T', 'T', extensionData, trailer: null)[..(16 + extensionData.Length)];
    string meta = null!;
    Assert.DoesNotThrow(() => meta = Metadata(image));
    Assert.That(meta, Does.Contain("parse_status=partial"));
  }

  [TestCase(0)]
  [TestCase(15)]
  public void GivenAFileShorterThanTheFixedHeader_WhenParsed_ThenItIsPartial(int length) {
    var image = Slice('T', 'N', [], 'T')[..length];
    using var ms = new MemoryStream(image);
    Assert.DoesNotThrow(() => new DarFormatDescriptor().List(ms, null));
    Assert.That(Metadata(image), Does.Contain("parse_status=partial"));
  }

  [Test]
  public void GivenExactlyTheFixedHeaderWithNoExtension_WhenParsed_ThenItIsValid() {
    var image = Slice('T', 'N', [], trailer: null)[..16];
    var meta = Metadata(image);
    Assert.That(meta, Does.Contain("slice_header_length=16"));
    Assert.That(meta, Does.Contain("parse_status=ok"));
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[64];
    Array.Fill(garbage, (byte)0x33);
    var d = new DarFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "dar_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.List(ms, null));
      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "FULL.dar")), Is.EqualTo(garbage));
      Assert.That(File.ReadAllText(Path.Combine(dir, "metadata.ini")), Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  // ── Slices written by dar 2.8.6 (see ReferenceVectors/README.md) ──────────

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DarVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded DAR vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  [Test]
  public void GivenASingleSliceArchiveFromDar_WhenParsed_ThenHeaderAndTrailerAgree() {
    var meta = Metadata(Vector("single.1.dar"));
    Assert.That(meta, Does.Contain("header_flag=T"));
    Assert.That(meta, Does.Contain("extension_flag=T"));
    Assert.That(meta, Does.Contain("trailer_flag=T"));
    Assert.That(meta, Does.Contain("last_slice=yes"));
    Assert.That(meta, Does.Contain("internal_name=11F9BC6A000000007305"));
    Assert.That(meta, Does.Contain("data_name=11F9BC6A000000007305"));
    Assert.That(meta, Does.Not.Contain("slice_size="));
    Assert.That(meta, Does.Contain("parse_status=ok"));
  }

  [TestCase("sliced.1.dar", "no")]
  [TestCase("sliced.2.dar", "no")]
  [TestCase("sliced.3.dar", "yes")]
  public void GivenASlicedArchiveFromDar_WhenEachSliceIsParsed_ThenTheTrailerTellsTheLastOne(string name, string last) {
    var meta = Metadata(Vector(name));
    Assert.That(meta, Does.Contain("header_flag=E"));
    Assert.That(meta, Does.Contain($"last_slice={last}"));
    Assert.That(meta, Does.Contain("first_slice_size=1500"));
    Assert.That(meta, Does.Contain("slice_size=1200"));
    Assert.That(meta, Does.Contain("parse_status=ok"));
  }

  [Test]
  public void GivenASlicedArchiveFromDar_WhenParsed_ThenEverySliceSharesOneInternalName() {
    var names = new[] { "sliced.1.dar", "sliced.2.dar", "sliced.3.dar" }
      .Select(n => Metadata(Vector(n)).Split('\n').Single(l => l.StartsWith("internal_name=", StringComparison.Ordinal)))
      .Distinct()
      .ToArray();
    Assert.That(names, Has.Length.EqualTo(1));
  }

  [Test]
  public void GivenASlicedArchiveFromDar_WhenMeasured_ThenTheRecordedSizesMatchTheFiles() {
    Assert.That(Vector("sliced.1.dar"), Has.Length.EqualTo(1500));
    Assert.That(Vector("sliced.2.dar"), Has.Length.EqualTo(1200));
    Assert.That(Vector("sliced.3.dar").Length, Is.LessThanOrEqualTo(1200));
  }
}
