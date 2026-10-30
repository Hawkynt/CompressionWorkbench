using System.Reflection;
using System.Text;
using Compression.Registry;
using FileFormat.Um3;
using FileFormat.Utage;

namespace Compression.Tests.GameAssetCiphers;

/// <summary>
/// Bruns UM3 and UTAGE single-file obfuscations. Oracle: GARbro 1.5.44 decoded the <c>cwb-*</c>
/// files we wrote to exactly what it decodes from the plain originals (<c>garbro-decode.txt</c>).
/// See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class GameAssetCipherTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"GameAssetCipherVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static byte[] Run(Action<Stream, Stream> op, byte[] input) {
    using var output = new MemoryStream();
    op(new MemoryStream(input), output);
    return output.ToArray();
  }

  private static void DumpIfRequested(string name, byte[] bytes) {
    var dir = Environment.GetEnvironmentVariable("CWB_DUMP_GAMECIPHER");
    if (!string.IsNullOrEmpty(dir)) File.WriteAllBytes(Path.Combine(dir, name), bytes);
  }

  /// <summary>GARbro's decode line for a file: everything after its name.</summary>
  private static string GarbroDecode(string file)
    => Encoding.UTF8.GetString(Vector("garbro-decode.txt")).Replace("\r\n", "\n").Split('\n')
      .Single(l => l.StartsWith(file + "\t", StringComparison.Ordinal))[(file.Length + 1)..];

  // ── UM3 ──────────────────────────────────────────────────────────────

  [Test, Category("ThirdPartyVector")]
  public void Given_Ogg_When_EncodedAsUm3_Then_IdenticalToTheFileGarbroPlays() {
    var um3 = Run(new Um3FormatDescriptor().Compress, Vector("tone.ogg"));
    DumpIfRequested("cwb-tone.um3", um3);
    Assert.That(um3, Is.EqualTo(Vector("cwb-tone.um3")));
    Assert.That(GarbroDecode("cwb-tone.um3"), Is.EqualTo(GarbroDecode("tone.ogg")));
  }

  [Test, Category("RoundTrip")]
  public void Given_Um3_When_Decoded_Then_TheOriginalOgg()
    => Assert.That(Run(new Um3FormatDescriptor().Decompress, Vector("cwb-tone.um3")), Is.EqualTo(Vector("tone.ogg")));

  [Test, Category("ThirdPartyVector")]
  public void Given_Um3_When_Encoded_Then_OnlyTheFirst2048BytesAreInverted() {
    var ogg = Vector("tone.ogg");
    var um3 = Vector("cwb-tone.um3");
    Assert.That(um3[..4], Is.EqualTo(new byte[] { 0xB0, 0x98, 0x98, 0xAC }));
    Assert.That(um3[2047], Is.EqualTo((byte)~ogg[2047]));
    Assert.That(um3[2048..], Is.EqualTo(ogg[2048..]));
  }

  [TestCase(4)]
  [TestCase(2047)]
  [TestCase(2048)]
  [TestCase(2049)]
  [Category("EdgeCase")]
  public void Given_OggOfBoundaryLength_When_RoundTripped_Then_Identical(int length) {
    var ogg = new byte[length];
    "OggS"u8.CopyTo(ogg);
    for (var i = 4; i < length; ++i) ogg[i] = (byte)(i * 7);
    var d = new Um3FormatDescriptor();
    Assert.That(Run(d.Decompress, Run(d.Compress, ogg)), Is.EqualTo(ogg));
  }

  [Test, Category("EdgeCase")]
  public void Given_NonOgg_When_EncodedAsUm3_Then_Refused()
    => Assert.Throws<InvalidDataException>(() => Run(new Um3FormatDescriptor().Compress, "RIFF....WAVE"u8.ToArray()));

  [Test, Category("EdgeCase")]
  public void Given_PlainOgg_When_DecodedAsUm3_Then_Refused()
    => Assert.Throws<InvalidDataException>(() => Run(new Um3FormatDescriptor().Decompress, Vector("tone.ogg")));

  [Test, Category("EdgeCase")]
  public void Given_EmptyInput_When_DecodedAsUm3_Then_Refused()
    => Assert.Throws<InvalidDataException>(() => Run(new Um3FormatDescriptor().Decompress, []));

  [Test, Category("HappyPath")]
  public void Given_Um3Bytes_When_DetectedByMagic_Then_Um3()
    => Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Vector("cwb-tone.um3")).ToString(), Is.EqualTo("Um3"));

  // ── UTAGE ────────────────────────────────────────────────────────────

  [TestCase("t.png", "cwb-t.png.utage")]
  [TestCase("t.jpg", "cwb-t.jpg.utage")]
  [Category("ThirdPartyVector")]
  public void Given_Image_When_EncodedAsUtage_Then_IdenticalToTheFileGarbroDecodes(string plain, string encoded) {
    var utage = Run(new UtageFormatDescriptor().Compress, Vector(plain));
    DumpIfRequested(encoded, utage);
    Assert.That(utage, Is.EqualTo(Vector(encoded)));
    Assert.That(GarbroDecode(encoded), Is.EqualTo(GarbroDecode(plain)));
  }

  [TestCase("cwb-t.png.utage", "t.png")]
  [TestCase("cwb-t.jpg.utage", "t.jpg")]
  [Category("RoundTrip")]
  public void Given_Utage_When_Decoded_Then_TheOriginal(string encoded, string plain)
    => Assert.That(Run(new UtageFormatDescriptor().Decompress, Vector(encoded)), Is.EqualTo(Vector(plain)));

  [Test, Category("EdgeCase")]
  public void Given_EveryByteValueAtEveryKeyPosition_When_AppliedTwice_Then_Identity() {
    var data = new byte[256 * 16];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i / 16);
    var once = Run((i, o) => UtageFormatDescriptor.Apply(i, o, UtageFormatDescriptor.DefaultKey), data);
    Assert.That(Run((i, o) => UtageFormatDescriptor.Apply(i, o, UtageFormatDescriptor.DefaultKey), once), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void Given_ZerosAndKeyBytes_When_Encoded_Then_LeftAlone() {
    var key = UtageFormatDescriptor.DefaultKey.ToArray();
    var data = new byte[32];
    key.CopyTo(data, 16); // the second key period holds exactly the key
    Assert.That(Run(new UtageFormatDescriptor().Compress, data), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Given_CustomKey_When_Encoded_Then_OnlyThatKeyDecodes() {
    var d = new UtageFormatDescriptor();
    var plain = Vector("t.png");
    var options = new FormatCreateOptions { FormatSpecific = new() { ["key"] = "GameSpecific" } };
    var encoded = Run((i, o) => d.Compress(i, o, options), plain);
    Assert.That(encoded, Is.Not.EqualTo(Vector("cwb-t.png.utage")));
    Assert.That(Run((i, o) => UtageFormatDescriptor.Apply(i, o, "GameSpecific"u8), encoded), Is.EqualTo(plain));
  }

  [Test, Category("EdgeCase")]
  public void Given_EmptyKey_When_Encoded_Then_Refused() {
    var options = new FormatCreateOptions { FormatSpecific = new() { ["key"] = "" } };
    Assert.Throws<ArgumentException>(() => Run((i, o) => new UtageFormatDescriptor().Compress(i, o, options), [1, 2, 3]));
  }

  [Test, Category("EdgeCase")]
  public void Given_EmptyInput_When_Encoded_Then_Empty()
    => Assert.That(Run(new UtageFormatDescriptor().Compress, []), Is.Empty);

  [Test, Category("HappyPath")]
  public void Given_UtagePng_When_DetectedByMagic_Then_Utage()
    => Assert.That(Compression.Lib.FormatDetector.DetectByMagic(Vector("cwb-t.png.utage")).ToString(), Is.EqualTo("Utage"));
}
