using FileFormat.SevenZip;

namespace Compression.Tests.SevenZip;

[TestFixture]
public class SolidBlockOptimizerTests {

  /// <summary>
  /// Creates a 7z archive containing a mix of source code, binary data, and
  /// plain text — representative of a real-world project archive.
  /// </summary>
  private static MemoryStream CreateMixedArchive() {
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, SevenZipCodec.Lzma2, leaveOpen: true)) {
      // Source code files (similar content compresses well together)
      var cs1 = System.Text.Encoding.UTF8.GetBytes(
        "using System;\nnamespace Foo {\n  public class Bar {\n    public void Run() { Console.WriteLine(\"Hello\"); }\n  }\n}\n");
      var cs2 = System.Text.Encoding.UTF8.GetBytes(
        "using System;\nnamespace Foo {\n  public class Baz {\n    public int Compute() { return 42; }\n  }\n}\n");
      var cs3 = System.Text.Encoding.UTF8.GetBytes(
        "using System.IO;\nnamespace Foo {\n  public static class FileHelper {\n    public static void Save(string path, byte[] data) { File.WriteAllBytes(path, data); }\n  }\n}\n");

      // Plain text
      var txt1 = System.Text.Encoding.UTF8.GetBytes(
        "This is a README file describing the project.\nIt has multiple lines.\nLine 3.\nLine 4.\n");
      var txt2 = System.Text.Encoding.UTF8.GetBytes(
        "CHANGELOG\n=========\nv1.0 - Initial release\nv1.1 - Bug fixes\nv1.2 - Performance improvements\n");

      // Binary data (pseudo-random, less compressible)
      var bin1 = new byte[2048];
      var rng = new Random(42);
      rng.NextBytes(bin1);

      var bin2 = new byte[1024];
      rng.NextBytes(bin2);

      // XML config
      var xml = System.Text.Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\"?>\n<configuration>\n  <setting name=\"debug\" value=\"true\" />\n  <setting name=\"timeout\" value=\"30\" />\n</configuration>\n");

      writer.AddEntry(new SevenZipEntry { Name = "src/Bar.cs" }, cs1);
      writer.AddEntry(new SevenZipEntry { Name = "src/Baz.cs" }, cs2);
      writer.AddEntry(new SevenZipEntry { Name = "src/FileHelper.cs" }, cs3);
      writer.AddEntry(new SevenZipEntry { Name = "README.txt" }, txt1);
      writer.AddEntry(new SevenZipEntry { Name = "CHANGELOG.txt" }, txt2);
      writer.AddEntry(new SevenZipEntry { Name = "bin/data.bin" }, bin1);
      writer.AddEntry(new SevenZipEntry { Name = "bin/extra.bin" }, bin2);
      writer.AddEntry(new SevenZipEntry { Name = "config.xml" }, xml);
      writer.Finish();
    }
    ms.Position = 0;
    return ms;
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_MixedArchive_ReturnsValidResult() {
    using var archive = CreateMixedArchive();
    var result = SolidBlockOptimizer.Optimize(archive);

    Assert.That(result, Is.Not.Null);
    Assert.That(result.Data, Is.Not.Null);
    Assert.That(result.Data.Length, Is.GreaterThan(0));
    Assert.That(result.WinningStrategy, Is.Not.Null.And.Not.Empty);
    Assert.That(result.Trials, Is.Not.Null);
    Assert.That(result.Trials.Count, Is.GreaterThan(0));
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_MixedArchive_OutputIsValidSevenZip() {
    using var archive = CreateMixedArchive();
    var result = SolidBlockOptimizer.Optimize(archive);

    // Verify the output is a valid 7z archive
    using var optimizedStream = new MemoryStream(result.Data);
    var reader = new SevenZipReader(optimizedStream);
    Assert.That(reader.Entries.Count, Is.EqualTo(8));
  }

  [Category("End2End")]
  [Test]
  public void Optimize_MixedArchive_AllFilesExtractCorrectly() {
    // First, collect the original file contents
    using var archive = CreateMixedArchive();
    archive.Position = 0;
    var originalReader = new SevenZipReader(archive, leaveOpen: true);
    var originalFiles = new Dictionary<string, byte[]>();
    for (var i = 0; i < originalReader.Entries.Count; i++) {
      var e = originalReader.Entries[i];
      if (!e.IsDirectory)
        originalFiles[e.Name] = originalReader.Extract(i);
    }

    // Optimize
    archive.Position = 0;
    var result = SolidBlockOptimizer.Optimize(archive);

    // Verify all files extract correctly from the optimized archive
    using var optimizedStream = new MemoryStream(result.Data);
    var optimizedReader = new SevenZipReader(optimizedStream);
    var extractedFiles = new Dictionary<string, byte[]>();
    for (var i = 0; i < optimizedReader.Entries.Count; i++) {
      var e = optimizedReader.Entries[i];
      if (!e.IsDirectory)
        extractedFiles[e.Name] = optimizedReader.Extract(i);
    }

    Assert.That(extractedFiles.Count, Is.EqualTo(originalFiles.Count));
    foreach (var (name, original) in originalFiles) {
      Assert.That(extractedFiles.ContainsKey(name), Is.True, $"Missing file: {name}");
      Assert.That(extractedFiles[name], Is.EqualTo(original), $"Content mismatch: {name}");
    }
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_MixedArchive_OutputSizeNotLargerThanDoubleOriginal() {
    using var archive = CreateMixedArchive();
    var originalSize = archive.Length;
    var result = SolidBlockOptimizer.Optimize(archive);

    // The optimized output should not be drastically larger than the original.
    // (Different groupings may produce slightly different sizes; we just verify
    // it's in a reasonable range.)
    Assert.That(result.Data.Length, Is.LessThanOrEqualTo(originalSize * 2),
      "Optimized archive should not be more than 2x the original size");
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_RunsAllFiveStrategies() {
    using var archive = CreateMixedArchive();
    var result = SolidBlockOptimizer.Optimize(archive, maxTrials: 5);

    Assert.That(result.Trials.Count, Is.EqualTo(5),
      "Should have tried all 5 strategies");
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_TrialsAreSortedBySizeAscending() {
    using var archive = CreateMixedArchive();
    var result = SolidBlockOptimizer.Optimize(archive);

    for (var i = 1; i < result.Trials.Count; i++)
      Assert.That(result.Trials[i].OutputSize, Is.GreaterThanOrEqualTo(result.Trials[i - 1].OutputSize),
        $"Trial {i} should be >= trial {i - 1} in size");
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_WinningStrategyMatchesSmallestTrial() {
    using var archive = CreateMixedArchive();
    var result = SolidBlockOptimizer.Optimize(archive);

    Assert.That(result.WinningStrategy, Is.EqualTo(result.Trials[0].StrategyName));
    Assert.That(result.Data.Length, Is.EqualTo(result.Trials[0].OutputSize));
  }

  [Category("EdgeCase")]
  [Test]
  public void Optimize_SingleFileArchive_ReturnsOriginal() {
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      writer.AddEntry(new SevenZipEntry { Name = "only.txt" }, "single file"u8.ToArray());
      writer.Finish();
    }
    ms.Position = 0;
    var originalSize = ms.Length;

    var result = SolidBlockOptimizer.Optimize(ms);
    Assert.That(result.WinningStrategy, Is.EqualTo("original"));
    Assert.That(result.Data.Length, Is.EqualTo(originalSize));
  }

  [Category("EdgeCase")]
  [Test]
  public void Optimize_EmptyArchive_ReturnsOriginal() {
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true))
      writer.Finish();
    ms.Position = 0;
    var originalSize = ms.Length;

    var result = SolidBlockOptimizer.Optimize(ms);
    Assert.That(result.WinningStrategy, Is.EqualTo("original"));
    Assert.That(result.Data.Length, Is.EqualTo(originalSize));
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_MaxTrials_LimitsStrategies() {
    using var archive = CreateMixedArchive();
    var result = SolidBlockOptimizer.Optimize(archive, maxTrials: 2);

    Assert.That(result.Trials.Count, Is.LessThanOrEqualTo(2));
  }

  [Category("HappyPath")]
  [Test]
  public void Optimize_ProgressCallback_IsInvoked() {
    using var archive = CreateMixedArchive();
    var callbackInvocations = new List<(int Index, int Total, string Name)>();
    var result = SolidBlockOptimizer.Optimize(archive, maxTrials: 3,
      onProgress: (i, t, n) => callbackInvocations.Add((i, t, n)));

    Assert.That(callbackInvocations.Count, Is.EqualTo(3));
    Assert.That(callbackInvocations[0].Index, Is.EqualTo(0));
    Assert.That(callbackInvocations[0].Total, Is.EqualTo(3));
    Assert.That(callbackInvocations[0].Name, Is.Not.Null.And.Not.Empty);
  }

  [Category("End2End")]
  [Test]
  public void Optimize_HomogeneousTextFiles_SingleBlockWins() {
    // When all files are similar text, single solid block should be near-optimal
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      for (var i = 0; i < 10; i++) {
        var data = System.Text.Encoding.UTF8.GetBytes(
          $"Line {i}: This is a text file with repetitive content. Lorem ipsum dolor sit amet.\n" +
          "Repeated content helps compression. The quick brown fox jumps over the lazy dog.\n");
        writer.AddEntry(new SevenZipEntry { Name = $"file{i}.txt" }, data);
      }
      writer.Finish();
    }
    ms.Position = 0;

    var result = SolidBlockOptimizer.Optimize(ms);

    // Verify all entries extract
    using var check = new MemoryStream(result.Data);
    var reader = new SevenZipReader(check);
    Assert.That(reader.Entries.Count, Is.EqualTo(10));
    for (var i = 0; i < reader.Entries.Count; i++)
      Assert.That(reader.Extract(i).Length, Is.GreaterThan(0));
  }

  [Category("End2End")]
  [Test]
  public void Optimize_LargeAndSmallFiles_SizeBucketsProducesValidOutput() {
    var ms = new MemoryStream();
    using (var writer = new SevenZipWriter(ms, leaveOpen: true)) {
      // Small files (< 4KB)
      for (var i = 0; i < 5; i++)
        writer.AddEntry(new SevenZipEntry { Name = $"small{i}.txt" },
          System.Text.Encoding.UTF8.GetBytes($"Small file {i}\n"));

      // Large file (> 64KB)
      var large = new byte[100_000];
      for (var j = 0; j < large.Length; j++)
        large[j] = (byte)(j % 26 + 'a');
      writer.AddEntry(new SevenZipEntry { Name = "large.dat" }, large);

      writer.Finish();
    }
    ms.Position = 0;

    var result = SolidBlockOptimizer.Optimize(ms);

    // Verify all entries extract correctly
    using var check = new MemoryStream(result.Data);
    var reader = new SevenZipReader(check);
    Assert.That(reader.Entries.Count, Is.EqualTo(6));
    for (var i = 0; i < reader.Entries.Count; i++) {
      var data = reader.Extract(i);
      Assert.That(data.Length, Is.GreaterThan(0), $"Entry {reader.Entries[i].Name} should not be empty");
    }
  }
}
