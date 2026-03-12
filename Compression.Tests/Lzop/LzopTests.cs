using System.Text;
using FileFormat.Lzop;

namespace Compression.Tests.Lzop;

[TestFixture]
public class LzopTests {
  [Test]
  public void RoundTrip_EmptyData() {
    byte[] input = [];
    var compressed = LzopWriter.Compress(input);
    var reader = new LzopReader(new MemoryStream(compressed));
    var result = reader.Decompress();
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void RoundTrip_SmallData() {
    var input = Encoding.ASCII.GetBytes("Hello, LZOP World!");
    var compressed = LzopWriter.Compress(input);
    var reader = new LzopReader(new MemoryStream(compressed));
    var result = reader.Decompress();
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void RoundTrip_RepetitiveData() {
    var input = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ABCDEFGHIJKLMNOP", 5000)));
    var compressed = LzopWriter.Compress(input);
    Assert.That(compressed.Length, Is.LessThan(input.Length), "Repetitive data should compress.");
    var reader = new LzopReader(new MemoryStream(compressed));
    var result = reader.Decompress();
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(12345);
    var input = new byte[4096];
    rng.NextBytes(input);
    var compressed = LzopWriter.Compress(input);
    var reader = new LzopReader(new MemoryStream(compressed));
    var result = reader.Decompress();
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void RoundTrip_LargeData_MultipleBlocks() {
    // Larger than one 256 KB block to exercise multi-block handling
    var pattern = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog. ");
    var input = new byte[pattern.Length * 8000]; // ~360 KB
    for (var i = 0; i < 8000; ++i)
      pattern.CopyTo(input, i * pattern.Length);

    var compressed = LzopWriter.Compress(input);
    var reader = new LzopReader(new MemoryStream(compressed));
    var result = reader.Decompress();
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void OriginalFileName_IsPreserved() {
    var input = Encoding.ASCII.GetBytes("file contents");
    var fileName = "hello.txt";
    var compressed = LzopWriter.Compress(input, fileName);
    var reader = new LzopReader(new MemoryStream(compressed));
    reader.Decompress();
    Assert.That(reader.OriginalFileName, Is.EqualTo(fileName));
  }

  [Test]
  public void OriginalFileName_NullWhenNotProvided() {
    var input = Encoding.ASCII.GetBytes("no filename here");
    var compressed = LzopWriter.Compress(input);
    var reader = new LzopReader(new MemoryStream(compressed));
    reader.Decompress();
    Assert.That(reader.OriginalFileName, Is.Null);
  }

  [Test]
  public void RoundTrip_ExactlyOneBlock() {
    // Exactly 256 KB (one full block)
    var input = new byte[256 * 1024];
    new Random(7).NextBytes(input);
    var compressed = LzopWriter.Compress(input);
    var reader = new LzopReader(new MemoryStream(compressed));
    var result = reader.Decompress();
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void InvalidMagic_Throws() {
    var bad = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
    var reader = new LzopReader(new MemoryStream(bad));
    Assert.Throws<InvalidDataException>(() => reader.Decompress());
  }
}
