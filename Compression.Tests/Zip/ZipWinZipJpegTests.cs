using System.Reflection;
using Compression.Core.Entropy.WinZipJpeg;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

/// <summary>
/// ZIP method 96 (WinZip JPEG): the archive WinZip 21 wrote (Zip/WinZipJpegVectors) must
/// extract to its source JPEG byte for byte; corrupt streams must fail rather than return bytes.
/// </summary>
[TestFixture]
public class ZipWinZipJpegTests {
  private const int PayloadOffset = 59;
  private const int PayloadSize = 3155;

  private static byte[] Vector(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ZipWinZipJpegVectors.{name}")
      ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static byte[] Payload() => Vector("winzip21-method96.zipx").AsSpan(PayloadOffset, PayloadSize).ToArray();

  [Test, Category("Spec")]
  public void Given_WinZip21Archive_When_Extracted_Then_TheSourceJpegComesBackByteForByte() {
    using var reader = new ZipReader(new MemoryStream(Vector("winzip21-method96.zipx")));
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].CompressionMethod, Is.EqualTo(ZipCompressionMethod.Jpeg));
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(Vector("method96-project-authored.jpg")));
  }

  [Test, Category("Spec")]
  public void Given_RawPayload_When_Decoded_Then_ItIsTheSourceJpeg()
    => Assert.That(WinZipJpegDecoder.Decode(Payload()), Is.EqualTo(Vector("method96-project-authored.jpg")));

  [Test, Category("Exception")]
  public void Given_UnknownPropertiesVersion_When_Decoded_Then_NotSupported() {
    var payload = Payload();
    payload[1] = 0x20;
    Assert.Throws<NotSupportedException>(() => WinZipJpegDecoder.Decode(payload));
  }

  [Test, Category("Exception")]
  public void Given_TruncatedPayload_When_Decoded_Then_InvalidData() {
    var payload = Payload();
    Assert.Throws<InvalidDataException>(() => WinZipJpegDecoder.Decode(payload.AsSpan(0, payload.Length / 2)));
  }

  [Test, Category("Exception")]
  public void Given_EmptyPayload_When_Decoded_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => WinZipJpegDecoder.Decode([]));

  [Test, Category("Exception")]
  public void Given_CorruptCoefficientByte_When_Extracted_Then_NoWrongBytesComeBack() {
    var archive = Vector("winzip21-method96.zipx");
    archive[PayloadOffset + PayloadSize - 200] ^= 0x5A;
    using var reader = new ZipReader(new MemoryStream(archive));
    Assert.That(() => reader.ExtractEntry(reader.Entries[0]), Throws.InstanceOf<InvalidDataException>());
  }
}

/// <summary>unar (XADMaster) as an independent decoder of the WinZip 21 sample.</summary>
[TestFixture]
[Category("ExternalInterop")]
public class ZipWinZipJpegUnarTests {
  [Test]
  public void Given_WinZip21Archive_When_UnarExtracts_Then_ItMatchesTheSourceJpeg() {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("WSL/Linux shell unavailable.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("'unar' is required.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_wzjpeg_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZipWinZipJpegVectors.winzip21-method96.zipx")!)
      using (var f = File.Create(Path.Combine(dir, "sample.zipx")))
        s.CopyTo(f);
      var r = FsInteropToolbox.RunWsl($"cd {FsInteropToolbox.WinToWsl(dir)} && unar -q -f -o out sample.zipx");
      if (r.ExitCode != 0) Assert.Ignore($"this unar build does not decode method 96:\n{r.StdOut}\n{r.StdErr}");
      var produced = Directory.GetFiles(Path.Combine(dir, "out"), "*", SearchOption.AllDirectories);
      Assert.That(produced, Has.Length.EqualTo(1));
      using var expected = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZipWinZipJpegVectors.method96-project-authored.jpg")!;
      using var ms = new MemoryStream();
      expected.CopyTo(ms);
      Assert.That(File.ReadAllBytes(produced[0]), Is.EqualTo(ms.ToArray()));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
