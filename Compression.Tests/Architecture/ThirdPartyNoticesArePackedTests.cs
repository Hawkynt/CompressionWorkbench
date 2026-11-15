using Compression.Tests.Documentation;

namespace Compression.Tests.Architecture;

/// <summary>
/// Every THIRD-PARTY-NOTICE*.txt beside converted code must reach the NuGet package: attribution is
/// a licence term. The repository's Directory.Build.targets packs them all; these checks keep that
/// rule in place and keep a nearer Directory.Build.targets from silently hiding it, which is how
/// most notices had fallen out of the Archives package.
/// </summary>
[TestFixture]
public sealed class ThirdPartyNoticesArePackedTests {
  [Test, Category("Spec")]
  public void TheRepositoryTargets_PackEveryThirdPartyNotice() {
    var root = FilesystemReadmeIsCurrentTests.RepositoryRoot();
    var targets = File.ReadAllText(Path.Combine(root, "Directory.Build.targets"));
    Assert.That(targets, Does.Contain("THIRD-PARTY-NOTICE*.txt").And.Contain("Pack=\"true\"").And.Contain("_GetPackageFiles"));
  }

  [Test, Category("Spec")]
  public void EveryNestedDirectoryBuildTargets_ChainsToTheRepositoryOne() {
    var root = FilesystemReadmeIsCurrentTests.RepositoryRoot();
    var nested = Directory.EnumerateFiles(root, "Directory.Build.targets", SearchOption.AllDirectories)
      .Where(p => !string.Equals(Path.GetDirectoryName(p), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
      .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(static s => s is "bin" or "obj" or ".git" or ".claude" or "node_modules"))
      .Where(p => !File.ReadAllText(p).Contains("GetPathOfFileAbove('Directory.Build.targets'", StringComparison.Ordinal))
      .Select(p => Path.GetRelativePath(root, p))
      .ToList();
    Assert.That(nested, Is.Empty, "these Directory.Build.targets hide the repository one (and its notice packing); import it with GetPathOfFileAbove");
  }
}
