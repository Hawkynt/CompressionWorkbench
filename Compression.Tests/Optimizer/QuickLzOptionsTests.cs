using Compression.Core.Dictionary.QuickLz;
using Compression.Lib;
using Compression.Registry;
using FileFormat.QuickLz;

namespace Compression.Tests.Optimizer;

/// <summary>
/// QuickLZ 1.5.0 exposes level 1 vs. level 3 and the level-3 hash-candidate depth.
/// The generic optimizer must search those knobs, while the descriptor's direct
/// <c>CompressOptimal</c> path must cover the same finite candidate set.
/// </summary>
[TestFixture]
public sealed class QuickLzOptionsTests {

  [Test, Category("Spec")]
  public void QuickLz_HonoursLevelAndSearchDepth_AndRoundTrips() {
    var descriptor = new QuickLzFormatDescriptor();
    var data = OptimizerSample();

    var level1 = CompressAt(descriptor, data, QuickLzCompressionLevel.Level1, 16);
    var level3Fast = CompressAt(descriptor, data, QuickLzCompressionLevel.Level3, 1);
    var level3Max = CompressAt(descriptor, data, QuickLzCompressionLevel.Level3, 16);

    Assert.That(Level(level1), Is.EqualTo(1));
    Assert.That(Level(level3Fast), Is.EqualTo(3));
    Assert.That(Level(level3Max), Is.EqualTo(3));
    Assert.That(Decompress(descriptor, level1), Is.EqualTo(data));
    Assert.That(Decompress(descriptor, level3Fast), Is.EqualTo(data));
    Assert.That(Decompress(descriptor, level3Max), Is.EqualTo(data));
    Assert.That(level3Max.Length, Is.LessThan(level3Fast.Length),
      "the deterministic collision-heavy sample should benefit from the deeper level-3 search");
  }

  [Test, Category("Spec")]
  public void Optimizer_FindsSmallestDeclaredConfiguration_AndRoundTrips() {
    var descriptor = new QuickLzFormatDescriptor();
    var data = OptimizerSample();
    var defaultPacket = CompressAt(descriptor, data, QuickLzCompressionLevel.Level1, 16);

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.That(result.Parameters, Does.ContainKey("Level"));
    Assert.That(result.Parameters, Does.ContainKey("SearchDepth"));
    Assert.That(result.CompressedSize, Is.LessThan(defaultPacket.Length),
      "the sample is constructed so a level-3 search beats the historical level-1 default");
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));

    var levels = descriptor.OptionsSchema.Single(option => option.Key == "Level").AllowedValues!;
    var depths = descriptor.OptionsSchema.Single(option => option.Key == "SearchDepth").AllowedValues!;
    foreach (var level in levels)
      foreach (var depth in depths) {
        var candidate = CompressAt(descriptor, data,
          Enum.Parse<QuickLzCompressionLevel>(level), int.Parse(depth));
        Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(candidate.Length),
          $"optimizer result must be <= {level} with search depth {depth}");
      }
  }

  [Test, Category("Spec")]
  public void CompressOptimal_MatchesGenericOptimizerMinimum() {
    var descriptor = new QuickLzFormatDescriptor();
    var data = OptimizerSample();
    var generic = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    descriptor.CompressOptimal(input, output);
    var direct = output.ToArray();

    Assert.That(direct.Length, Is.EqualTo(generic.CompressedSize));
    Assert.That(Decompress(descriptor, direct), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Schema_PreservesLevel1Default_AndAdvertisesOptimize() {
    var descriptor = new QuickLzFormatDescriptor();

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    Assert.That(descriptor.OptionsSchema.Single(option => option.Key == "Level").Default,
      Is.EqualTo(nameof(QuickLzCompressionLevel.Level1)));
    Assert.That(descriptor.OptionsSchema.Single(option => option.Key == "SearchDepth").Default,
      Is.EqualTo(QuickLzCompressor.Level3MaxSearchDepth.ToString()));
  }

  private static byte[] OptimizerSample() {
    // Several short phrase families deliberately collide and recur. Level 1 remembers
    // one hash position; level 3 can retain older alternatives and recover longer matches.
    uint state = 1;
    var phrases = new byte[20][];
    for (var phraseIndex = 0; phraseIndex < phrases.Length; ++phraseIndex) {
      var length = 8 + (int)(Next(ref state) % 32);
      var phrase = new byte[length];
      for (var index = 0; index < phrase.Length; ++index)
        phrase[index] = (byte)('A' + (int)(Next(ref state) % 26));
      phrases[phraseIndex] = phrase;
    }

    using var data = new MemoryStream();
    for (var repetition = 0; repetition < 200; ++repetition) {
      var phraseIndex = (int)(Next(ref state) % (uint)phrases.Length);
      var phrase = phrases[phraseIndex].ToArray();
      if (Next(ref state) % 10 < 3) {
        var index = (int)(Next(ref state) % (uint)phrase.Length);
        phrase[index] = (byte)('A' + (int)(Next(ref state) % 26));
      }
      data.Write(phrase);
    }
    return data.ToArray();
  }

  private static uint Next(ref uint state) {
    state ^= state << 13;
    state ^= state >> 17;
    state ^= state << 5;
    return state;
  }

  private static byte[] CompressAt(QuickLzFormatDescriptor descriptor, byte[] data,
      QuickLzCompressionLevel level, int searchDepth) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["Level"] = level.ToString(),
        ["SearchDepth"] = searchDepth.ToString(),
      },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(QuickLzFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  private static int Level(byte[] packet) => (packet[0] >> 2) & 0x03;
}
