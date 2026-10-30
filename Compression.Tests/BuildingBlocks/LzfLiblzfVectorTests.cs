using System.Buffers.Binary;
using System.Reflection;
using Compression.Core.Dictionary.Lzf;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// LZF against liblzf 3.6, which defines it. The codec used to be a private variant: its
/// back-references copied one byte fewer than liblzf's, so liblzf rejected everything we wrote
/// and we misread everything it wrote. The vectors are liblzf's own output (see
/// LzfVectors/README.md). We must restore them, and our compressor must reproduce them byte
/// for byte.
/// </summary>
[TestFixture]
public sealed class LzfLiblzfVectorTests {

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
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"LzfVectors.{name}")
      ?? throw new InvalidOperationException($"Missing resource LzfVectors.{name}");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  [TestCase("text", 20000, TestName = "GivenText_WhenComparedWithLiblzf_ThenBothDirectionsAgree")]
  [TestCase("text", 70000, TestName = "GivenTextBeyondTheHashRange_WhenComparedWithLiblzf_ThenBothDirectionsAgree")]
  [TestCase("mixed", 8000, TestName = "GivenTextWithRandomRuns_WhenComparedWithLiblzf_ThenBothDirectionsAgree")]
  [TestCase("zeros", 5000, TestName = "GivenZeros_WhenComparedWithLiblzf_ThenTheLongBackReferencesAgree")]
  [TestCase("random", 3000, TestName = "GivenRandomBytes_WhenComparedWithLiblzf_ThenTheLiteralRunsAgree")]
  [TestCase("text", 1, TestName = "GivenOneByte_WhenComparedWithLiblzf_ThenBothDirectionsAgree")]
  [TestCase("text", 2, TestName = "GivenTwoBytes_WhenComparedWithLiblzf_ThenBothDirectionsAgree")]
  [TestCase("text", 3, TestName = "GivenThreeBytes_WhenComparedWithLiblzf_ThenBothDirectionsAgree")]
  [Category("HappyPath")]
  public void AgreesWithLiblzf(string kind, int length) {
    var data = Generate(kind, length);
    var liblzf = Vector($"{kind}-{length}.lzf");
    Assert.Multiple(() => {
      Assert.That(LzfDecompressor.Decompress(liblzf, length), Is.EqualTo(data), "decoding liblzf's stream");
      Assert.That(LzfCompressor.Compress(data), Is.EqualTo(liblzf), "our stream against liblzf's");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheBuildingBlock_WhenItCompresses_ThenItIsALengthAndLiblzfsStream() {
    var data = Generate("text", 20000);
    var block = new LzfBuildingBlock().Compress(data);
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadInt32LittleEndian(block), Is.EqualTo(data.Length));
      Assert.That(block[4..], Is.EqualTo(Vector("text-20000.lzf")));
    });
  }

  [TestCase("01", 2, TestName = "GivenALiteralRunPastTheInput_WhenDecoded_ThenItIsTruncated")]
  [TestCase("0041" + "20", 5, TestName = "GivenABackReferenceWithoutItsOffset_WhenDecoded_ThenItIsTruncated")]
  [TestCase("0041" + "2001", 4, TestName = "GivenABackReferenceBeforeTheStart_WhenDecoded_ThenItIsRefused")]
  [TestCase("0041" + "2000", 2, TestName = "GivenABackReferencePastTheOutput_WhenDecoded_ThenItIsRefused")]
  [TestCase("0041", 2, TestName = "GivenAStreamShorterThanItsLength_WhenDecoded_ThenItIsRefused")]
  [Category("ExceptionalCase")]
  public void Malformed(string hex, int length)
    => Assert.Throws<InvalidDataException>(() => LzfDecompressor.Decompress(Convert.FromHexString(hex), length));
}
