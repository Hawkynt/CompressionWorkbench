using Compression.Lib;
using Compression.Registry;
using FileFormat.PackBits;

namespace Compression.Tests.PackBits;

[TestFixture]
public class PackBitsTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleText() {
    var data = "Hello, PackBits! This is a test of run-length encoding."u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      PackBitsStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    PackBitsStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++)
      data[i] = (byte)i;

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      PackBitsStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    PackBitsStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[2048];
    Array.Fill(data, (byte)'A');

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      PackBitsStream.Compress(input, compressed);

    // Highly repetitive data should compress well.
    Assert.That(compressed.Length, Is.LessThan(data.Length));

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    PackBitsStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AlternatingPattern() {
    var data = new byte[1024];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 2 == 0 ? 'A' : 'B');

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      PackBitsStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    PackBitsStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsPKBT() {
    var data = "test"u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      PackBitsStream.Compress(input, compressed);

    compressed.Position = 0;
    Assert.That(compressed.ReadByte(), Is.EqualTo((int)'P'));
    Assert.That(compressed.ReadByte(), Is.EqualTo((int)'K'));
    Assert.That(compressed.ReadByte(), Is.EqualTo((int)'B'));
    Assert.That(compressed.ReadByte(), Is.EqualTo((int)'T'));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_SingleByte() {
    var data = new byte[] { 0x42 };

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      PackBitsStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    PackBitsStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeData() {
    // 100 KB of mixed content: repeating pattern with some variation.
    var data = new byte[100 * 1024];
    var rng = new Random(12345);
    for (var i = 0; i < data.Length; i++) {
      // Mix runs and random bytes.
      if (i % 256 < 64)
        data[i] = 0xAA; // runs of identical bytes
      else
        data[i] = (byte)rng.Next(256);
    }

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      PackBitsStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    PackBitsStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("Optimization")]
  public void Descriptor_AdvertisesPacketizerOptimization() {
    var descriptor = new PackBitsFormatDescriptor();
    var packetizer = descriptor.OptionsSchema.Single(option => option.Key == "Packetizer");

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
      Assert.That(packetizer.Default, Is.EqualTo("Greedy"));
      Assert.That(packetizer.AllowedValues, Is.EqualTo(new[] { "Greedy", "Optimal" }));
    });
  }

  [Test, Category("Optimization"), Category("RoundTrip")]
  public void Optimizer_SelectsSmallerOptimalPacketization() {
    // 127 literal bytes followed by a two-byte run exposes the historical
    // greedy packetizer's boundary case: it fills a 128-byte literal with the
    // first repeated byte and leaves the second byte in a separate literal.
    var data = new byte[129];
    for (var i = 0; i < 127; ++i)
      data[i] = (byte)i;
    data[127] = data[128] = 0xFE;

    var descriptor = new PackBitsFormatDescriptor();
    var greedy = Compress(descriptor, data, "Greedy");
    var optimal = Compress(descriptor, data, "Optimal");
    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.Multiple(() => {
      Assert.That(greedy.Length, Is.EqualTo(139));
      Assert.That(optimal.Length, Is.EqualTo(138));
      Assert.That(result.Bytes, Is.EqualTo(optimal));
      Assert.That(result.Parameters["Packetizer"], Is.EqualTo("Optimal"));
      Assert.That(result.Probes, Is.EqualTo(2));
    });

    using var encoded = new MemoryStream(result.Bytes, writable: false);
    using var decoded = new MemoryStream();
    descriptor.Decompress(encoded, decoded);
    Assert.That(decoded.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("Optimization")]
  public void OptimalPacketizer_MatchesBruteForceMinimum_ForAllShortBinaryInputs() {
    var descriptor = new PackBitsFormatDescriptor();

    for (var length = 0; length <= 10; ++length) {
      var variants = 1 << length;
      for (var mask = 0; mask < variants; ++mask) {
        var data = new byte[length];
        for (var bit = 0; bit < length; ++bit)
          data[bit] = (byte)((mask >> bit) & 1);

        var encoded = Compress(descriptor, data, "Optimal");
        Assert.That(encoded.Length - 8, Is.EqualTo(MinimumPayloadSize(data)),
          $"length={length}, mask=0x{mask:X}");
      }
    }
  }

  private static byte[] Compress(PackBitsFormatDescriptor descriptor, byte[] data, string packetizer) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Packetizer"] = packetizer },
    });
    return output.ToArray();
  }

  private static int MinimumPayloadSize(ReadOnlySpan<byte> data) {
    var costs = new int[data.Length + 1];
    for (var i = data.Length - 1; i >= 0; --i) {
      var maxLength = Math.Min(128, data.Length - i);
      var best = int.MaxValue;

      for (var length = 1; length <= maxLength; ++length)
        best = Math.Min(best, 1 + length + costs[i + length]);

      var repeatLength = 1;
      while (repeatLength < maxLength && data[i + repeatLength] == data[i])
        ++repeatLength;
      for (var length = 2; length <= repeatLength; ++length)
        best = Math.Min(best, 2 + costs[i + length]);

      costs[i] = best;
    }

    return costs[0];
  }
}
