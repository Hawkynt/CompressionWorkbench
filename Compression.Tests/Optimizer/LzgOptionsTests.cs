using Compression.Lib;
using Compression.Registry;
using FileFormat.Lzg;

namespace Compression.Tests.Optimizer;

/// <summary>
/// Verifies that LZG's encoder settings are real optimizer axes rather than
/// metadata-only knobs.
/// </summary>
[TestFixture]
public class LzgOptionsTests {

  private static byte[] Compress(LzgFormatDescriptor descriptor, byte[] input, int level, bool fast) {
    using var inMs = new MemoryStream(input, writable: false);
    using var outMs = new MemoryStream();
    descriptor.Compress(inMs, outMs, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["Level"] = level.ToString(),
        ["Fast"] = fast.ToString(),
      },
    });
    return outMs.ToArray();
  }

  private static byte[] CompressDefault(LzgFormatDescriptor descriptor, byte[] input) {
    using var inMs = new MemoryStream(input, writable: false);
    using var outMs = new MemoryStream();
    descriptor.Compress(inMs, outMs);
    return outMs.ToArray();
  }

  private static byte[] Decompress(LzgFormatDescriptor descriptor, byte[] input) {
    using var inMs = new MemoryStream(input, writable: false);
    using var outMs = new MemoryStream();
    descriptor.Decompress(inMs, outMs);
    return outMs.ToArray();
  }

  private static byte[] LongDistancePayload() {
    var random = new Random(0x4C5A47);
    var first = new byte[128];
    var gap = new byte[2_500];
    random.NextBytes(first);
    random.NextBytes(gap);

    return [.. first, .. gap, .. first];
  }

  private static byte[] FastLookupPayload() {
    var pattern = new byte[128];
    pattern[0] = (byte)'A';
    pattern[1] = (byte)'B';
    pattern[2] = (byte)'C';
    for (var i = 3; i < pattern.Length; ++i)
      pattern[i] = (byte)(0xC0 + (i & 0x1F));

    var data = new List<byte>(pattern.Length * 2 + 160);
    data.AddRange(pattern);

    // Forty newer "ABx" positions bury the original "ABC..." position beyond
    // level 1's 16-candidate budget for the two-byte lookup, while the
    // three-byte lookup goes straight to the useful candidate.
    for (var i = 0; i < 40; ++i) {
      data.Add((byte)'A');
      data.Add((byte)'B');
      data.Add((byte)(0x80 + i));
      data.Add((byte)i);
    }

    data.AddRange(pattern);
    return data.ToArray();
  }

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesFiniteLzgOptimizerAxes() {
    var descriptor = new LzgFormatDescriptor();
    var schema = descriptor.OptionsSchema.ToDictionary(option => option.Key);

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
      Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
      Assert.That(schema["Level"].AllowedValues, Is.EqualTo(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9" }));
      Assert.That(schema["Level"].Default, Is.EqualTo("5"));
      Assert.That(schema["Fast"].Default, Is.EqualTo("true"));
    });
  }

  [Test, Category("Regression")]
  public void DefaultEncoder_RemainsLevel5Fast() {
    var descriptor = new LzgFormatDescriptor();
    var input = LongDistancePayload();

    Assert.That(CompressDefault(descriptor, input), Is.EqualTo(Compress(descriptor, input, level: 5, fast: true)));
  }

  [Test, Category("Spec")]
  public void LevelOption_WidensMatchWindow() {
    var descriptor = new LzgFormatDescriptor();
    var input = LongDistancePayload();

    var level1 = Compress(descriptor, input, level: 1, fast: true);
    var level2 = Compress(descriptor, input, level: 2, fast: true);

    Assert.Multiple(() => {
      Assert.That(level2.Length, Is.LessThan(level1.Length),
        "The repeated 128-byte prefix is farther than level 1's 2 KiB window but inside level 2's 4 KiB window.");
      Assert.That(Decompress(descriptor, level1), Is.EqualTo(input));
      Assert.That(Decompress(descriptor, level2), Is.EqualTo(input));
    });
  }

  [Test, Category("Spec")]
  public void FastOption_UsesDifferentCandidateLookup() {
    var descriptor = new LzgFormatDescriptor();
    var input = FastLookupPayload();

    var fast = Compress(descriptor, input, level: 1, fast: true);
    var compact = Compress(descriptor, input, level: 1, fast: false);

    Assert.Multiple(() => {
      Assert.That(fast.Length, Is.LessThan(compact.Length),
        "The three-byte lookup must skip the deliberately injected two-byte-prefix collisions.");
      Assert.That(Decompress(descriptor, fast), Is.EqualTo(input));
      Assert.That(Decompress(descriptor, compact), Is.EqualTo(input));
    });
  }

  [Test, Category("Spec")]
  public void Optimizer_ProbesAllLevelAndLookupCombinations_AndReturnsSmallestRoundTrip() {
    var descriptor = new LzgFormatDescriptor();
    var input = LongDistancePayload();

    var direct = (
      from level in Enumerable.Range(1, 9)
      from fast in new[] { true, false }
      select Compress(descriptor, input, level, fast)
    ).ToArray();

    var result = CompressionOptimizer.OptimizeStream(input, descriptor, descriptor);

    Assert.Multiple(() => {
      Assert.That(result.Probes, Is.EqualTo(18),
        "The finite 9 x 2 LZG option space must be searched exhaustively.");
      Assert.That(result.CompressedSize, Is.EqualTo(direct.Min(bytes => bytes.LongLength)),
        "The optimizer must choose the smallest measured LZG stream.");
      Assert.That(result.Parameters, Does.ContainKey("Level"));
      Assert.That(result.Parameters, Does.ContainKey("Fast"));
      Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(input),
        "The optimized LZG stream must remain interoperable and lossless.");
    });
  }
}
