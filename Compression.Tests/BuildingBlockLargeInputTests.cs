using System.Diagnostics;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests;

/// <summary>
/// Round-trip tests at input sizes large enough to overflow a 32-bit quantity.
/// Marked [Explicit] — these allocate hundreds of megabytes and are not run by default.
/// </summary>
/// <remarks>
/// <para>
/// Run everything at the default size (2^28 + 1 bytes, just past the point where a
/// bit count overflows an <see cref="int"/>):
/// </para>
/// <code>dotnet test --filter "Category=LargeInput"</code>
/// <para>
/// Pick a different size, or restrict the run to named blocks, through environment
/// variables. <c>CW_LARGE_INPUT_BYTES</c> sets the input size in bytes and
/// <c>CW_LARGE_INPUT_BLOCKS</c> takes a comma-separated list of block ids:
/// </para>
/// <code>
/// set CW_LARGE_INPUT_BYTES=268435456
/// set CW_LARGE_INPUT_BLOCKS=BB_Deflate,BB_Lz4
/// dotnet test --filter "Category=LargeInput"
/// </code>
/// <para>
/// The ceiling is <see cref="Array.MaxLength"/> (2,147,483,591), which is 57 bytes
/// below 2^31. <see cref="IBuildingBlock.Compress"/> takes a
/// <see cref="ReadOnlySpan{T}"/> and returns a <see cref="byte"/> array, so no input
/// at or above 2^31 can reach a building block through this interface at all.
/// Blocks that expand — Unary and the Elias codes above all — hit that limit on their
/// <em>output</em> at a far smaller input; see <c>docs/large-inputs.md</c>.
/// </para>
/// </remarks>
[TestFixture]
[Explicit]
[Category("LargeInput")]
public class BuildingBlockLargeInputTests {

  /// <summary>
  /// Just past 2^28, where a bit count taken as <c>length * 8</c> stops fitting an
  /// <see cref="int"/>.
  /// </summary>
  private const int DefaultLargeSize = 268_435_457;

  /// <summary>Bytes of input used by the size-parameterised cases.</summary>
  private static int LargeSize
    => int.TryParse(Environment.GetEnvironmentVariable("CW_LARGE_INPUT_BYTES"), out var value) && value > 0
      ? value
      : DefaultLargeSize;

  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  /// <summary>
  /// Fills a buffer with repeating English text. Compressible, so most blocks produce
  /// an output far smaller than the input and the run stays inside memory.
  /// </summary>
  private static byte[] TextPattern(int size) {
    var text = "The quick brown fox jumps over the lazy dog. Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. "u8;
    var buffer = new byte[size];
    for (var i = 0; i < size; ++i)
      buffer[i] = text[i % text.Length];

    return buffer;
  }

  /// <summary>Fills a buffer with the byte values 0..255 in order, repeating.</summary>
  private static byte[] IncrementingPattern(int size) {
    var buffer = new byte[size];
    for (var i = 0; i < size; ++i)
      buffer[i] = (byte)(i & 0xFF);

    return buffer;
  }

  private static IEnumerable<TestCaseData> SelectedBlocks() {
    FormatRegistration.EnsureInitialized();

    var wanted = Environment.GetEnvironmentVariable("CW_LARGE_INPUT_BLOCKS");
    var filter = string.IsNullOrWhiteSpace(wanted)
      ? null
      : new HashSet<string>(wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    foreach (var block in BuildingBlockRegistry.All.OrderBy(b => b.Id, StringComparer.Ordinal))
      if (filter == null || filter.Contains(block.Id))
        yield return new TestCaseData(block).SetName($"{block.DisplayName} / LargeInput");
  }

  /// <summary>
  /// Round-trips one block at <see cref="LargeSize"/> bytes of compressible text.
  /// </summary>
  /// <param name="block">The building block under test.</param>
  [TestCaseSource(nameof(SelectedBlocks))]
  [CancelAfter(1_800_000)]
  public void RoundTrip_AtLargeSize(IBuildingBlock block) {
    var size = LargeSize;
    var data = TextPattern(size);

    var stopwatch = Stopwatch.StartNew();
    var compressed = block.Compress(data);
    var compressMs = stopwatch.ElapsedMilliseconds;

    stopwatch.Restart();
    var decompressed = block.Decompress(compressed);
    var decompressMs = stopwatch.ElapsedMilliseconds;

    // Compare lengths first: a failing sequence comparison over 256 MB would build
    // an unusable failure message.
    Assert.That(decompressed.Length, Is.EqualTo(data.Length),
      $"{block.DisplayName}: round-trip length mismatch at {size} bytes");
    Assert.That(decompressed.AsSpan().SequenceEqual(data), Is.True,
      $"{block.DisplayName}: round-trip content mismatch at {size} bytes");

    TestContext.Out.WriteLine(
      $"{block.DisplayName}: input={size}, compressed={compressed.Length}, " +
      $"ratio={compressed.Length * 100.0 / size:F2}%, compress={compressMs}ms, decompress={decompressMs}ms");
  }

  /// <summary>
  /// Unary coding emits up to 256 bits per input byte, so its decoder's bit position
  /// passes 2^31 at roughly 16.7 MB of input — far below any array limit. Held as a
  /// 32-bit value the position wraps negative, the bounds check reads the negative as
  /// in-range, and the decode throws <see cref="IndexOutOfRangeException"/>.
  /// </summary>
  [Test]
  [CancelAfter(300_000)]
  public void Unary_RoundTrips_WhereA32BitBitPositionWouldWrap() {
    // 2^24 bytes of 0..255 code to 2^24 * 128.5 = 2,155,872,256 bits, just over 2^31.
    const int Size = 16_777_216;
    var block = BuildingBlockRegistry.All.Single(b => b.Id == "BB_Unary");
    var data = IncrementingPattern(Size);

    var compressed = block.Compress(data);
    var decompressed = block.Decompress(compressed);

    Assert.That(decompressed.Length, Is.EqualTo(Size));
    Assert.That(decompressed.AsSpan().SequenceEqual(data), Is.True);
  }

  /// <summary>
  /// The DEFLATE encoder compares a Huffman-coded block against an uncompressed one
  /// using a bit estimate that includes <c>length * 8</c>. Crossing 2^28 bytes of
  /// input must not change the compression ratio, which it would if that estimate
  /// wrapped negative and made an uncompressed block look cheaper.
  /// </summary>
  [Test]
  [CancelAfter(1_800_000)]
  public void Deflate_RatioIsStableAcrossThe256MegabyteBoundary() {
    var block = BuildingBlockRegistry.All.Single(b => b.Id == "BB_Deflate");

    var below = block.Compress(TextPattern(268_435_455));
    var above = block.Compress(TextPattern(268_435_457));

    var ratioBelow = below.Length * 1.0 / 268_435_455;
    var ratioAbove = above.Length * 1.0 / 268_435_457;

    Assert.That(ratioAbove, Is.EqualTo(ratioBelow).Within(0.01),
      "crossing 2^28 bytes changed the DEFLATE compression ratio");
  }
}
