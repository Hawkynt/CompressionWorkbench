using System.Buffers.Binary;
using System.Reflection;
using Compression.Core.Dictionary.Lzav;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// LZAV against LZAV 5.20 itself. The block used to leave out the format's offset-carry bits
/// and its final nine literals, so LZAV rejected our text, zero and short streams, and we
/// misread its offsets. The vectors are LZAV's own output (see LzavVectors/README.md): we must
/// restore them, and our compressor must reproduce them byte for byte.
/// </summary>
[TestFixture]
public sealed class LzavReferenceVectorTests {

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
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"LzavVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource LzavVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  [TestCase("text", 20000, TestName = "GivenText_WhenComparedWithLzav_ThenBothDirectionsAgree")]
  [TestCase("text", 70000, TestName = "GivenLongerText_WhenComparedWithLzav_ThenTheOffsetCarriesAgree")]
  [TestCase("mixed", 8000, TestName = "GivenTextWithRandomRuns_WhenComparedWithLzav_ThenBothDirectionsAgree")]
  [TestCase("zeros", 5000, TestName = "GivenZeros_WhenComparedWithLzav_ThenTheLongReferenceAgrees")]
  [TestCase("random", 3000, TestName = "GivenRandomBytes_WhenComparedWithLzav_ThenTheLiteralsAgree")]
  [TestCase("text", 1, TestName = "GivenOneByte_WhenComparedWithLzav_ThenTheStreamIsPaddedToElevenBytes")]
  [TestCase("text", 8, TestName = "GivenEightBytes_WhenComparedWithLzav_ThenTheStreamIsPaddedToElevenBytes")]
  [TestCase("text", 9, TestName = "GivenNineBytes_WhenComparedWithLzav_ThenTheyAreTheFinalLiterals")]
  [TestCase("text", 15, TestName = "GivenFifteenBytes_WhenComparedWithLzav_ThenTheyAreOneLiteralBlock")]
  [TestCase("text", 16, TestName = "GivenSixteenBytes_WhenComparedWithLzav_ThenTheCompressorRuns")]
  [TestCase("text", 300000, TestName = "GivenTextAbove256KiB_WhenComparedWithLzav_ThenTheMinimumReferenceIsSix")]
  [TestCase("zeros", 300000, TestName = "GivenManyZeros_WhenComparedWithLzav_ThenTheVarintLengthsAgree")]
  [Category("HappyPath")]
  public void AgreesWithLzav(string kind, int length) {
    var data = Generate(kind, length);
    var reference = Vector($"{kind}-{length}.lzav");
    Assert.Multiple(() => {
      Assert.That(LzavCodec.Decompress(reference, length), Is.EqualTo(data), "decoding LZAV's stream");
      Assert.That(LzavCodec.Compress(data), Is.EqualTo(reference), "our stream against LZAV's");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheBuildingBlock_WhenItCompresses_ThenItIsALengthAndLzavsStream() {
    var data = Generate("text", 20000);
    var block = new LzavBuildingBlock().Compress(data);
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(block), Is.EqualTo(data.Length));
      Assert.That(block[4..], Is.EqualTo(Vector("text-20000.lzav")));
      Assert.That(new LzavBuildingBlock().Decompress(block), Is.EqualTo(data));
    });
  }

  [Test, Category("BoundaryCase")]
  public void GivenNoBytes_WhenTheBuildingBlockRoundTrips_ThenItIsEmpty() {
    var block = new LzavBuildingBlock().Compress([]);
    Assert.Multiple(() => {
      Assert.That(block, Has.Length.EqualTo(4));
      Assert.That(new LzavBuildingBlock().Decompress(block), Is.Empty);
    });
  }

  [TestCase("35", 1, TestName = "GivenAStreamShorterThanEleven_WhenDecoded_ThenItIsRefused")]
  [TestCase("25" + "0141" + "0000000000000000", 1, TestName = "GivenFormat2_WhenDecoded_ThenItIsRefused")]
  [TestCase("30" + "0141" + "0000000000000000", 1, TestName = "GivenMinimumReferenceZero_WhenDecoded_ThenItIsRefused")]
  [TestCase("35" + "0141" + "0000000000000000", 2, TestName = "GivenAStreamShorterThanItsLength_WhenDecoded_ThenItIsRefused")]
  [Category("ExceptionalCase")]
  public void Malformed(string hex, int length)
    => Assert.Throws<InvalidDataException>(() => LzavCodec.Decompress(Convert.FromHexString(hex), length));

  [Test, Category("ExceptionalCase")]
  public void GivenAReferenceBeforeTheStart_WhenDecoded_ThenItIsRefused() {
    // A literal block of one byte, then a 10-bit reference of distance 9 into nothing.
    var stream = Convert.FromHexString("35" + "0141" + "5102" + "000000000000000000");
    Assert.Throws<InvalidDataException>(() => LzavCodec.Decompress(stream, 30));
  }
}
