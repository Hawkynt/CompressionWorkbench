using System.Text;
using Compression.Core.Dictionary.Lzjb;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

[TestFixture]
public class LzjbTests {

  private static readonly LzjbBuildingBlock Bb = new();

  [Test, Category("HappyPath")]
  public void Empty_RoundTrips() {
    var compressed = Bb.Compress([]);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.Empty);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void SingleByte_RoundTrips() {
    var data = new byte[] { 0x2C };
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void HighlyRepetitive_RoundTripsAndCompresses() {
    var data = new byte[4096];
    Array.Fill<byte>(data, 0x61);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void IncompressibleRandom_RoundTrips() {
    var rng = new Random(0xD00D);
    var data = new byte[2048];
    rng.NextBytes(data);
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void TextSample_RoundTrips() {
    var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "Sphinx of black quartz, judge my vow. ", 20)));
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void MixedPattern_RoundTrips() {
    var rng = new Random(17);
    var parts = new List<byte>();
    for (var i = 0; i < 12; i++) {
      var block = new byte[300];
      if (i % 2 == 0) Array.Fill(block, (byte)i);
      else rng.NextBytes(block);
      parts.AddRange(block);
    }
    var data = parts.ToArray();
    var compressed = Bb.Compress(data);
    var round = Bb.Decompress(compressed);
    Assert.That(round, Is.EqualTo(data).AsCollection);
  }

  [Test, Category("Spec")]
  public void ReferenceVector_MatchTokenIsLengthHighDistanceLow() {
    // Given: copy-map 0x08 (item 3 is a match), "abc", then the match b0 = (9 - 3) << 2,
    //        b1 = distance 3, in the byte order ZFS writes.
    byte[] stream = [0x08, (byte)'a', (byte)'b', (byte)'c', 0x18, 0x03];
    // When
    var decoded = LzjbDecompressor.Decompress(stream, 12);
    // Then: the overlapping copy repeats the three bytes
    Assert.That(Encoding.ASCII.GetString(decoded), Is.EqualTo("abcabcabcabc"));
  }

  [Test, Category("Spec")]
  public void ReferenceVector_DistanceUsesTwoBitsOfFirstByte() {
    // Given: 300 literals, then a match of length 3 at distance 0x12C (b0 low bits = 1)
    var literals = Enumerable.Range(0, 300).Select(i => (byte)(i * 7 + 1)).ToArray();
    var stream = new List<byte>();
    for (var i = 0; i < 300; i += 8) {
      stream.Add(0);
      stream.AddRange(literals.Skip(i).Take(8));
    }
    // 300 = 37 * 8 + 4: the last group has room for the match as its fifth item
    stream[^5] = 1 << 4;
    stream.AddRange([0x01, 0x2C]);
    // When
    var decoded = LzjbDecompressor.Decompress(stream.ToArray(), 303);
    // Then
    Assert.That(decoded[300..], Is.EqualTo(literals[..3]).AsCollection);
  }

  [Test, Category("Spec")]
  public void Decompress_StopsInsideAMatchAtTheDeclaredLength() {
    byte[] stream = [0x02, (byte)'z', 0xFC, 0x01, 0, 0, 0];
    var decoded = LzjbDecompressor.Decompress(stream, 5);
    Assert.That(Encoding.ASCII.GetString(decoded), Is.EqualTo("zzzzz"));
  }

  [Test, Category("Spec")]
  public void Compress_EmitsTheLastSixtySixBytesAsLiterals() {
    var data = new byte[66];
    var compressed = Bb.Compress(data);
    // 4-byte size header, then 9 copy-map bytes for 66 literal items, then the literals
    Assert.That(compressed, Has.Length.EqualTo(4 + 9 + 66));
  }

  [Test, Category("ErrorHandling")]
  public void Decompress_DistanceBeforeOutputStart_Throws()
    => Assert.Throws<InvalidDataException>(() => LzjbDecompressor.Decompress([0x01, 0x00, 0x05], 3));

  [Test, Category("ErrorHandling")]
  public void Decompress_TruncatedStream_Throws()
    => Assert.Throws<InvalidDataException>(() => LzjbDecompressor.Decompress([0x00, 0x41], 2));

  [Test, Category("EdgeCase")]
  public void BbRegistry_ExposesMetadata() {
    Assert.That(Bb.Id, Is.EqualTo("BB_Lzjb"));
    Assert.That(Bb.DisplayName, Is.EqualTo("LZJB"));
    Assert.That(Bb.Family, Is.EqualTo(AlgorithmFamily.Dictionary));
  }
}
