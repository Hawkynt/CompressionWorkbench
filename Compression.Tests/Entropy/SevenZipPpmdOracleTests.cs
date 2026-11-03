using System.Reflection;
using System.Security.Cryptography;
using Compression.Core.Entropy.Ppmd;

namespace Compression.Tests.Entropy;

/// <summary>
/// The 7z PPMd coder (03 04 01) against 7-Zip. A stream 7-Zip wrote decodes byte-exact, and the
/// encoder reproduces pyppmd's output, 7-Zip's own Ppmd7 encoder, byte for byte (see
/// Entropy/PpmdVectors/README.md).
/// </summary>
[TestFixture]
public sealed class SevenZipPpmdOracleTests {

  private static readonly byte[][] Words = [.. new[] {
    "the ", "quick ", "brown ", "fox ", "jumps ", "over ", "lazy ", "dog ", "PPMd ", "model ", "context ", "7-Zip ", "\n",
  }.Select(w => System.Text.Encoding.ASCII.GetBytes(w))];

  // Mirrors gen() in the script that produced the oracle hashes: xorshift32 seeded from the length.
  private static byte[] Generate(string kind, int length) {
    var x = 0x9E3779B9u ^ (uint)length;
    uint Next() {
      x ^= x << 13;
      x ^= x >> 17;
      x ^= x << 5;
      return x;
    }
    var output = new List<byte>(length + 64);
    while (output.Count < length) {
      switch (kind) {
        case "random":
          output.Add((byte)Next());
          break;
        case "text":
          output.AddRange(Words[Next() % (uint)Words.Length]);
          break;
        default:
          if (Next() % 8 == 0)
            for (var i = 0; i < 64; ++i) output.Add((byte)Next());
          else
            output.AddRange(Words[Next() % (uint)Words.Length]);
          break;
      }
    }
    return [.. output.Take(length)];
  }

  private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCase("text", 0, 6, 16777216u, false, "e3b0c44298fc1c14", 5, "8855508aade16ec573d21e6a485dfd0a7624085c1a14b5ecdd6485de0c6839a4", TestName = "GivenNoInput_WhenEncoded_ThenItIsPyppmdsFlush")]
  [TestCase("text", 1, 6, 16777216u, false, "e3b98a4da31a127d", 6, "ad45421f214f9201617297368074fdec1c3ec6c2a8b2ce47128b037326f2dc79", TestName = "GivenOneByte_WhenEncoded_ThenItIsPyppmdsStream")]
  [TestCase("text", 100000, 6, 16777216u, false, "255735edeaf78d4f", 9581, "a2d614e4b94148b1cc65680b703271b405228b081569679603dc34b46d72e722", TestName = "GivenTextAtSevenZipsDefaults_WhenEncoded_ThenItIsPyppmdsStream")]
  [TestCase("text", 100000, 2, 1048576u, false, "255735edeaf78d4f", 9411, "15f1ebec9a6b390a0e585ee601210304a3d7b0f95e08c2e1f1efaf92714b33c6", TestName = "GivenTheLowestOrder_WhenEncoded_ThenItIsPyppmdsStream")]
  [TestCase("text", 100000, 64, 2048u, false, "255735edeaf78d4f", 78064, "82ee3cf6e0fd555ac93a3558bef43f311ddc67513d50f1d0358562d830d08e0e", TestName = "GivenTheHighestOrderInTheLeastMemory_WhenEncoded_ThenTheRestartsMatchPyppmd")]
  [TestCase("text", 300000, 16, 65536u, false, "6f5b500280b0d9a0", 46439, "d09beb0f71250d9b78cc76a840e9df8ef36498a9aeb53ed1d0bdd5bad2ff60e0", TestName = "GivenAModelThatFillsItsMemory_WhenEncoded_ThenItIsPyppmdsStream")]
  [TestCase("mixed", 120000, 8, 262144u, false, "63b3069e20748014", 86897, "46f05be4a1e36dd487b2d18d680d919392aff969361f7e117d25cd6d04bfd8af", TestName = "GivenTextWithRandomRuns_WhenEncoded_ThenItIsPyppmdsStream")]
  [TestCase("random", 20000, 6, 16777216u, false, "e82d495402f2d50c", 20441, "44bd080017e8d1f6e83748ca8bdf468ce6abdefad254c98bc1c9c523bdf594c0", TestName = "GivenRandomBytes_WhenEncoded_ThenItIsPyppmdsStream")]
  [TestCase("text", 50000, 6, 16777216u, true, "0934915c2c552653", 4859, "1a521bca269d4ff5def382bb7247cb672322845ffdd626e5eb577e5ab0b4e219", TestName = "GivenAnEndMarker_WhenEncoded_ThenItIsPyppmdsStream")]
  [Category("HappyPath")]
  public void EncoderMatchesPyppmdAndDecodes(string kind, int length, int order, uint memory, bool endMarker, string inputSha, int streamLength, string streamSha) {
    var data = Generate(kind, length);
    Assume.That(Sha256(data), Does.StartWith(inputSha), "the test input differs from the oracle's");
    var stream = SevenZipPpmd.Encode(data, order, memory, endMarker);
    Assert.Multiple(() => {
      Assert.That(stream, Has.Length.EqualTo(streamLength));
      Assert.That(Sha256(stream), Is.EqualTo(streamSha));
      Assert.That(SevenZipPpmd.Decode(SevenZipPpmd.Properties(order, memory), stream, length), Is.EqualTo(data));
    });
  }

  private static byte[] SevenZipArchive() {
    using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("PpmdVectors.libarchive-7zip-ppmd.7z")
      ?? throw new InvalidOperationException("Missing resource PpmdVectors.libarchive-7zip-ppmd.7z");
    using var copy = new MemoryStream();
    resource.CopyTo(copy);
    return copy.ToArray();
  }

  [Test, Category("HappyPath")]
  public void GivenTheStreamSevenZipWrote_WhenDecoded_ThenItIsTheFile() {
    var packed = SevenZipArchive().AsSpan(32, 10280).ToArray();
    var decoded = SevenZipPpmd.Decode(SevenZipPpmd.Properties(6, 1u << 24), packed, 102400);
    Assert.That(Sha256(decoded), Is.EqualTo("e587c5d5bdacd7376e5bccbbbd22cd971f4e51cee60a0ffc9737a596d3792136"));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenTheStreamSevenZipWroteCutShort_WhenDecoded_ThenItIsRefused() {
    var packed = SevenZipArchive().AsSpan(32, 5000).ToArray();
    Assert.Throws<InvalidDataException>(() => SevenZipPpmd.Decode(SevenZipPpmd.Properties(6, 1u << 24), packed, 102400));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAStreamNotStartingWithZero_WhenDecoded_ThenItIsRefused() {
    var stream = SevenZipPpmd.Encode("abc"u8, 6, 1u << 20);
    stream[0] = 1;
    Assert.Throws<InvalidDataException>(() => SevenZipPpmd.Decode(SevenZipPpmd.Properties(6, 1u << 20), stream, 3));
  }

  [Test, Category("ExceptionalCase")]
  public void GivenAnEndMarkerBeforeTheUnpackSize_WhenDecoded_ThenItIsRefused() {
    var stream = SevenZipPpmd.Encode("abc"u8, 6, 1u << 20, endMarker: true);
    Assert.Multiple(() => {
      Assert.That(SevenZipPpmd.Decode(SevenZipPpmd.Properties(6, 1u << 20), stream, 3), Is.EqualTo("abc"u8.ToArray()));
      Assert.Throws<InvalidDataException>(() => SevenZipPpmd.Decode(SevenZipPpmd.Properties(6, 1u << 20), stream, 4));
    });
  }

  [TestCase(new byte[] { 6, 0, 0, 0, 1 }, TestName = "GivenFiveValidPropertyBytes_WhenParsed_ThenTheyAreOrderAndMemory")]
  [Category("BoundaryCase")]
  public void ParsesProperties(byte[] props)
    => Assert.That(SevenZipPpmd.ParseProperties(props), Is.EqualTo((6, 1u << 24)));

  [TestCase(new byte[] { 6, 0, 0, 0 }, TestName = "GivenFourPropertyBytes_WhenParsed_ThenTheyAreRefused")]
  [TestCase(new byte[] { 1, 0, 0, 0, 1 }, TestName = "GivenOrderOne_WhenParsed_ThenItIsRefused")]
  [TestCase(new byte[] { 65, 0, 0, 0, 1 }, TestName = "GivenOrder65_WhenParsed_ThenItIsRefused")]
  [TestCase(new byte[] { 6, 0xFF, 0x07, 0, 0 }, TestName = "GivenMemoryBelowTwoKiB_WhenParsed_ThenItIsRefused")]
  [Category("ExceptionalCase")]
  public void RefusesProperties(byte[] props)
    => Assert.Throws<InvalidDataException>(() => SevenZipPpmd.ParseProperties(props));

  [Test, Category("BoundaryCase")]
  public void GivenTheLimits_WhenPropertiesAreBuilt_ThenTheyRoundTrip() {
    Assert.Multiple(() => {
      Assert.That(SevenZipPpmd.ParseProperties(SevenZipPpmd.Properties(2, 1u << 11)), Is.EqualTo((2, 1u << 11)));
      Assert.That(SevenZipPpmd.ParseProperties(SevenZipPpmd.Properties(64, 1u << 11)), Is.EqualTo((64, 1u << 11)));
      Assert.Throws<ArgumentOutOfRangeException>(() => SevenZipPpmd.Properties(6, (1u << 11) - 1));
    });
  }
}
