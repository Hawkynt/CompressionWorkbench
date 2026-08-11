using System.Text;
using Compression.Core.Dictionary.Sequitur;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class SequiturBuildingBlockTests {

  private static readonly SequiturBuildingBlock Bb = new();

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

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    Assert.That(Convert.ToHexString(compressed), Is.EqualTo("00000000"));
    Assert.That(Bb.Decompress(compressed), Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x42];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  /// <summary>
  /// A run of one byte is the shortest possible period, so the grammar becomes
  /// a doubling hierarchy: R0 = "aa", R1 = R0 R0, and so on. Twenty kilobytes
  /// need about fourteen rules in total.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleRepeatedByte_CompressesToAlmostNothing() {
    var data = Repeat(0x5A, 20480);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    TestContext.Out.WriteLine($"Sequitur repeated byte: {compressed.Length}/{data.Length}");
    Assert.That(compressed.Length, Is.LessThan(64));
  }

  /// <summary>
  /// The point of grammar inference: a long phrase repeated many times collapses
  /// to a rule for the phrase, then rules for pairs and quadruples of that rule,
  /// leaving a start sequence of a handful of symbols. The length of the
  /// repeated phrase does not matter — only that it repeats.
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RepeatedSentence_CompressesToUnderOnePercent() {
    var data = Repeat("the quick brown fox jumps over the lazy dog. ", 2000);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    TestContext.Out.WriteLine($"Sequitur repeated sentence: {compressed.Length}/{data.Length}");
    Assert.That(compressed.Length, Is.LessThan(data.Length / 100));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Alternating_RoundTripsAndCompresses() {
    var data = new byte[1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2 == 0 ? 0xC3 : 0x3C);
    var compressed = Bb.Compress(data);
    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 10));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0x9BEEF);
    var data = new byte[4096];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(
      "In the beginning the universe was created. This has made a lot of people very angry " +
      "and been widely regarded as a bad move. Many races believe that it was created by some " +
      "sort of god, though the Jatravartid people of Viltvodle Six believe that the entire " +
      "universe was in fact sneezed out of the nose of a being called the Great Green " +
      "Arkleseizure.");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void AllByteValues_RoundTrips() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
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
  /// in the Cipher project, so these vectors pin the grammar the two invariants
  /// produce as well as its bit-level layout.
  /// </summary>
  [Test, Category("HappyPath")]
  public void KnownVectors_MatchWireFormat() {
    (byte[] Data, string Hex)[] vectors = [
      (Encoding.ASCII.GetBytes("A"), "0100000000A080"),
      (Repeat(0x61, 4), "0400000001A3098680"),
      (Repeat(0x61, 256), "0001000007FEA66AAEF3377B8C261880"),
      (Repeat("ab", 32), "4000000005FA99AABBCC3098A200"),
      (Repeat("the quick brown fox jumps over the lazy dog. ", 4),
       "B400000004704731D0D065106E713A9A4C66B10188E46F3B9B84066379E0406A3A9B4E073101BCEC6539736184F47910190DE67170824B6E1000"),
      (Convert.FromHexString("D3B07A1C8F4E2B6905C1FD3846A70E92"), "10000000000869D83D0E47A715B482E0FE9C2353874900"),
    ];

    Assert.Multiple(() => {
      foreach (var (data, hex) in vectors) {
        Assert.That(Convert.ToHexString(Bb.Compress(data)), Is.EqualTo(hex));
        Assert.That(Bb.Decompress(Convert.FromHexString(hex)), Is.EqualTo(data).AsCollection);
      }
    });
  }

  [Test, Category("ExceptionalCase")]
  public void TruncatedHeader_Throws() =>
    Assert.Throws<InvalidDataException>(() => Bb.Decompress([0x01, 0x00]));

  [Test, Category("ExceptionalCase")]
  public void ReferenceToMissingRule_Throws() =>
    // One byte of payload declaring no rules but a start symbol of 0xFF, then a
    // length that the grammar cannot fill.
    Assert.Throws<InvalidDataException>(() => Bb.Decompress([0x10, 0x00, 0x00, 0x00, 0x00, 0xA0, 0x80]));

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Sequitur"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Sequitur"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
