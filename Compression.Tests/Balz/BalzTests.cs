using FileFormat.Balz;
namespace Compression.Tests.Balz;

[TestFixture]
public class BalzTests {

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(256)]
  [TestCase(4096)]
  [TestCase(65536)]
  public void RoundTrip(int size) {
    var data = new byte[size];
    Random.Shared.NextBytes(data);
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    BalzStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    BalzStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  /// <summary>
  /// This payload used to desynchronize the coder: on a small enough range the
  /// scaled probability truncates to zero and the split point landed one below
  /// low, so a zero bit set high under low. One literal came back wrong 43020
  /// symbols in, and the next match pointed at an empty slot.
  /// </summary>
  [Test]
  public void RoundTrip_PayloadThatCollapsedTheRange() {
    var data = new byte[65536];
    new Random(3138).NextBytes(data);
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    BalzStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    BalzStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_Empty() {
    var data = Array.Empty<byte>();
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    BalzStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    BalzStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Repetitive() {
    var data = new byte[10000];
    var pattern = "The quick brown fox jumps over the lazy dog. "u8;
    for (var i = 0; i < data.Length; i++) data[i] = pattern[i % pattern.Length];
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    BalzStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    BalzStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Header_IsBigEndianSize() {
    var data = new byte[1000];
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    BalzStream.Compress(input, compressed);
    compressed.Position = 0;
    var b0 = compressed.ReadByte();
    var b1 = compressed.ReadByte();
    var b2 = compressed.ReadByte();
    var b3 = compressed.ReadByte();
    var size = (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
    Assert.That(size, Is.EqualTo(1000));
  }

  /// <summary>
  /// The look-ahead parser must produce ordinary BALZ tokens; the decoder has no
  /// special optimal-mode path and therefore verifies that the optimized parse
  /// stays inside the existing bitstream grammar.
  /// </summary>
  [Test, Category("HappyPath")]
  public void CompressOptimal_RoundTrips() {
    var data = new byte[16384];
    new Random(0xBA12).NextBytes(data);

    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    BalzStream.CompressOptimal(input, compressed);
    compressed.Position = 0;

    using var decompressed = new MemoryStream();
    BalzStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  /// <summary>
  /// Optimal mode evaluates both the existing greedy parse and the flexible
  /// look-ahead parse, so it is a hard invariant that requesting optimization
  /// cannot increase the final stream size.
  /// </summary>
  [Test, Category("Compression")]
  public void CompressOptimal_IsNeverLargerThanGreedy() {
    var random = new byte[4096];
    new Random(0xBA12).NextBytes(random);

    var repetitive = new byte[4096];
    var pattern = "BALZ optimizer regression corpus. "u8;
    for (var i = 0; i < repetitive.Length; i++) repetitive[i] = pattern[i % pattern.Length];

    byte[][] payloads = [
      [],
      "adbacdaddbacdadddaddbacddaddbacdadddaddbacddabdcdaddbacdaddddadb"u8.ToArray(),
      repetitive,
      random,
    ];

    foreach (var data in payloads) {
      var greedy = Compress(data, optimal: false);
      var optimal = Compress(data, optimal: true);
      Assert.That(optimal.Length, Is.LessThanOrEqualTo(greedy.Length), $"payload length {data.Length}");
    }
  }

  /// <summary>
  /// Greedy consumes the longest current match in this corpus and loses the
  /// better match that starts inside it. Flexible parsing shortens that token,
  /// exposing the later match and producing a genuinely smaller stream.
  /// </summary>
  [Test, Category("Compression")]
  public void CompressOptimal_FindsSmallerLazyParse() {
    var data = "adbacdaddbacdadddaddbacddaddbacdadddaddbacddabdcdaddbacdaddddadb"u8.ToArray();

    var greedy = Compress(data, optimal: false);
    var optimal = Compress(data, optimal: true);

    Assert.That(optimal.Length, Is.LessThan(greedy.Length));

    using var compressed = new MemoryStream(optimal);
    using var decompressed = new MemoryStream();
    BalzStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  private static byte[] Compress(byte[] data, bool optimal) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    if (optimal)
      BalzStream.CompressOptimal(input, output);
    else
      BalzStream.Compress(input, output);
    return output.ToArray();
  }
}
