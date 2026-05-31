using Compression.Core.Layout;

namespace Compression.Tests.Layout;

/// <summary>
/// The cluster/layout optimizer must return the genuine global optimum over its
/// candidate set — not a heuristic. These tests pin that against an independent
/// brute-force reference, plus the tie-break (prefer the smaller cluster) and
/// the tier-then-cost ordering.
/// </summary>
[TestFixture]
public class FilesystemLayoutOptimizerTests {

  private static long BruteForceArgminCost(IReadOnlyList<int> candidates, Func<int, long?> cost) {
    long best = long.MaxValue;
    foreach (var c in candidates) {
      var v = cost(c);
      if (v.HasValue && v.Value < best) best = v.Value;
    }
    return best;
  }

  [Test, Category("Spec")]
  public void SelectClusterSize_ReturnsGlobalMinimumCost() {
    var candidates = FilesystemLayoutOptimizer.StandardClusterSizes;
    // A non-monotonic cost so a naive first/last pick would be wrong.
    long Cost(int cb) => Math.Abs((long)cb - 7000) + cb / 4;
    var chosen = FilesystemLayoutOptimizer.SelectClusterSize(candidates, cb => Cost(cb));
    Assert.That(Cost(chosen), Is.EqualTo(BruteForceArgminCost(candidates, cb => Cost(cb))),
      "chosen size must have the minimum cost over all candidates");
  }

  [Test, Category("Spec")]
  public void SelectClusterSize_PrefersSmallerClusterOnTie() {
    // Constant cost: every candidate ties, so the smallest must win (least slack
    // risk for heterogeneous future writes).
    var chosen = FilesystemLayoutOptimizer.SelectClusterSize([512, 1024, 2048, 4096], _ => 100L);
    Assert.That(chosen, Is.EqualTo(512));
  }

  [Test, Category("Spec")]
  public void SelectClusterSize_AllInvalid_FallsBackToFirst() {
    var chosen = FilesystemLayoutOptimizer.SelectClusterSize([2048, 4096], _ => null);
    Assert.That(chosen, Is.EqualTo(2048));
  }

  [Test, Category("Spec")]
  public void SelectClusterSizeTiered_PrefersLowestTierEvenWhenHigherTierIsCheaper() {
    // Tier 16 is genuinely cheaper, but the optimizer must stay in tier 12 when a
    // valid tier-12 option exists (avoids silently escalating the FS variant).
    var candidates = new[] { 512, 1024, 2048, 4096 };
    int? Tier(int cb) => cb <= 1024 ? 12 : 16;
    long Cost(int cb) => cb <= 1024 ? 1000 : 1; // higher tier looks cheaper
    var chosen = FilesystemLayoutOptimizer.SelectClusterSizeTiered(candidates, Tier, cb => Cost(cb));
    Assert.That(Tier(chosen), Is.EqualTo(12), "must stay in the lowest valid tier");
    Assert.That(chosen, Is.EqualTo(512), "within the tier, the cheaper option wins");
  }

  [Test, Category("Spec")]
  public void SelectPair_ReturnsGlobalMinimumOverBothAxes() {
    var p1 = new[] { 512, 4096, 32768 };
    var p2 = new[] { 256, 1024, 4096 };
    long Cost(int a, int b) => (long)a * 3 + b * 7 + Math.Abs(a - b);
    var (ca, cb) = FilesystemLayoutOptimizer.SelectPair(p1, p2, (a, b) => Cost(a, b));

    long brute = long.MaxValue;
    foreach (var a in p1) foreach (var b in p2) if (Cost(a, b) < brute) brute = Cost(a, b);
    Assert.That(Cost(ca, cb), Is.EqualTo(brute), "pair must be the 2-D global minimum");
  }

  [Test, Category("Spec")]
  public void DataClusters_And_Slack_AreExact() {
    var files = new long[] { 0, 100, 4096, 4097 };
    // clusters: 0 + 1 + 1 + 2 = 4 at 4096-byte clusters
    Assert.That(FilesystemLayoutOptimizer.DataClusters(files, 4096), Is.EqualTo(4));
    // slack: 0 + (4096-100) + 0 + (8192-4097) = 3996 + 4095 = 8091
    Assert.That(FilesystemLayoutOptimizer.Slack(files, 4096), Is.EqualTo(3996 + 4095));
  }

  [Test, Category("Spec")]
  public void SelectClusterSize_MatchesBruteForce_OnRandomWorkloads() {
    var rng = new Random(20260531);
    var candidates = FilesystemLayoutOptimizer.StandardClusterSizes;
    for (var iter = 0; iter < 200; iter++) {
      var files = new long[rng.Next(1, 40)];
      for (var i = 0; i < files.Length; i++) files[i] = rng.Next(0, 2_000_000);
      // Realistic FAT-like cost: per-file slack + a FAT-table overhead growing with cluster count.
      long Cost(int cb) {
        var clusters = FilesystemLayoutOptimizer.DataClusters(files, cb);
        var slack = FilesystemLayoutOptimizer.Slack(files, cb);
        var fatOverhead = (clusters + 2) * 4; // 4 bytes/entry, FAT32-ish
        return slack + fatOverhead;
      }
      var chosen = FilesystemLayoutOptimizer.SelectClusterSize(candidates, cb => Cost(cb));
      Assert.That(Cost(chosen), Is.EqualTo(BruteForceArgminCost(candidates, cb => Cost(cb))),
        $"iteration {iter}: optimizer must match brute-force minimum");
    }
  }
}
