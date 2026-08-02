#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Documentation;

/// <summary>
/// Each filesystem has a page, and the page says what the code says.
/// </summary>
/// <remarks>
/// <para>Documentation kept by hand goes stale the first time a verb is added
/// or a parameter renamed, and nothing notices. Every fact on these pages is
/// read back from the descriptor at runtime or from the XML documentation the
/// compiler emits for it, so this test can regenerate them and compare. Drift
/// fails here rather than misleading a reader later.</para>
///
/// <para>Set <c>CWB_WRITE_DOCS=1</c> to rewrite the pages instead of comparing
/// — that is how they are updated after a change.</para>
/// </remarks>
[TestFixture]
public class FilesystemDocsAreCurrentTests {

  /// <summary>Where the pages live, relative to the repository root.</summary>
  private const string DocsFolder = "docs/filesystems";

  private static IEnumerable<TestCaseData> Filesystems()
    => FilesystemDocGenerator.Filesystems()
        .Select(d => new TestCaseData(d.Id).SetName($"{d.Id} has a current page"));

  [TestCaseSource(nameof(Filesystems))]
  public void ThePage_SaysWhatTheCodeSays(string formatId) {
    var descriptor = FilesystemDocGenerator.Filesystems().First(d => d.Id == formatId);
    var expected = FilesystemDocGenerator.Page(descriptor);
    var path = Path.Combine(RepositoryRoot(), DocsFolder, formatId + ".md");

    if (Environment.GetEnvironmentVariable("CWB_WRITE_DOCS") == "1") {
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, expected);
      Assert.Pass($"Wrote {DocsFolder}/{formatId}.md.");
      return;
    }

    Assert.That(File.Exists(path), Is.True,
      $"{DocsFolder}/{formatId}.md is missing. Re-run with CWB_WRITE_DOCS=1 to write it.");
    Assert.That(File.ReadAllText(path).ReplaceLineEndings("\n"), Is.EqualTo(expected),
      $"{DocsFolder}/{formatId}.md no longer matches the implementation. " +
      "Re-run with CWB_WRITE_DOCS=1 to bring it up to date.");
  }

  [Test]
  public void TheIndex_ListsEveryFilesystem() {
    var expected = FilesystemDocGenerator.Index();
    var path = Path.Combine(RepositoryRoot(), DocsFolder, "README.md");

    if (Environment.GetEnvironmentVariable("CWB_WRITE_DOCS") == "1") {
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, expected);
      Assert.Pass($"Wrote {DocsFolder}/README.md.");
      return;
    }

    Assert.That(File.Exists(path), Is.True,
      $"{DocsFolder}/README.md is missing. Re-run with CWB_WRITE_DOCS=1 to write it.");
    Assert.That(File.ReadAllText(path).ReplaceLineEndings("\n"), Is.EqualTo(expected),
      $"{DocsFolder}/README.md no longer matches the implementation. " +
      "Re-run with CWB_WRITE_DOCS=1 to bring it up to date.");
  }

  [Test]
  public void NoPage_IsLeftBehindByAFormatThatWentAway() {
    var folder = Path.Combine(RepositoryRoot(), DocsFolder);
    if (!Directory.Exists(folder)) Assert.Ignore("No pages have been written yet.");

    var known = FilesystemDocGenerator.Filesystems().Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
    var orphans = Directory.EnumerateFiles(folder, "*.md")
      .Select(Path.GetFileNameWithoutExtension)
      .Where(id => id != null && id != "README" && !known.Contains(id))
      .ToList();

    Assert.That(orphans, Is.Empty,
      $"These pages describe formats that no longer exist: {string.Join(", ", orphans)}.");
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
