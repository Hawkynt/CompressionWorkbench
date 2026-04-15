#pragma warning disable CS1591

using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Exotic;

/// <summary>
/// Optional real-world validation harness. Scans <c>test-corpus/</c> at the repo
/// root (if it exists) and attempts to identify, list, and extract every file
/// found there. The directory is <c>.gitignore</c>d so users can drop private
/// or licensed files in without risk of committing them.
/// </summary>
[TestFixture]
[Category("ExoticFormats")]
public class ExoticFormatReadmeTests {

  [OneTimeSetUp]
  public void OneTimeSetup() => FormatRegistration.EnsureInitialized();

  [Test]
  public void TestCorpus_IdentifyAndExtractAll() {
    // TestContext.TestDirectory is the test bin\<cfg>\<tfm> folder. The repo root
    // sits four levels up (bin → net10.0 → cfg → Compression.Tests → repo).
    var corpusDir = Path.GetFullPath(
      Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus"));

    if (!Directory.Exists(corpusDir)) {
      Assert.Ignore($"test-corpus directory not found at '{corpusDir}' — " +
        "drop real files into <repo>/test-corpus to enable real-world validation");
      return;
    }

    var files = Directory.GetFiles(corpusDir, "*", SearchOption.AllDirectories)
      .Where(f => !string.Equals(Path.GetFileName(f), "README.md", StringComparison.OrdinalIgnoreCase))
      .ToList();

    if (files.Count == 0) {
      Assert.Ignore($"test-corpus directory at '{corpusDir}' contains no files " +
        "(only README.md) — drop real files in to enable real-world validation");
      return;
    }

    var results = new List<CorpusResult>();
    foreach (var file in files) {
      var fileName = Path.GetFileName(file);
      string? formatName = null;
      var listed = false;
      var extracted = false;
      string? error = null;

      try {
        var format = FormatDetector.Detect(file);
        formatName = format.ToString();

        try {
          var entries = ArchiveOperations.List(file, null);
          listed = entries != null;
        } catch (Exception ex) {
          error = "list: " + ex.GetType().Name + ": " + ex.Message;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "corpus_" + Guid.NewGuid().ToString("N"));
        try {
          Directory.CreateDirectory(tempDir);
          try {
            ArchiveOperations.Extract(file, tempDir, null, null);
            extracted = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories).Length > 0;
          } catch (Exception ex) {
            error = (error == null ? "" : error + "; ") + "extract: " + ex.GetType().Name + ": " + ex.Message;
          }
        } finally {
          try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }
      } catch (Exception ex) {
        error = "detect: " + ex.GetType().Name + ": " + ex.Message;
      }

      results.Add(new CorpusResult(fileName, formatName, listed, extracted, error));
    }

    // Emit a per-file report. Visible in `dotnet test --logger:console;verbosity=normal`.
    TestContext.Out.WriteLine($"test-corpus report — {results.Count} file(s) found in {corpusDir}");
    TestContext.Out.WriteLine(new string('-', 100));
    foreach (var r in results) {
      TestContext.Out.WriteLine(
        $"  {r.File,-40} format={r.Format ?? "?",-18} listed={r.Listed,-5} extracted={r.Extracted,-5} " +
        (r.Error != null ? "ERROR: " + r.Error : ""));
    }
    TestContext.Out.WriteLine(new string('-', 100));

    var anyReadable = results.Any(r => r.Listed || r.Extracted);
    var totals = $"{results.Count(r => r.Listed)}/{results.Count} listed, " +
                 $"{results.Count(r => r.Extracted)}/{results.Count} extracted";
    TestContext.Out.WriteLine("Totals: " + totals);

    // Only fail if literally nothing could be read — a healthy corpus may include
    // unsupported formats and we don't want to penalise that.
    Assert.That(anyReadable, Is.True,
      $"No files in test-corpus could be listed or extracted ({totals})");
  }

  private readonly record struct CorpusResult(
    string File, string? Format, bool Listed, bool Extracted, string? Error);
}
