using System.Text;
using Compression.Core.Dictionary.Shoco;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class ShocoBuildingBlockTests {

  private static readonly ShocoBuildingBlock Bb = new();

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Empty_RoundTrips() {
    var round = Bb.Decompress(Bb.Compress([]));
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    foreach (byte b in new byte[] { 0x00, (byte)'e', (byte)'Z', 0xFF }) {
      var round = Bb.Decompress(Bb.Compress([b]));
      Assert.That(round, Is.EqualTo(new[] { b }).AsCollection);
    }
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Repetitive_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(new string('e', 500));
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xC5);
    var data = new byte[2048];
    rng.NextBytes(data);
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void EnglishText_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog.");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void MixedCaseAndPunctuation_RoundTrips() {
    var data = Encoding.ASCII.GetBytes("Hello, World! 123 - Test.");
    var round = Bb.Decompress(Bb.Compress(data));
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void AlternatingPattern_RoundTrips() {
    var data = new byte[2048];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2 == 0 ? 0xA5 : 0x5A);
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

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RepeatedCommonWords_ExercisesAllPackTiers() {
    // Text drawn from the same vocabulary the model was trained on. The mix of
    // long, highly predictable runs ("the day and the night") and shorter,
    // less predictable joins between them exercises all three pack tiers.
    const string text = "the day and the night and the day and the night";
    var data = Encoding.ASCII.GetBytes(text);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);

    var sawPack0 = false;
    var sawPack1 = false;
    var sawPack2 = false;
    var payload = compressed.AsSpan(4);
    var i = 0;
    while (i < payload.Length) {
      var b = payload[i];
      if (b == 0x00) {
        i += 2;
      } else if (b < 0x80) {
        i += 1;
      } else if ((b & 0xC0) == 0x80) {
        sawPack0 = true;
        i += 1;
      } else if ((b & 0xE0) == 0xC0) {
        sawPack1 = true;
        i += 2;
      } else if ((b & 0xF0) == 0xE0) {
        sawPack2 = true;
        i += 4;
      } else {
        Assert.Fail($"Unrecognized pack header 0x{b:X2} at offset {i}.");
      }
    }

    Assert.Multiple(() => {
      Assert.That(sawPack0, Is.True, "expected the 1-byte/2-character pack tier to appear");
      Assert.That(sawPack1, Is.True, "expected the 2-byte/4-character pack tier to appear");
      Assert.That(sawPack2, Is.True, "expected the 4-byte/8-character pack tier to appear");
    });
  }

  [Test, Category("EdgeCase")]
  public void PlainAsciiText_DoesNotGrowMuch() {
    var data = Encoding.ASCII.GetBytes("the quick brown fox and the lazy dog");
    var compressed = Bb.Compress(data);
    // 4-byte length header plus payload should still be competitive for common text.
    Assert.That(compressed.Length, Is.LessThanOrEqualTo(data.Length + 4));
  }

  [Test, Category("EdgeCase")]
  public void Registry_Metadata_IsStable() {
    Assert.Multiple(() => {
      Assert.That(Bb.Id, Is.EqualTo("BB_Shoco"));
      Assert.That(Bb.DisplayName, Is.EqualTo("Shoco"));
      Assert.That(Bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    });
  }
}
