using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Ppmd;

namespace Compression.Tests.Optimizer;

/// <summary>
/// The standalone PPMd format exposes the PPMd var.I model order as a finite search
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
      Assert.That(option.Default, Is.EqualTo(PpmdStream.DefaultOrder.ToString()));
      Assert.That(option.AllowedValues, Has.Count.EqualTo(15));
      Assert.That(option.AllowedValues, Does.Contain("2"));
      Assert.That(option.AllowedValues, Does.Contain("16"));
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
      Assert.That(order2[8] & 0x0F, Is.EqualTo(1), "header order - 1");
      Assert.That(order12[8] & 0x0F, Is.EqualTo(11), "header order - 1");
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
      Assert.That(result.Probes, Is.EqualTo(15));
      Assert.That(result.Ratio, Is.LessThan(1.0));
      Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));
    });

    var winningOrder = int.Parse(result.Parameters["Order"], System.Globalization.CultureInfo.InvariantCulture);
    var replay = Compress(descriptor, data, winningOrder);
    Assert.That(result.Bytes, Is.EqualTo(replay));
  }

  [TestCase("1")]
  [TestCase("17")]
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
