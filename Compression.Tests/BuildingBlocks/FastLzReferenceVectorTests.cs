using System.Buffers.Binary;
using System.Reflection;
using Compression.Core.Dictionary.FastLz;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// FastLZ against Ariya Hidayat's library, which defines it. The codec used to be a private
/// variant. It agreed with FastLZ on literal runs only, so FastLZ rejected our text and zero
/// blocks and we crashed on its. The vectors are FastLZ's own output (see
/// FastLzVectors/README.md): we must restore them, and our compressor must reproduce them byte
/// for byte at the same level.
/// </summary>
[TestFixture]
public sealed class FastLzReferenceVectorTests {

  private static readonly byte[][] Words = [.. new[] {
    "the ", "quick ", "brown ", "fox ", "jumps ", "over ", "lazy ", "dog ", "PPMd ", "model ", "context ", "7-Zip ", "\n",
  }.Select(w => System.Text.Encoding.ASCII.GetBytes(w))];

  /// <summary>The generator the vectors were made from (xorshift32 seeded with the length).</summary>
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
        case "random": output.Add((byte)Next()); break;
        case "text": output.AddRange(Words[Next() % (uint)Words.Length]); break;
        case "zeros": output.Add(0); break;
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

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"FastLzVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource FastLzVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  [TestCase("text", 20000, 1, TestName = "GivenText_WhenComparedWithFastLzLevel1_ThenBothDirectionsAgree")]
  [TestCase("text", 70000, 1, TestName = "GivenTextBeyondTheWindow_WhenComparedWithFastLzLevel1_ThenBothDirectionsAgree")]
  [TestCase("mixed", 8000, 1, TestName = "GivenTextWithRandomRuns_WhenComparedWithFastLzLevel1_ThenBothDirectionsAgree")]
  [TestCase("zeros", 5000, 1, TestName = "GivenZeros_WhenComparedWithFastLzLevel1_ThenTheSplitMatchesAgree")]
  [TestCase("zeros", 80000, 1, TestName = "GivenManyZeros_WhenComparedWithFastLzLevel1_ThenTheSplitMatchesAgree")]
  [TestCase("random", 3000, 1, TestName = "GivenRandomBytes_WhenComparedWithFastLzLevel1_ThenTheLiteralRunsAgree")]
  [TestCase("text", 1, 1, TestName = "GivenOneByte_WhenComparedWithFastLzLevel1_ThenBothDirectionsAgree")]
  [TestCase("text", 2, 1, TestName = "GivenTwoBytes_WhenComparedWithFastLzLevel1_ThenBothDirectionsAgree")]
  [TestCase("text", 3, 1, TestName = "GivenThreeBytes_WhenComparedWithFastLzLevel1_ThenBothDirectionsAgree")]
  [TestCase("text", 100000, 2, TestName = "GivenText_WhenComparedWithFastLzLevel2_ThenTheFarDistancesAgree")]
  [TestCase("zeros", 80000, 2, TestName = "GivenManyZeros_WhenComparedWithFastLzLevel2_ThenTheLongLengthsAgree")]
  [TestCase("mixed", 70000, 2, TestName = "GivenTextWithRandomRuns_WhenComparedWithFastLzLevel2_ThenBothDirectionsAgree")]
  [TestCase("text", 20000, 2, TestName = "GivenShortText_WhenComparedWithFastLzLevel2_ThenBothDirectionsAgree")]
  [TestCase("zeros", 5000, 2, TestName = "GivenZeros_WhenComparedWithFastLzLevel2_ThenBothDirectionsAgree")]
  [TestCase("text", 1, 2, TestName = "GivenOneByte_WhenComparedWithFastLzLevel2_ThenTheLevelBitIsSet")]
  [Category("HappyPath")]
  public void AgreesWithFastLz(string kind, int length, int level) {
    var data = Generate(kind, length);
    var reference = Vector($"{kind}-{length}.level{level}.flz");
    Assert.Multiple(() => {
      Assert.That(FastLzDecompressor.Decompress(reference, length), Is.EqualTo(data), "decoding FastLZ's block");
      Assert.That(FastLzCompressor.Compress(data, level), Is.EqualTo(reference), "our block against FastLZ's");
    });
  }

  [TestCase("text", 20000, 1, TestName = "GivenLessThan64KiB_WhenTheBlockCompresses_ThenItIsFastLzLevel1")]
  [TestCase("text", 100000, 2, TestName = "GivenAtLeast64KiB_WhenTheBlockCompresses_ThenItIsFastLzLevel2")]
  [Category("HappyPath")]
  public void TheBlockChoosesTheLevelAsFastLzDoes(string kind, int length, int level) {
    var data = Generate(kind, length);
    var block = new FastLzBuildingBlock().Compress(data);
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(block), Is.EqualTo(length));
      Assert.That(block[4..], Is.EqualTo(Vector($"{kind}-{length}.level{level}.flz")));
    });
  }

  [TestCase("40", 1, TestName = "GivenLevel3_WhenDecoded_ThenItIsRefused")]
  [TestCase("01", 2, TestName = "GivenALiteralRunPastTheInput_WhenDecoded_ThenItIsTruncated")]
  [TestCase("0041" + "2001", 4, TestName = "GivenAMatchBeforeTheStart_WhenDecoded_ThenItIsRefused")]
  [TestCase("0041" + "2000", 2, TestName = "GivenAMatchPastTheOutput_WhenDecoded_ThenItIsRefused")]
  [TestCase("0041", 2, TestName = "GivenABlockShorterThanItsLength_WhenDecoded_ThenItIsRefused")]
  [Category("ExceptionalCase")]
  public void Malformed(string hex, int length)
    => Assert.Throws<InvalidDataException>(() => FastLzDecompressor.Decompress(Convert.FromHexString(hex), length));

  [Test, Category("ExceptionalCase")]
  public void GivenLevel3_WhenCompressing_ThenItIsRejected()
    => Assert.Throws<ArgumentOutOfRangeException>(() => FastLzCompressor.Compress([1, 2, 3], 3));
}
