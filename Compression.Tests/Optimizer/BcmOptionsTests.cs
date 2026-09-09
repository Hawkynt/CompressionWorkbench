using Compression.Lib;
using Compression.Registry;
using FileFormat.Bcm;

namespace Compression.Tests.Optimizer;

/// <summary>
/// BCM exposes its BWT block size through <see cref="IFormatOptionsSchema"/> so
/// <see cref="CompressionOptimizer"/> can choose the smallest valid encoding for
/// the caller's actual payload.
/// </summary>
[TestFixture]
[Category("Slow")]
public class BcmOptionsTests {

  private static byte[] CompressibleSample() {
    var data = new byte[24 * 1024];
    var phrase = "compression workbench bcm block-size optimizer test data\n"u8;
    for (var i = 0; i < data.Length; ++i)
      data[i] = phrase[i % phrase.Length];
    return data;
  }

  private static byte[] CompressAt(BcmFormatDescriptor descriptor, byte[] data, string blockSize) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["BlockSize"] = blockSize },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(BcmFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  [Test, Category("Spec")]
  public void Bcm_ExposesBlockSizeOption() {
    var descriptor = new BcmFormatDescriptor();

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    Assert.That(descriptor, Is.InstanceOf<IFormatOptionsSchema>());

    var option = descriptor.OptionsSchema.Single(option => option.Key == "BlockSize");
    Assert.Multiple(() => {
      Assert.That(option.Default, Is.EqualTo("64"));
      Assert.That(option.AllowedValues, Is.EqualTo(new[] { "16", "32", "64", "128" }));
      Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
    });
  }

  [Test, Category("Spec")]
  public void Bcm_HonoursBlockSizeOption_AndRoundTrips() {
    var descriptor = new BcmFormatDescriptor();
    var data = CompressibleSample();

    var smallBlocks = CompressAt(descriptor, data, "16");
    var largeBlocks = CompressAt(descriptor, data, "128");

    Assert.Multiple(() => {
      Assert.That(smallBlocks, Is.Not.EqualTo(largeBlocks), "different block partitioning must change the encoded stream");
      Assert.That(Decompress(descriptor, smallBlocks), Is.EqualTo(data), "16 KiB block output round-trips");
      Assert.That(Decompress(descriptor, largeBlocks), Is.EqualTo(data), "128 KiB block output round-trips");
    });
  }

  [Test, Category("Spec")]
  public void Optimizer_FindsSmallestCandidate_AndRoundTrips() {
    var descriptor = new BcmFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);
    var candidates = descriptor.OptionsSchema.Single().AllowedValues!;

    Assert.Multiple(() => {
      Assert.That(result.Parameters, Does.ContainKey("BlockSize"));
      Assert.That(result.Probes, Is.EqualTo(candidates.Count));
      Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
      Assert.That(result.Ratio, Is.LessThan(1.0), "repetitive sample should compress");
      Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data), "optimized output round-trips");
    });

    foreach (var blockSize in candidates)
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(CompressAt(descriptor, data, blockSize).Length),
        $"optimizer result must be <= {blockSize} KiB candidate");
  }
}
