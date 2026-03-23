using System.IO;
using System.Text;
using Compression.Core.Dictionary.Lz78;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class Lz78Tests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyData() {
    var compressor = new Lz78Compressor();
    var tokens = compressor.Compress(ReadOnlySpan<byte>.Empty);
    var result = Lz78Decompressor.Decompress(tokens);

    Assert.That(result, Is.EqualTo(Array.Empty<byte>()));
    Assert.That(tokens, Is.Empty);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    var compressor = new Lz78Compressor();
    byte[] data = [0xAB];

    var tokens = compressor.Compress(data);
    var result = Lz78Decompressor.Decompress(tokens);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    var compressor = new Lz78Compressor();
    var data = Encoding.ASCII.GetBytes("AAAAAAA");

    var tokens = compressor.Compress(data);
    var result = Lz78Decompressor.Decompress(tokens);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var compressor = new Lz78Compressor();
    var random = new Random(42);
    var data = new byte[1024];
    random.NextBytes(data);

    var tokens = compressor.Compress(data);
    var result = Lz78Decompressor.Decompress(tokens);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_HighlyRepetitive() {
    var compressor = new Lz78Compressor();
    var pattern = Encoding.ASCII.GetBytes("ABCABC");
    var data = new byte[4096];
    for (var i = 0; i < data.Length; ++i)
      data[i] = pattern[i % pattern.Length];

    var tokens = compressor.Compress(data);
    var result = Lz78Decompressor.Decompress(tokens);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_TextData() {
    var compressor = new Lz78Compressor();
    var data = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog");

    var tokens = compressor.Compress(data);
    var result = Lz78Decompressor.Decompress(tokens);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Compress_BuildsDictionary() {
    // Input: "ABAB"
    // Step 1: current=0, byte='A' -> miss -> emit (0,'A'), add entry 1="A"
    // Step 2: current=0, byte='B' -> miss -> emit (0,'B'), add entry 2="B"
    // Step 3: current=0, byte='A' -> hit entry 1, current=1
    // Step 4: current=1, byte='B' -> miss -> emit (1,'B'), add entry 3="AB"
    // Result: [(0,'A'), (0,'B'), (1,'B')]
    var compressor = new Lz78Compressor();
    var data = Encoding.ASCII.GetBytes("ABAB");

    var tokens = compressor.Compress(data);

    Assert.That(tokens.Count, Is.EqualTo(3));

    Assert.That(tokens[0].DictionaryIndex, Is.EqualTo(0));
    Assert.That(tokens[0].NextByte, Is.EqualTo((byte)'A'));

    Assert.That(tokens[1].DictionaryIndex, Is.EqualTo(0));
    Assert.That(tokens[1].NextByte, Is.EqualTo((byte)'B'));

    Assert.That(tokens[2].DictionaryIndex, Is.EqualTo(1));
    Assert.That(tokens[2].NextByte, Is.EqualTo((byte)'B'));
  }

  [Category("Exception")]
  [Test]
  public void Decompress_InvalidIndex_Throws() {
    var tokens = new List<Lz78Token> {
      new(0, (byte)'A'),
      new(99, (byte)'B'), // invalid index
    };

    Assert.That(
      () => Lz78Decompressor.Decompress(tokens),
      Throws.TypeOf<InvalidDataException>());
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_DictionaryReset() {
    // Use maxBits=9 so the dictionary resets at 512 entries.
    // Feed enough data to force at least one reset.
    const int maxBits = 9;
    var compressor = new Lz78Compressor(maxBits);
    var random = new Random(123);
    var data = new byte[4096];
    random.NextBytes(data);

    var tokens = compressor.Compress(data);
    var result = Lz78Decompressor.Decompress(tokens, maxBits);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_TerminalToken() {
    // Input: "AA"
    // Step 1: current=0, byte='A' -> miss -> emit (0,'A'), add entry 1="A"
    // Step 2: current=0, byte='A' -> hit entry 1, current=1
    // End of input: current=1 > 0 -> emit terminal (1, null)
    // Result: [(0,'A'), (1,null)]
    var compressor = new Lz78Compressor();
    var data = Encoding.ASCII.GetBytes("AA");

    var tokens = compressor.Compress(data);

    // Verify last token is terminal.
    var lastToken = tokens[^1];
    Assert.That(lastToken.NextByte, Is.Null);
    Assert.That(lastToken.DictionaryIndex, Is.GreaterThan(0));

    // Verify round-trip correctness.
    var result = Lz78Decompressor.Decompress(tokens);
    Assert.That(result, Is.EqualTo(data));
  }
}
