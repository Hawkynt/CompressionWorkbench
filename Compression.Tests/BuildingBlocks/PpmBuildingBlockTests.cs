using System.Text;
using Compression.Core.Dictionary.Ppm;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class PpmBuildingBlockTests {

  private static readonly PpmBuildingBlock Bb = new();

  private static byte[] Repeat(byte value, int count) {
    var data = new byte[count];
    Array.Fill(data, value);
    return data;
  }

  private static byte[] Repeat(string text, int count) {
    var unit = Encoding.ASCII.GetBytes(text);
    var data = new byte[unit.Length * count];
    for (var i = 0; i < count; ++i)
      unit.CopyTo(data, i * unit.Length);
    return data;
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    Assert.That(Convert.ToHexString(compressed), Is.EqualTo("0300000000"));
    Assert.That(Bb.Decompress(compressed), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x42];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void AllByteValues_RoundTrip() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void Alternating_RoundTrips() {
    var data = new byte[1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2 == 0 ? 0x00 : 0xFF);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0x9BEEF);
    var data = new byte[4096];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  /// <summary>
  /// Twenty kilobytes of one byte value is the easiest input there is: after
  /// the first byte every context predicts the next one with certainty, and an
  /// arithmetic coder spends almost nothing on a certainty.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleRepeatedByte_CompressesToAlmostNothing() {
    var data = Repeat(0x5A, 20480);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    TestContext.Out.WriteLine($"PPM repeated byte: {compressed.Length}/{data.Length}");
    Assert.That(compressed.Length, Is.LessThan(64));
  }

  /// <summary>
  /// Ninety kilobytes of one repeated sentence. The order-3 contexts become
  /// deterministic within the first couple of repetitions, so the whole of the
  /// rest costs a fraction of a bit per byte.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatedSentence_CompressesToUnderOnePercent() {
    var data = Repeat("the quick brown fox jumps over the lazy dog. ", 2000);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    TestContext.Out.WriteLine($"PPM repeated sentence: {compressed.Length}/{data.Length}");
    Assert.That(compressed.Length, Is.LessThan(data.Length / 100));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTripsAndCompresses() {
    var data = Encoding.ASCII.GetBytes(
      "In the beginning the universe was created. This has made a lot of people very angry " +
      "and been widely regarded as a bad move. Many races believe that it was created by some " +
      "sort of god, though the Jatravartid people of Viltvodle Six believe that the entire " +
      "universe was in fact sneezed out of the nose of a being called the Great Green " +
      "Arkleseizure.");
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void OneMegabyte_RoundTrips() {
    var data = Repeat("Lorem ipsum dolor sit amet, consectetur adipiscing elit. ", 18725);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 10));
  }

  /// <summary>
  /// The wire format is shared byte-for-byte with the JavaScript implementation
  /// in the Cipher project, so these vectors pin the header, the escape and
  /// exclusion decisions, and the arithmetic coder's flush.
  /// </summary>
  [TestCase("", "0300000000", TestName = "Vector_Empty")]
  [TestCase("A", "03010000004140", TestName = "Vector_SingleByte")]
  [TestCase("AA", "03020000004120", TestName = "Vector_TwoIdenticalBytes")]
  [TestCase("ABABABABABABABAB", "031000000041A0A090", TestName = "Vector_AlternatingPair")]
  [TestCase(
    "the quick brown fox jumps over the lazy dog. the quick brown fox jumps over the lazy dog. ",
    "035A00000074B48DF0B3D1CB7EEDFAB1DD59C3CD5E412CAA40DAAE22B0623D8562A01614B461ADF95DCDC03520B7975380000071D0",
    TestName = "Vector_RepeatedSentence")]
  [Category("HappyPath")]
  public void KnownVectors_MatchWireFormat(string text, string expectedHex) {
    var data = Encoding.ASCII.GetBytes(text);
    var compressed = Bb.Compress(data);
    Assert.Multiple(() => {
      Assert.That(Convert.ToHexString(compressed), Is.EqualTo(expectedHex));
      Assert.That(Bb.Decompress(Convert.FromHexString(expectedHex)), Is.EqualTo(data).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void KnownVector_RepeatedByteRun() {
    var data = Repeat(0x61, 64);
    Assert.Multiple(() => {
      Assert.That(Convert.ToHexString(Bb.Compress(data)), Is.EqualTo("0340000000610040"));
      Assert.That(Bb.Decompress(Convert.FromHexString("0340000000610040")), Is.EqualTo(data).AsCollection);
    });
  }

  [Test, Category("ExceptionalCase")]
  public void TruncatedHeader_Throws() =>
    Assert.Throws<InvalidDataException>(() => Bb.Decompress([0x03, 0x00]));

  [Test, Category("ExceptionalCase")]
  public void ForeignOrder_Throws() =>
    Assert.Throws<InvalidDataException>(() => Bb.Decompress([0x09, 0x01, 0x00, 0x00, 0x00, 0x00]));

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_PPM"));
      Assert.That(Bb.DisplayName, Is.EqualTo("PPM"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.ContextMixing));
    });
  }
}
