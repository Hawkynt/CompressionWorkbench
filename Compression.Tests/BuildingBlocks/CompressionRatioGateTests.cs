using System.Text;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.BuildingBlocks;

/// <summary>
/// Asserts that every registered building block claiming to compress actually
/// does, by driving the whole catalogue rather than one block at a time.
/// </summary>
/// <remarks>
/// <para>
/// A round-trip test proves a block can decode its own output. It says nothing
/// about whether it compresses, because storing the input verbatim round-trips
/// perfectly - and several blocks here did exactly that while every test passed.
/// A compressor has one property a store cannot fake: on redundant input it must
/// produce markedly less output.
/// </para>
/// <para>
/// Two samples are checked, because either alone can be passed without coding
/// anything. A run of one byte is the only redundancy a run-length coder can
/// exploit, so it cannot be the sole test; but an entropy coder carrying a
/// run-length shortcut crushes that run while its model does nothing, which is
/// how a broken context-mixing coder went unnoticed. A repeated multi-byte
/// phrase closes that hole and is required of anything general purpose.
/// </para>
/// <para>
/// Exemptions are listed by id with the reason recorded, so waiving the check is
/// a claim someone has to make. A newly added block is held to it by default.
/// </para>
/// </remarks>
[TestFixture]
public class CompressionRatioGateTests {

  private const double Limit = 0.95;
  private const int SampleSize = 20000;

  /// <summary>Blocks that do not compress a run of a single byte, with the reason.</summary>
  private static readonly Dictionary<string, string> RunExempt = new(StringComparer.Ordinal) {
    ["BB_BcjArm"] = "branch-conversion filter, size-preserving by design",
    ["BB_BcjArm64"] = "branch-conversion filter, size-preserving by design",
    ["BB_BcjArmThumb"] = "branch-conversion filter, size-preserving by design",
    ["BB_BcjIa64"] = "branch-conversion filter, size-preserving by design",
    ["BB_BcjPowerPc"] = "branch-conversion filter, size-preserving by design",
    ["BB_BcjRiscV"] = "branch-conversion filter, size-preserving by design",
    ["BB_BcjSparc"] = "branch-conversion filter, size-preserving by design",
    ["BB_BcjX86"] = "branch-conversion filter, size-preserving by design",
    ["BB_Bwt"] = "reordering transform, size-preserving by design",
    ["BB_Delta"] = "difference transform, size-preserving by design",
    ["BB_Dpcm"] = "difference transform, size-preserving by design",
    ["BB_Mtf"] = "ranking transform, size-preserving by design",
    ["BB_EliasDelta"] = "universal code for small integers; arbitrary bytes cost over 8 bits",
    ["BB_EliasGamma"] = "universal code for small integers; arbitrary bytes cost over 8 bits",
    ["BB_ExpGolomb"] = "universal code for small integers; arbitrary bytes cost over 8 bits",
    ["BB_Fibonacci"] = "universal code for small integers; arbitrary bytes cost over 8 bits",
    ["BB_Golomb"] = "parameterised code for small integers; arbitrary bytes cost over 8 bits",
    ["BB_GolombFixedM"] = "M pinned at 2; arbitrary bytes cost far more than 8 bits",
    ["BB_Levenshtein"] = "universal code for small integers; arbitrary bytes cost over 8 bits",
    ["BB_Omega"] = "universal code for small integers; arbitrary bytes cost over 8 bits",
    ["BB_Unary"] = "universal code for small integers; arbitrary bytes cost far more than 8 bits",
    ["BB_Dna"] = "packs 2 bits per symbol for A/C/G/T only; other bytes become exceptions",
    ["BB_Shoco"] = "entropy model trained on English text, not on repeated single bytes",
  };

  /// <summary>Blocks that do not compress a repeated multi-byte phrase, with the reason.</summary>
  private static readonly Dictionary<string, string> PatternExempt = BuildPatternExempt();

  private static Dictionary<string, string> BuildPatternExempt() {
    var result = new Dictionary<string, string>(RunExempt, StringComparer.Ordinal) {
      ["BB_Rle"] = "run-length coder; a repeating phrase contains no runs",
      ["BB_PackBits"] = "run-length coder; a repeating phrase contains no runs",
      ["BB_DeltaRle"] = "run-length coder over differences; a repeating phrase contains no runs",
      ["BB_842"] = "fixed template coder with a short window; a 45-byte phrase exceeds its reach",
      ["BB_Tunstall"] = "variable-to-fixed code with one-byte codewords; gains little on this phrase",
    };
    result.Remove("BB_Shoco"); // Shoco does compress the phrase, just not the run
    return result;
  }

  private static byte[] RunSample() {
    var data = new byte[SampleSize];
    Array.Fill(data, (byte)0x61);
    return data;
  }

  private static byte[] PatternSample() {
    const string phrase = "the quick brown fox jumps over the lazy dog. ";
    var builder = new StringBuilder(SampleSize + phrase.Length);
    while (builder.Length < SampleSize)
      builder.Append(phrase);
    return Encoding.ASCII.GetBytes(builder.ToString(0, SampleSize));
  }

  private static IEnumerable<IBuildingBlock> AllBlocks() {
    FormatRegistration.EnsureInitialized();
    return BuildingBlockRegistry.All;
  }

  [Test, Category("Regression")]
  public void EveryBlock_CompressesARunOfOneByte() => AssertCompresses(RunSample(), RunExempt, "a run of one byte");

  [Test, Category("Regression")]
  public void EveryBlock_CompressesARepeatedPhrase() => AssertCompresses(PatternSample(), PatternExempt, "a repeated phrase");

  private static void AssertCompresses(byte[] sample, Dictionary<string, string> exempt, string what) {
    var offenders = new List<string>();

    foreach (var block in AllBlocks()) {
      if (exempt.ContainsKey(block.Id))
        continue;

      byte[] compressed;
      try {
        compressed = block.Compress(sample);
      } catch (Exception error) {
        offenders.Add($"{block.Id} ({block.DisplayName}) threw {error.GetType().Name} on {what}");
        continue;
      }

      var ratio = (double)compressed.Length / sample.Length;
      if (ratio >= Limit)
        offenders.Add($"{block.Id} ({block.DisplayName}) left {what} at "
          + $"{sample.Length} -> {compressed.Length} bytes ({ratio * 100:F0}%), which is not compression");
    }

    Assert.That(offenders, Is.Empty,
      $"A block that round-trips but never shrinks {what} is storing its input rather than coding it. "
      + "If it genuinely is a transform or a code for a narrow domain, add it to the exemption table "
      + "above with the reason.\n" + string.Join("\n", offenders));
  }
}
