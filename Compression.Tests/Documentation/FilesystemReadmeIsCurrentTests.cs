#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Documentation;

/// <summary>
/// The support matrix in the filesystem package README says what the code says.
/// </summary>
/// <remarks>
/// <para>A state letter written by hand is right on the day it is written and
/// quietly wrong afterwards, and nothing notices. Every derived cell of the
/// matrix is read back from the descriptor at runtime, so this test can render
/// the region again and compare. Drift fails here rather than misleading a
/// reader on nuget.org.</para>
///
/// <para>Set <c>CWB_WRITE_DOCS=1</c> to rewrite the region instead of comparing
/// — that is how it is updated after a change. The hand-written cells survive
/// the rewrite.</para>
/// </remarks>
[TestFixture]
public class FilesystemReadmeIsCurrentTests {

  [Test]
  public void TheSupportMatrix_SaysWhatTheCodeSays() {
    var root = RepositoryRoot();
    var path = Path.Combine(root, FilesystemSupportMatrix.ReadmePath);
    var readme = File.ReadAllText(path).ReplaceLineEndings("\n");
    var (before, section, after) = FilesystemSupportMatrix.Split(readme);
    var expected = FilesystemSupportMatrix.Render(section, FilesystemSupportMatrix.Descriptors(root));

    if (Environment.GetEnvironmentVariable("CWB_WRITE_DOCS") == "1") {
      File.WriteAllText(path, before + expected + after);
      Assert.Pass($"Wrote the support matrix of {FilesystemSupportMatrix.ReadmePath}.");
      return;
    }

    Assert.That(section, Is.EqualTo(expected),
      $"The support matrix in {FilesystemSupportMatrix.ReadmePath} no longer matches the descriptors. " +
      "Re-run with CWB_WRITE_DOCS=1 to bring it up to date.");
  }

  [Test]
  public void EveryBundledDescriptor_HasARow() {
    var root = RepositoryRoot();
    var readme = File.ReadAllText(Path.Combine(root, FilesystemSupportMatrix.ReadmePath));
    var (_, section, _) = FilesystemSupportMatrix.Split(readme);
    var missing = FilesystemSupportMatrix.Descriptors(root)
      .Select(d => d.Id)
      .Where(id => !section.Contains("| `" + id + "` |", StringComparison.Ordinal))
      .ToList();
    Assert.That(missing, Is.Empty, "Descriptors without a row in the support matrix: " + string.Join(", ", missing));
  }

  [Test]
  public void EveryRow_NamesALiveDescriptor() {
    var root = RepositoryRoot();
    var readme = File.ReadAllText(Path.Combine(root, FilesystemSupportMatrix.ReadmePath));
    var (_, section, _) = FilesystemSupportMatrix.Split(readme);
    var known = FilesystemSupportMatrix.Descriptors(root).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
    var orphans = System.Text.RegularExpressions.Regex.Matches(section, @"^\| [^|]* \| `([^`]+)` \|", System.Text.RegularExpressions.RegexOptions.Multiline)
      .Select(m => m.Groups[1].Value)
      .Where(id => !known.Contains(id))
      .ToList();
    Assert.That(orphans, Is.Empty, "Rows for formats that no longer exist: " + string.Join(", ", orphans));
  }

  /// <summary>The repository root, found by walking up to the solution file.</summary>
  internal static string RepositoryRoot() {
    var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
    while (directory != null && !directory.EnumerateFiles("*.slnx").Any())
      directory = directory.Parent;
    Assert.That(directory, Is.Not.Null, "Could not find the repository root from the test directory.");
    return directory!.FullName;
  }
}
