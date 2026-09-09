using Compression.Lib;
using Compression.Registry;
using FileFormat.Density;

namespace Compression.Tests.Optimizer;

/// <summary>
/// Verifies that Density exposes its three algorithm tiers as a real optimizer
/// axis and that the generic stream optimizer returns the smallest valid stream.
/// </summary>
[TestFixture]
public class DensityOptionsTests {

  private static byte[] SamplePayload() {
    var pattern = "The quick brown fox jumps over the lazy dog. Density optimizer. "u8;
    var data = new byte[64_003];
    for (var i = 0; i < data.Length; ++i)
      data[i] = pattern[i % pattern.Length];
    return data;
  }

  private static byte[] Compress(DensityFormatDescriptor descriptor, byte[] input, string algorithm) {
    using var inMs = new MemoryStream(input, writable: false);
    using var outMs = new MemoryStream();
    descriptor.Compress(inMs, outMs, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Algorithm"] = algorithm },
    });
    return outMs.ToArray();
  }

  private static byte[] CompressDefault(DensityFormatDescriptor descriptor, byte[] input) {
    using var inMs = new MemoryStream(input, writable: false);
    using var outMs = new MemoryStream();
    descriptor.Compress(inMs, outMs);
    return outMs.ToArray();
  }

  private static byte[] Decompress(DensityFormatDescriptor descriptor, byte[] input) {
    using var inMs = new MemoryStream(input, writable: false);
    using var outMs = new MemoryStream();
    descriptor.Decompress(inMs, outMs);
    return outMs.ToArray();
  }

  [Test, Category("Spec")]
  public void AlgorithmOption_SelectsRequestedAlgorithm_AndRoundTrips() {
    var descriptor = new DensityFormatDescriptor();
    var input = SamplePayload();

    foreach (var algorithm in Enum.GetValues<DensityStream.Algorithm>()) {
      var compressed = Compress(descriptor, input, algorithm.ToString());

      Assert.Multiple(() => {
        Assert.That(compressed.Length, Is.GreaterThanOrEqualTo(10));
        Assert.That(compressed[5], Is.EqualTo((byte)algorithm),
          $"Density header must record {algorithm} when that algorithm is selected.");
        Assert.That(Decompress(descriptor, compressed), Is.EqualTo(input),
          $"{algorithm} output must round-trip losslessly.");
      });
    }
  }

  [Test, Category("Regression")]
  public void DefaultAlgorithm_RemainsCheetah() {
    var descriptor = new DensityFormatDescriptor();
    var input = SamplePayload();

    Assert.That(
      CompressDefault(descriptor, input),
      Is.EqualTo(Compress(descriptor, input, nameof(DensityStream.Algorithm.Cheetah))),
      "Publishing an optimizer axis must not change Density's existing default output.");
  }

  [Test, Category("Spec")]
  public void Optimizer_ProbesAllAlgorithms_AndReturnsSmallestRoundTrip() {
    var descriptor = new DensityFormatDescriptor();
    var input = SamplePayload();
    var direct = Enum.GetValues<DensityStream.Algorithm>()
      .ToDictionary(algorithm => algorithm, algorithm => Compress(descriptor, input, algorithm.ToString()));

    var result = CompressionOptimizer.OptimizeStream(input, descriptor, descriptor);

    Assert.That(result.Parameters, Does.ContainKey("Algorithm"));
    var winner = Enum.Parse<DensityStream.Algorithm>(result.Parameters["Algorithm"]);

    Assert.Multiple(() => {
      Assert.That(result.Probes, Is.EqualTo(direct.Count),
        "The finite one-axis Density search must probe Chameleon, Cheetah and Lion exactly once each.");
      Assert.That(result.CompressedSize, Is.EqualTo(direct.Values.Min(bytes => bytes.LongLength)),
        "The optimizer must return the smallest actual Density stream, not a hard-coded tier.");
      Assert.That(result.Bytes[5], Is.EqualTo((byte)winner),
        "The reported winning algorithm must match the algorithm recorded in the stream header.");
      Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(input),
        "The optimized Density stream must round-trip losslessly.");
    });
  }
}
