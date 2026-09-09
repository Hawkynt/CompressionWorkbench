using Compression.Lib;
using Compression.Registry;
using FileFormat.Mcm;

namespace Compression.Tests.Optimizer;

/// <summary>
/// MCM exposes its managed compression modes through the generic optimizer
/// schema. Every candidate is self-described in the MCM block metadata and must
/// therefore round-trip independently of which mode wins the size search.
/// </summary>
[TestFixture]
[Category("Slow")]
public class McmOptionsTests {
  private static readonly string[] ModeNames = ["Legacy", "Turbo", "Fast", "Mid", "High", "Max"];

  private static byte[] CompressibleSample() {
    using var output = new MemoryStream();
    var phrases = new[] {
      "MCM mixes local contexts with progressively wider contexts.\n",
      "compression workbench optimizer chooses the smallest candidate.\n",
      "context mixing rewards repeated structure but profiles cost memory.\n",
    };

    for (var i = 0; i < 48; ++i) {
      var bytes = System.Text.Encoding.ASCII.GetBytes(phrases[i % phrases.Length]);
      output.Write(bytes);
    }

    return output.ToArray();
  }

  private static byte[] CompressAt(McmFormatDescriptor descriptor, byte[] data, string mode) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Mode"] = mode },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(McmFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  [Test, Category("Spec")]
  public void Mcm_ExposesSearchableModesAndOptimizeCapability() {
    var descriptor = new McmFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
      Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
      var option = descriptor.OptionsSchema.Single(o => o.Key == "Mode");
      Assert.That(option.Default, Is.EqualTo("Legacy"));
      Assert.That(option.AllowedValues, Is.EqualTo(ModeNames));
    });
  }

  [Test, Category("Spec")]
  public void AllModes_AreSelfDescribingAndRoundTrip() {
    var descriptor = new McmFormatDescriptor();
    var data = CompressibleSample();

    foreach (var mode in ModeNames) {
      var compressed = CompressAt(descriptor, data, mode);
      Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data), $"{mode} must round-trip");

      if (mode == "Legacy") {
        Assert.Multiple(() => {
          Assert.That(compressed[15], Is.Zero, "legacy algorithm id remains byte-compatible");
          Assert.That(compressed[18], Is.Zero, "legacy profile id remains byte-compatible");
        });
      } else {
        Assert.Multiple(() => {
          Assert.That(compressed[15], Is.EqualTo(0x80), $"{mode} uses the reduced managed algorithm id");
          Assert.That(compressed[18], Is.EqualTo((byte)Enum.Parse<McmCompressionMode>(mode)), $"{mode} records its profile id");
        });
      }
    }
  }

  [Test, Category("Regression")]
  public void DefaultCompression_RemainsLegacyByteForByte() {
    var descriptor = new McmFormatDescriptor();
    var data = CompressibleSample();

    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output);

    Assert.That(output.ToArray(), Is.EqualTo(CompressAt(descriptor, data, "Legacy")));
  }

  [Test, Category("Spec")]
  public void CompressOptimal_UsesMaxManagedProfile() {
    var descriptor = new McmFormatDescriptor();
    var data = CompressibleSample();

    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.CompressOptimal(input, output);
    var compressed = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(compressed[15], Is.EqualTo(0x80));
      Assert.That(compressed[18], Is.EqualTo((byte)McmCompressionMode.Max));
      Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data));
    });
  }

  [Test, Category("Optimizer")]
  public void Optimizer_FindsSmallestMode_AndResultRoundTrips() {
    var descriptor = new McmFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.Multiple(() => {
      Assert.That(result.Parameters, Does.ContainKey("Mode"));
      Assert.That(ModeNames, Does.Contain(result.Parameters["Mode"]));
      Assert.That(result.OriginalSize, Is.EqualTo(data.LongLength));
      Assert.That(result.Probes, Is.EqualTo(ModeNames.Length));
      Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));
    });

    foreach (var mode in ModeNames)
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(CompressAt(descriptor, data, mode).LongLength),
        $"optimizer result must be no larger than {mode}");
  }
}
