namespace Compression.Tests.Analysis;

[TestFixture]
public class AnalyzerNewFeaturesTests {

  [Test, Category("HappyPath")]
  public void AutoExtractor_DetectsAndExtracts_Zip() {
    // Create a ZIP in memory
    using var zipMs = new MemoryStream();
    using (var w = new FileFormat.Zip.ZipWriter(zipMs, leaveOpen: true))
      w.AddEntry("hello.txt", "Hello World"u8.ToArray());
    zipMs.Position = 0;

    var extractor = new Compression.Analysis.AutoExtractor();
    var result = extractor.Extract(zipMs);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.FormatId, Is.EqualTo("Zip"));
    Assert.That(result.Entries, Has.Count.EqualTo(1));
    Assert.That(result.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(result.Entries[0].Data, Is.EqualTo("Hello World"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void AutoExtractor_ReturnsNull_ForUnknownData() {
    using var ms = new MemoryStream(new byte[100]);
    var extractor = new Compression.Analysis.AutoExtractor();
    var result = extractor.Extract(ms);
    Assert.That(result, Is.Null);
  }

  [Test, Category("HappyPath")]
  public void AutoExtractor_StreamFormat_Gzip() {
    // Create gzip data
    using var gzMs = new MemoryStream();
    var data = "Hello compressed world!"u8.ToArray();
    using var dataMs = new MemoryStream(data);
    var gzipDesc = new FileFormat.Gzip.GzipFormatDescriptor();
    ((Compression.Registry.IStreamFormatOperations)gzipDesc).Compress(dataMs, gzMs);
    gzMs.Position = 0;

    var extractor = new Compression.Analysis.AutoExtractor();
    var result = extractor.Extract(gzMs);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.FormatId, Is.EqualTo("Gzip"));
    Assert.That(result.Entries, Has.Count.EqualTo(1));
    Assert.That(result.Entries[0].Data, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void BatchAnalyzer_AnalyzesDirectory() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_batch_test_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);

      // Create a ZIP file
      using (var fs = File.Create(Path.Combine(tmpDir, "test.zip"))) {
        using var w = new FileFormat.Zip.ZipWriter(fs, leaveOpen: true);
        w.AddEntry("data.txt", "test"u8.ToArray());
      }

      // Create a plain text file
      File.WriteAllText(Path.Combine(tmpDir, "readme.txt"), "hello");

      var analyzer = new Compression.Analysis.BatchAnalyzer();
      var result = analyzer.AnalyzeDirectory(tmpDir);

      Assert.That(result.TotalFiles, Is.EqualTo(2));
      Assert.That(result.FileResults, Has.Count.EqualTo(2));
      Assert.That(result.FormatDistribution.ContainsKey("Zip"), Is.True);
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void FormatSuggester_SuggestsFormats() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_suggest_test_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      File.WriteAllText(Path.Combine(tmpDir, "file1.txt"), "content");
      File.WriteAllText(Path.Combine(tmpDir, "file2.txt"), "more content");

      var suggester = new Compression.Analysis.FormatSuggester();
      var suggestions = suggester.Suggest([tmpDir]);

      Assert.That(suggestions, Has.Count.GreaterThan(0));
      Assert.That(suggestions[0].FormatId, Is.Not.Null.Or.Empty);
      Assert.That(suggestions[0].Score, Is.GreaterThan(0));
      Assert.That(suggestions[0].Rationale, Is.Not.Null.Or.Empty);
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void FormatSuggester_LinuxPlatform_PrefersTarGz() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb_suggest_linux_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      Directory.CreateDirectory(tmpDir);
      File.WriteAllText(Path.Combine(tmpDir, "script.sh"), "#!/bin/bash");

      var suggester = new Compression.Analysis.FormatSuggester();
      var suggestions = suggester.Suggest([tmpDir], Compression.Analysis.FormatSuggester.Platform.Linux);

      Assert.That(suggestions, Has.Count.GreaterThan(0));
      // tar.gz should rank high for Linux
      var tgzSuggestion = suggestions.FirstOrDefault(s => s.FormatId == "TarGz");
      Assert.That(tgzSuggestion, Is.Not.Null);
    } finally {
      Directory.Delete(tmpDir, true);
    }
  }
}
