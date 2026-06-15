using Compression.Analysis;
using Compression.Registry;

namespace Compression.Tests.Optimizer;

/// <summary>
/// The cross-block auto-selector: given raw bytes it benchmarks every building
/// block, ranks them by the chosen objective, and returns the winner's actual
/// compressed bytes plus the ranked table. These specs use small synthetic
/// inputs and a constrained candidate set for determinism.
/// </summary>
[TestFixture]
public class BestBlockSelectorTests {

  private static byte[] CompressibleSample() {
    var ms = new MemoryStream();
    var phrase = "the quick brown fox jumps over the lazy dog "u8.ToArray();
    for (var i = 0; i < 2000; i++) ms.Write(phrase);
    return ms.ToArray();
  }

  private static byte[] RandomSample() {
    var rng = new Random(4242);
    var data = new byte[8192];
    rng.NextBytes(data);
    return data;
  }

  /// <summary>A subset of registered blocks that are robust across arbitrary inputs.</summary>
  private static IReadOnlyList<IBuildingBlock> StableBlocks() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    string[] ids = ["BB_Deflate", "BB_Lz4", "BB_Lz77"];
    return [.. ids.Select(BuildingBlockRegistry.GetById).Where(b => b is not null).Cast<IBuildingBlock>()];
  }

  [Test, Category("Spec")]
  public void Select_OnCompressibleData_PicksAWinnerThatShrinks() {
    var data = CompressibleSample();
    var result = BestBlockSelector.Select(data, new BestBlockSelector.Options { OptimizeWinnerParameters = false }, StableBlocks());

    Assert.That(result.WinningBlockId, Is.Not.Null.And.Not.Empty);
    Assert.That(result.Ratio, Is.LessThan(1.0), "compressible data must shrink");
    Assert.That(result.CompressedBytes.Length, Is.GreaterThan(0));

    // The winner must be the smallest among the successful candidates.
    var smallest = result.Table.Where(c => c.Succeeded).Min(c => c.CompressedSize);
    Assert.That(result.CompressedSize, Is.EqualTo(smallest), "winner is the smallest successful candidate");
  }

  [Test, Category("Spec")]
  public void Select_WinnerCompressedBytesRoundTrip() {
    var data = CompressibleSample();
    var result = BestBlockSelector.Select(data, new BestBlockSelector.Options { OptimizeWinnerParameters = false }, StableBlocks());

    var block = BuildingBlockRegistry.GetById(result.WinningBlockId)!;
    var restored = block.Decompress(result.CompressedBytes);
    Assert.That(restored, Is.EqualTo(data), "the winner's output must decompress back to the input");
  }

  [Test, Category("Spec")]
  public void Select_OnRandomData_DoesNotClaimBigWins() {
    var data = RandomSample();
    var result = BestBlockSelector.Select(data, new BestBlockSelector.Options { OptimizeWinnerParameters = false }, StableBlocks());

    Assert.That(result.Table.Any(c => c.Succeeded), Is.True, "at least one block round-trips random data");
    // Incompressible data: the best ratio should be near or above 1.0 — no magic shrinking.
    Assert.That(result.Ratio, Is.GreaterThan(0.9), "random data should not compress meaningfully");
  }

  [Test, Category("Spec")]
  public void Select_RatioObjective_PrefersSmallWithinSpeedWindow() {
    var data = CompressibleSample();
    var result = BestBlockSelector.Select(
      data,
      new BestBlockSelector.Options {
        Objective = BestBlockSelector.Objective.BestRatioWithinSpeedWindow,
        SpeedWindowPercent = 100.0,
        OptimizeWinnerParameters = false,
      },
      StableBlocks());

    // The winner must be a successful candidate and present in the table.
    var winnerRow = result.Table.Single(c => c.BlockId == result.WinningBlockId);
    Assert.That(winnerRow.Succeeded, Is.True);
  }

  [Test, Category("Spec")]
  public void Select_EmptyCandidateSet_Throws() {
    var data = CompressibleSample();
    Assert.That(
      () => BestBlockSelector.Select(data, new BestBlockSelector.Options(), []),
      Throws.InstanceOf<InvalidOperationException>());
  }
}
