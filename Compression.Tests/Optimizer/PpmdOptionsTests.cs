using System.Text;
using Compression.Core.Dictionary.Ppm;
using Compression.Core.Entropy.Ppmd;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Ppmd;

namespace Compression.Tests.Optimizer;

/// <summary>
/// The standalone PPMd format exposes the PPMd-H model order as a finite search
/// axis. Parameterized writes must honour that order, every candidate must still
/// round-trip, and the generic compression optimizer must keep the smallest one.
/// </summary>
[TestFixture]
public class PpmdOptionsTests {

  private static byte[] CompressibleSample() {
    var paragraph = Encoding.ASCII.GetBytes(
      "Prediction by partial matching chooses the longest useful context, escapes when it must, " +
      "and learns repeated structure. CompressionWorkbench should search context orders because " +
      "the best depth depends on the actual input rather than on a universal maximum.\n");
    var data = new byte[paragraph.Length * 48];
    for (var i = 0; i < 48; ++i)
      paragraph.CopyTo(data.AsSpan(i * paragraph.Length));
    return data;
  }

  private static byte[] Compress(PpmdFormatDescriptor descriptor, byte[] data, int order) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new(StringComparer.OrdinalIgnoreCase) {
        ["Order"] = order.ToString(System.Globalization.CultureInfo.InvariantCulture),
      },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(PpmdFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  [Test, Category("Spec")]
  public void Schema_ExposesCompleteDocumentedOrderRange() {
    var descriptor = new PpmdFormatDescriptor();
    var option = descriptor.OptionsSchema.Single();

    Assert.Multiple(() => {
      Assert.That(option.Key, Is.EqualTo("Order"));
      Assert.That(option.Default, Is.EqualTo(PpmdBuildingBlock.DefaultOrder.ToString()));
      Assert.That(option.AllowedValues, Has.Count.EqualTo(31));
      Assert.That(option.AllowedValues, Does.Contain("2"));
      Assert.That(option.AllowedValues, Does.Contain("32"));
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
      Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
    });
  }

  [Test, Category("RoundTrip")]
  public void DifferentOrders_AreWrittenAndRoundTrip() {
    var descriptor = new PpmdFormatDescriptor();
    var data = CompressibleSample();

    var order2 = Compress(descriptor, data, 2);
    var order12 = Compress(descriptor, data, 12);

    Assert.Multiple(() => {
      Assert.That(order2[4], Is.EqualTo(1), "standalone wrapper version");
      Assert.That(order2[5], Is.EqualTo(2), "PPMd payload order");
      Assert.That(order12[5], Is.EqualTo(12), "PPMd payload order");
      Assert.That(Decompress(descriptor, order2), Is.EqualTo(data));
      Assert.That(Decompress(descriptor, order12), Is.EqualTo(data));
    });
  }

  [Test, Category("Optimizer")]
  public void Optimizer_SearchesOrders_AndReturnsSmallestRoundTrip() {
    var descriptor = new PpmdFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.Multiple(() => {
      Assert.That(result.Parameters, Does.ContainKey("Order"));
      Assert.That(result.Probes, Is.EqualTo(31));
      Assert.That(result.Ratio, Is.LessThan(1.0));
      Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));
    });

    var winningOrder = int.Parse(result.Parameters["Order"], System.Globalization.CultureInfo.InvariantCulture);
    var replay = Compress(descriptor, data, winningOrder);
    Assert.That(result.Bytes, Is.EqualTo(replay));
  }

  [Test, Category("Regression"), Category("RoundTrip")]
  public void LegacyOrder3PpmPayload_RemainsReadable() {
    var data = CompressibleSample();
    var legacyPayload = new PpmBuildingBlock().Compress(data);
    using var stream = new MemoryStream();
    stream.Write([0x8F, 0xAF, 0xAC, 0x84]);
    stream.Write(legacyPayload);

    var descriptor = new PpmdFormatDescriptor();
    Assert.That(Decompress(descriptor, stream.ToArray()), Is.EqualTo(data));
  }

  [TestCase("1")]
  [TestCase("33")]
  [TestCase("not-an-order")]
  [Category("Validation")]
  public void InvalidOrder_IsRejected(string value) {
    var descriptor = new PpmdFormatDescriptor();
    using var input = new MemoryStream([1, 2, 3]);
    using var output = new MemoryStream();
    var options = new FormatCreateOptions {
      FormatSpecific = new(StringComparer.OrdinalIgnoreCase) { ["Order"] = value },
    };

    Assert.That(() => descriptor.Compress(input, output, options), Throws.ArgumentException);
  }
}
