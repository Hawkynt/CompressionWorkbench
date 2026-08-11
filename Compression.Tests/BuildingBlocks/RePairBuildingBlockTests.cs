using System.Text;
using Compression.Core.Dictionary.RePair;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class RePairBuildingBlockTests {

  private static readonly RePairBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    byte[] data = [0x99];
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Repetitive_RoundTripsAndCompresses() {
    var data = new byte[8192];
    Array.Fill(data, (byte)0x37);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xC5);
    var data = new byte[4096];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTrips() {
    // The serialized sequence spends two bytes per symbol, so the grammar has to
    // earn its keep over a text long enough for the rules to pay for themselves.
    var data = Encoding.ASCII.GetBytes(string.Concat(
      Enumerable.Repeat("Re-Pair replaces the most frequent adjacent pair again and again. ", 64)));
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("EdgeCase")]
  public void NoRepeatedPair_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("abcdef");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void OverlappingRun_ReplacesNonOverlapping() {
    // "aaaaa" counts the pair (a,a) four times but can only fuse it twice, which
    // is the case where counting and substitution deliberately disagree.
    var data = Encoding.ASCII.GetBytes("aaaaa");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void TiesGoToTheEarliestPair() {
    // (a,b) and (c,d) both occur twice; the grammar must take the one that
    // appears first in the sequence, so rule 0 is (a,b) and not (c,d).
    var data = Encoding.ASCII.GetBytes("abXcdYabZcd");
    var compressed = Bb.Compress(data);

    var ruleCount = BitConverter.ToInt32(compressed, 4);
    Assert.That(ruleCount, Is.GreaterThanOrEqualTo(1));

    var firstLeft = BitConverter.ToUInt16(compressed, 8);
    var firstRight = BitConverter.ToUInt16(compressed, 10);
    Assert.Multiple(() => {
      Assert.That(firstLeft, Is.EqualTo((ushort)'a'));
      Assert.That(firstRight, Is.EqualTo((ushort)'b'));
    });

    Assert.That(Bb.Decompress(compressed), Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void AllByteValues_RoundTrip() {
    var data = new byte[256];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)i;
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_RePair"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Re-Pair"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
