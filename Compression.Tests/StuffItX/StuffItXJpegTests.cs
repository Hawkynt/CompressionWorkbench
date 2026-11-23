using System.Security.Cryptography;
using FileFormat.StuffItX;

namespace Compression.Tests.StuffItX;

/// <summary>
/// StuffIt X method 7 (JPEG recompression). The archives StuffIt Deluxe 2009 and 2010 wrote
/// (ssokolow/stuffit-test-files, CC0) must restore the original testfile.jpg byte for byte; the eight
/// complete streams of KaitoKit's JPEG fixture (JpegVectors/README.md) cover mode 1, baseline and
/// progressive mode 2, restart markers and header-only images.
/// </summary>
[TestFixture]
public class StuffItXJpegTests {
  private const string Jpg = "e514232511df1a4f4221a75c27523518c3c62a2fe6470fa56e430364428eecd1";

  private static readonly string[] Cases = [
    "mode2-prog0-1x1-rst0-literal0-inter0",
    "mode2-prog0-3x3-rst2-literal0-inter0",
    "mode1-prog0-3x3-rst2-literal0-inter0",
    "mode2-prog1-1x1-rst0-literal0-inter0",
    "mode2-prog1-2x3-rst2-literal0-inter0",
    "mode2-prog0-1x1-rst0-literal1-inter0",
    "mode1-prog0-1x1-rst0-literal1-inter0",
    "mode2-prog1-2x3-rst0-literal0-inter1",
  ];

  private static byte[] Resource(string name) {
    using var s = typeof(StuffItXJpegTests).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static byte[] Stream(string name) => Resource("StuffItXJpegVectors." + name + ".jc");

  private static byte[] Expected(string name) => Resource("StuffItXJpegVectors." + name + ".jpg");

  private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

  [TestCase("testfile.stuffit_deluxe_2009.win.sitx")]
  [TestCase("testfile.stuffit_deluxe_2010.win.sitx")]
  [Category("ThemVsUs")]
  public void GivenStuffItWrittenJpeg_WhenExtracted_ThenTheOriginalFileIsRestored(string vector) {
    using var r = new StuffItXReader(new MemoryStream(Resource("StuffItXVectors." + vector)));
    var jpg = r.Entries.Single(e => e.FullPath == "sources/testfile.jpg");
    Assert.That(jpg.Method, Is.EqualTo("JPEG"));
    Assert.That(Sha(r.Extract(jpg)), Is.EqualTo(Jpg));
  }

  [TestCaseSource(nameof(Cases)), Category("HappyPath")]
  public void GivenCompleteStream_WhenDecoded_ThenTheJpegMatches(string name)
    => Assert.That(StuffItXJpeg.Decode(Stream(name), -1), Is.EqualTo(Expected(name)));

  [TestCaseSource(nameof(Cases)), Category("Boundary")]
  public void GivenCompleteStream_WhenTheDeclaredSizeIsTheOutputSize_ThenAccepted(string name)
    => Assert.That(StuffItXJpeg.Decode(Stream(name), Expected(name).Length), Has.Length.EqualTo(Expected(name).Length));

  [TestCaseSource(nameof(Cases)), Category("Exception")]
  public void GivenCompleteStream_WhenTheDeclaredSizeIsOneShort_ThenRefused(string name)
    => Assert.That(() => StuffItXJpeg.Decode(Stream(name), Expected(name).Length - 1), Throws.InstanceOf<InvalidDataException>());

  [TestCaseSource(nameof(Cases)), Category("Exception")]
  public void GivenTruncatedStream_WhenDecoded_ThenEveryPrefixIsRefused(string name) {
    var data = Stream(name);
    for (var end = 0; end < data.Length; ++end)
      Assert.That(() => StuffItXJpeg.Decode(data[..end], -1), Throws.InstanceOf<InvalidDataException>(), $"{name} cut at {end}");
  }

  [TestCaseSource(nameof(Cases)), Category("Exception")]
  public void GivenStreamWithATrailingByte_WhenDecoded_ThenRefused(string name)
    => Assert.That(() => StuffItXJpeg.Decode([.. Stream(name), 0], -1), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("HappyPath")]
  public void GivenMode0_WhenDecoded_ThenTheStoredBytesComeBack()
    => Assert.That(StuffItXJpeg.Decode([0, 3, 0xFF, 0xD8, 0xFF], 3), Is.EqualTo(new byte[] { 0xFF, 0xD8, 0xFF }));

  [TestCase(new byte[] { 0, 4, 0xFF, 0xD8, 0xFF }, -1L)]   // fewer bytes than declared
  [TestCase(new byte[] { 0, 2, 0xFF, 0xD8, 0xFF }, -1L)]   // more bytes than declared
  [TestCase(new byte[] { 0, 3, 0xFF, 0xD8, 0xFF }, 4L)]    // the archive expects another size
  [Category("Exception")]
  public void GivenMode0WithAnExtentMismatch_WhenDecoded_ThenRefused(byte[] data, long size)
    => Assert.That(() => StuffItXJpeg.Decode(data, size), Throws.InstanceOf<InvalidDataException>());

  [TestCase(3)]
  [TestCase(127)]
  [Category("Exception")]
  public void GivenUnknownCodecMode_WhenDecoded_ThenRefusedAsNotSupported(byte mode)
    => Assert.That(() => StuffItXJpeg.Decode([mode, 0], -1), Throws.InstanceOf<NotSupportedException>());

  [Test, Category("Boundary")]
  public void GivenMultiByteWzSize_WhenDecoded_ThenTheBigEndianGroupsAreJoined() {
    // 0x81 0x00 = 128: a mode-0 stream of 128 bytes
    var data = new byte[3 + 128];
    data[1] = 0x81;
    Assert.That(StuffItXJpeg.Decode(data, 128), Has.Length.EqualTo(128));
  }

  [Test, Category("Exception")]
  public void GivenUnterminatedWz_WhenDecoded_ThenRefused()
    => Assert.That(() => StuffItXJpeg.Decode([0, .. Enumerable.Repeat((byte)0x80, 10)], -1), Throws.InstanceOf<InvalidDataException>());

  [Test, Category("Exception")]
  public void GivenStuffItWrittenJpegWithACorruptByte_WhenExtracted_ThenRefusedByItsChecksum() {
    var data = Resource("StuffItXVectors.testfile.stuffit_deluxe_2009.win.sitx");
    using var clean = new StuffItXReader(new MemoryStream(data));
    var jpg = clean.Entries.Single(e => e.FullPath == "sources/testfile.jpg");
    data[(int)jpg.DataOffset + 40] ^= 0x10;
    using var r = new StuffItXReader(new MemoryStream(data));
    Assert.That(() => r.Extract(r.Entries.Single(e => e.FullPath == "sources/testfile.jpg")), Throws.InstanceOf<InvalidDataException>());
  }
}
