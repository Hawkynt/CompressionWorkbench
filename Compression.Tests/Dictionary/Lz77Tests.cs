using System.Text;
using Compression.Core.Dictionary.Lz77;
using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class Lz77Tests {
  [Test]
  public void RoundTrip_EmptyData() {
    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress([]);
    var result = Lz77Decompressor.Decompress(tokens);
    Assert.That(result, Is.Empty);
  }

  [Test]
  public void RoundTrip_SingleByte() {
    byte[] input = [0x42];
    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress(input);
    var result = Lz77Decompressor.Decompress(tokens);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void RoundTrip_RepetitiveData() {
    byte[] input = Encoding.ASCII.GetBytes("ABCABCABCABCABCABC");
    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress(input);
    var result = Lz77Decompressor.Decompress(tokens);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    byte[] input = new byte[1000];
    rng.NextBytes(input);

    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress(input);
    var result = Lz77Decompressor.Decompress(tokens);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void RoundTrip_HighlyRepetitive() {
    // Single byte repeated many times — should produce matches
    byte[] input = new byte[500];
    Array.Fill(input, (byte)0xAA);

    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress(input);
    var result = Lz77Decompressor.Decompress(tokens);
    Assert.That(result, Is.EqualTo(input));

    // Should have produced some match tokens (not all literals)
    Assert.That(tokens.Any(t => !t.IsLiteral), Is.True);
  }

  [Test]
  public void Compress_ProducesMatchTokens_ForRepeatedSequences() {
    byte[] input = Encoding.ASCII.GetBytes("ABCDEFABCDEF");
    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress(input);

    // Should have some match tokens
    bool hasMatch = tokens.Any(t => !t.IsLiteral);
    Assert.That(hasMatch, Is.True);
  }

  [Test]
  public void Decompress_InvalidBackReference_Throws() {
    var tokens = new List<Lz77Token> {
      Lz77Token.CreateMatch(5, 3) // Distance 5, but output is empty
    };

    Assert.Throws<InvalidDataException>(() => Lz77Decompressor.Decompress(tokens));
  }

  [Test]
  public void RoundTrip_TextData() {
    byte[] input = Encoding.ASCII.GetBytes(
      "The quick brown fox jumps over the lazy dog. " +
      "The quick brown fox jumps over the lazy dog.");

    var finder = new HashChainMatchFinder(32768);
    var compressor = new Lz77Compressor(finder);
    var tokens = compressor.Compress(input);
    var result = Lz77Decompressor.Decompress(tokens);
    Assert.That(result, Is.EqualTo(input));
  }
}
