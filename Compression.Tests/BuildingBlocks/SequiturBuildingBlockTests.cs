using System.Text;
using Compression.Core.Dictionary.Sequitur;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class SequiturBuildingBlockTests {

  private static readonly SequiturBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x42];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[2048];
    Array.Fill(data, (byte)0x99);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Alternating_RoundTrips() {
    var data = new byte[1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2 == 0 ? 0xC3 : 0x3C);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0x9BEEF);
    var data = new byte[512];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  /// <summary>
  /// Ordinary prose has real but irregular word-level recurrence, not the
  /// short (period 1-2) internal periodicity that lets Sequitur's digram
  /// matching collapse a block into a compact doubling hierarchy (see
  /// <see cref="HeavyRepeatedSubstrings_RoundTripsAndCompressesWell"/> and the
  /// "compression is input-shaped" remarks on <see cref="SequiturCompressor"/>).
  /// This is a round-trip test, not a compression guarantee: raw grammar
  /// serialisation with no follow-on entropy coding is not expected to beat
  /// the input on general text.
  /// </summary>
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

  /// <summary>
  /// Heavily repeated substrings are exactly where grammar inference is meant
  /// to pay off: a short repeating unit ("ab") folds into a compact doubling
  /// hierarchy of two-symbol rules (R0="ab", R1=R0R0="abab", R2=R1R1, ...)
  /// instead of storing every repetition literally, and the ratio keeps
  /// improving as the input grows — unlike a longer unit with no short
  /// internal periodicity, which only achieves near-linear grammar size (see
  /// the "compression is input-shaped" remarks on <see cref="SequiturCompressor"/>).
  /// </summary>
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HeavyRepeatedSubstrings_RoundTripsAndCompressesWell() {
    var unit = Encoding.ASCII.GetBytes("ab");
    var data = new byte[unit.Length * 500];
    for (var i = 0; i < 500; ++i)
      unit.CopyTo(data, i * unit.Length);

    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);

    var ratio = (double)compressed.Length / data.Length;
    TestContext.Out.WriteLine($"Sequitur heavy-repeat ratio: {compressed.Length}/{data.Length} = {ratio:P2}");
    Assert.That(compressed.Length, Is.LessThan(data.Length / 10));
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Sequitur"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Sequitur"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
