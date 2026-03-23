using System.Text;
using Compression.Core.Dictionary.Lz77;
using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class Lz77Tests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyData() {
    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress([]);
    var result = Lz77Decompressor.Decompress(tokens);
    Assert.That(result, Is.Empty);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] input = [0x42];
    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress(input);
    var result = Lz77Decompressor.Decompress(tokens);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    var input = Encoding.ASCII.GetBytes("ABCABCABCABCABCABC");
    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress(input);
    var result = Lz77Decompressor.Decompress(tokens);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var input = new byte[1000];
    rng.NextBytes(input);

    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress(input);
    var result = Lz77Decompressor.Decompress(tokens);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_HighlyRepetitive() {
    // Single byte repeated many times — should produce matches
    var input = new byte[500];
    Array.Fill(input, (byte)0xAA);

    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress(input);
    var result = Lz77Decompressor.Decompress(tokens);
    Assert.That(result, Is.EqualTo(input));

    // Should have produced some match tokens (not all literals)
    Assert.That(tokens.Any(t => !t.IsLiteral), Is.True);
  }

  [Category("HappyPath")]
  [Test]
  public void Compress_ProducesMatchTokens_ForRepeatedSequences() {
    var input = Encoding.ASCII.GetBytes("ABCDEFABCDEF");
    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress(input);

    // Should have some match tokens
    var hasMatch = tokens.Any(t => !t.IsLiteral);
    Assert.That(hasMatch, Is.True);
  }

  [Category("Exception")]
  [Test]
  public void Decompress_InvalidBackReference_Throws() {
    var tokens = new List<Lz77Token> {
      Lz77Token.CreateMatch(5, 3) // Distance 5, but output is empty
    };

    Assert.Throws<InvalidDataException>(() => Lz77Decompressor.Decompress(tokens));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_TextData() {
    var input = Encoding.ASCII.GetBytes(
      "The quick brown fox jumps over the lazy dog. " +
      "The quick brown fox jumps over the lazy dog.");

    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress(input);
    var result = Lz77Decompressor.Decompress(tokens);
    Assert.That(result, Is.EqualTo(input));
  }
}
