using Compression.Lib;
using Compression.Registry;
using FileFormat.BriefLz;

namespace Compression.Tests.Optimizer;

/// <summary>
/// BriefLZ exposes ten decoder-compatible managed encoder effort levels. The
/// generic optimizer must search all of them and retain the smallest complete
/// blzpack stream for the actual payload.
/// </summary>
[TestFixture]
public class BriefLzOptionsTests {

  [Test, Category("Spec")]
  public void Levels_RoundTrip_AndSchemaCoversReferenceRange() {
    var descriptor = new BriefLzFormatDescriptor();
    var data = CompressibleSample();
    var levels = descriptor.OptionsSchema.Single(option => option.Key == "Level").AllowedValues!;

    Assert.That(levels, Is.EqualTo(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10" }));

    foreach (var level in levels) {
      var compressed = CompressAt(descriptor, data, level);
      Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data), $"level {level} must round-trip");
    }
  }

  [Test, Category("Spec")]
  public void Optimizer_FindsSmallestLevel_AndResultRoundTrips() {
    var descriptor = new BriefLzFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.That(result.Parameters, Does.ContainKey("Level"));
    Assert.That(result.Probes, Is.EqualTo(10), "the ten-level search space is small enough to exhaust");
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));

    foreach (var level in descriptor.OptionsSchema.Single(option => option.Key == "Level").AllowedValues!) {
      var candidate = CompressAt(descriptor, data, level);
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(candidate.Length),
        $"optimizer result must be no larger than level {level}");
    }
  }

  [Test, Category("Spec")]
  public void CompressOptimal_IsNoLargerThanAnyManagedLevel() {
    var descriptor = new BriefLzFormatDescriptor();
    var data = CompressibleSample();

    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    descriptor.CompressOptimal(input, output);
    var optimal = output.ToArray();

    Assert.That(Decompress(descriptor, optimal), Is.EqualTo(data));
    foreach (var level in descriptor.OptionsSchema.Single(option => option.Key == "Level").AllowedValues!)
      Assert.That(optimal.Length, Is.LessThanOrEqualTo(CompressAt(descriptor, data, level).Length));
  }

  private static byte[] CompressibleSample() {
    var data = new byte[8192];
    var phrases = new[] {
      "brief lz optimizer ",
      "compression workbench ",
      "the quick brown fox ",
      "decoder compatible "
    };

    var position = 0;
    for (var i = 0; position < data.Length; ++i) {
      var phrase = System.Text.Encoding.ASCII.GetBytes(phrases[(i * 7 + i / 3) % phrases.Length]);
      var length = Math.Min(phrase.Length, data.Length - position);
      phrase.AsSpan(0, length).CopyTo(data.AsSpan(position));
      position += length;
    }

    // Repeat an earlier region at several distances so deeper candidate
    // searches have meaningful alternatives instead of ten identical probes.
    data.AsSpan(512, 768).CopyTo(data.AsSpan(4096, 768));
    data.AsSpan(128, 384).CopyTo(data.AsSpan(6656, 384));
    return data;
  }

  private static byte[] CompressAt(BriefLzFormatDescriptor descriptor, byte[] data, string level) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Level"] = level },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(BriefLzFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }
}
