using System.Buffers.Binary;
using Compression.Lib;
using Compression.Registry;
using FileFormat.Bsc;

namespace Compression.Tests.Optimizer;

/// <summary>
/// BSC exposes the two format-level knobs the managed writer genuinely honours:
/// block size and following/preceding sorting contexts. The optimizer must search
/// both axes and return a representation that still round-trips.
/// </summary>
[TestFixture]
[Category("Slow")]
public class BscOptionsTests {
  private static byte[] CompressibleSample() {
    using var ms = new MemoryStream();
    var rng = new Random(7331);
    var phrases = new[] {
      "block sorting compression ",
      "the quick brown fox jumps over the lazy dog ",
      "contexts can face forwards or backwards ",
    };
    while (ms.Length < 24 * 1024) {
      var phrase = System.Text.Encoding.ASCII.GetBytes(phrases[rng.Next(phrases.Length)]);
      ms.Write(phrase);
    }
    return ms.ToArray();
  }

  private static byte[] CompressAt(BscFormatDescriptor descriptor, byte[] data, string blockSize, string contexts) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["BlockSize"] = blockSize,
        ["SortingContexts"] = contexts,
      },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(BscFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  [Test, Category("Spec")]
  public void Bsc_ExposesSearchableOptimizationAxes() {
    var descriptor = new BscFormatDescriptor();
    var schema = (IFormatOptionsSchema)descriptor;

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);

    var blockSize = schema.OptionsSchema.Single(option => option.Key == "BlockSize");
    Assert.That(blockSize.Default, Is.EqualTo("26214400"));
    Assert.That(blockSize.AllowedValues, Does.Contain("16384"));

    var contexts = schema.OptionsSchema.Single(option => option.Key == "SortingContexts");
    Assert.That(contexts.Default, Is.EqualTo("Following"));
    Assert.That(contexts.AllowedValues, Is.EqualTo(new[] { "Following", "Preceding" }));
  }

  [Test, Category("Spec")]
  public void Bsc_HonoursBlockSize_AndRoundTripsMultipleBlocks() {
    var descriptor = new BscFormatDescriptor();
    var data = CompressibleSample();

    var smallBlocks = CompressAt(descriptor, data, "16384", "Following");
    var defaultBlocks = CompressAt(descriptor, data, "26214400", "Following");

    Assert.That(BinaryPrimitives.ReadInt32LittleEndian(smallBlocks.AsSpan(4, 4)), Is.EqualTo(2));
    Assert.That(BinaryPrimitives.ReadInt32LittleEndian(defaultBlocks.AsSpan(4, 4)), Is.EqualTo(1));
    Assert.That(Decompress(descriptor, smallBlocks), Is.EqualTo(data));
    Assert.That(Decompress(descriptor, defaultBlocks), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void Bsc_HonoursSortingContexts_AndRoundTrips() {
    var descriptor = new BscFormatDescriptor();
    var data = CompressibleSample();

    var following = CompressAt(descriptor, data, "65536", "Following");
    var preceding = CompressAt(descriptor, data, "65536", "Preceding");

    // File header is 8 bytes; sortingContexts is byte 9 of the 10-byte BSC block header.
    Assert.That(following[17], Is.EqualTo(1));
    Assert.That(preceding[17], Is.EqualTo(2));
    Assert.That(Decompress(descriptor, following), Is.EqualTo(data));
    Assert.That(Decompress(descriptor, preceding), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void Optimizer_SearchesBscAxes_AndResultRoundTrips() {
    var descriptor = new BscFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.That(result.Parameters, Does.ContainKey("BlockSize"));
    Assert.That(result.Parameters, Does.ContainKey("SortingContexts"));
    Assert.That(result.Probes, Is.EqualTo(12), "6 block sizes × 2 context orders are searched exhaustively");
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));

    var atDefault = CompressAt(descriptor, data, "26214400", "Following");
    Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(atDefault.Length),
      "optimizer must never lose to BSC's default representation");
  }
}
