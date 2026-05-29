using Compression.Core.Statistics;

namespace Compression.Tests.Statistics;

[TestFixture]
public class FileSimilarityGrouperTests {

  [Test, Category("HappyPath")]
  public void ComputeFingerprint_AllZeros_HasZeroEntropy() {
    var data = new byte[1024];
    var fp = FileSimilarityGrouper.ComputeFingerprint(data);
    Assert.That(fp.Entropy, Is.EqualTo(0.0).Within(0.001));
    Assert.That(fp.BigramHistogram.Length, Is.EqualTo(256));
    Assert.That(fp.SampleSize, Is.EqualTo(1024));
  }

  [Test, Category("HappyPath")]
  public void ComputeFingerprint_Random_HasHighEntropy() {
    var rng = new Random(42);
    var data = new byte[4096];
    rng.NextBytes(data);
    var fp = FileSimilarityGrouper.ComputeFingerprint(data);
    Assert.That(fp.Entropy, Is.GreaterThan(7.5));
    Assert.That(fp.ChiSquare, Is.LessThan(400)); // near-uniform distribution
  }

  [Test, Category("HappyPath")]
  public void ComputeFingerprint_EnglishText_HasMediumEntropy() {
    var text = string.Join(' ', Enumerable.Repeat(
      "The quick brown fox jumps over the lazy dog. " +
      "Pack my box with five dozen liquor jugs.", 20));
    var data = System.Text.Encoding.UTF8.GetBytes(text);
    var fp = FileSimilarityGrouper.ComputeFingerprint(data);
    // English text: entropy ~4-5, high chi-square (non-uniform)
    Assert.That(fp.Entropy, Is.InRange(3.0, 6.0));
    Assert.That(fp.ChiSquare, Is.GreaterThan(500));
  }

  [Test, Category("HappyPath")]
  public void ComputeFingerprint_CSharpSource_HasMediumEntropy() {
    var source = string.Join('\n', Enumerable.Repeat(
      "public static void Main(string[] args) {\n" +
      "  Console.WriteLine(\"Hello, World!\");\n" +
      "  var x = args.Length > 0 ? int.Parse(args[0]) : 42;\n" +
      "  for (var i = 0; i < x; i++) Console.Write(i);\n" +
      "}", 10));
    var data = System.Text.Encoding.UTF8.GetBytes(source);
    var fp = FileSimilarityGrouper.ComputeFingerprint(data);
    Assert.That(fp.Entropy, Is.InRange(3.0, 6.0));
    Assert.That(fp.ChiSquare, Is.GreaterThan(500));
  }

  [Test, Category("HappyPath")]
  public void ComputeFingerprint_Empty_ReturnsZeroFingerprint() {
    var fp = FileSimilarityGrouper.ComputeFingerprint([]);
    Assert.That(fp.Entropy, Is.EqualTo(0.0));
    Assert.That(fp.ChiSquare, Is.EqualTo(0.0));
    Assert.That(fp.SampleSize, Is.EqualTo(0));
    Assert.That(fp.BigramHistogram.Length, Is.EqualTo(256));
  }

  [Test, Category("HappyPath")]
  public void ComputeFingerprint_LargeFile_SamplesFirst64KB() {
    var data = new byte[256 * 1024]; // 256 KB
    Array.Fill(data, (byte)0xAA);
    var fp = FileSimilarityGrouper.ComputeFingerprint(data);
    Assert.That(fp.SampleSize, Is.EqualTo(64 * 1024));
  }

  [Test, Category("HappyPath")]
  public void Distance_IdenticalFingerprints_IsZero() {
    var data = System.Text.Encoding.UTF8.GetBytes("Hello, World! This is some test text.");
    var fp1 = FileSimilarityGrouper.ComputeFingerprint(data);
    var fp2 = FileSimilarityGrouper.ComputeFingerprint(data);
    var dist = FileSimilarityGrouper.Distance(fp1, fp2);
    Assert.That(dist, Is.EqualTo(0.0).Within(0.001));
  }

  [Test, Category("HappyPath")]
  public void Distance_TextVsText_IsSmall() {
    var text1 = System.Text.Encoding.UTF8.GetBytes(
      string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 50)));
    var text2 = System.Text.Encoding.UTF8.GetBytes(
      string.Join(' ', Enumerable.Repeat("Pack my box with five dozen liquor jugs.", 50)));

    var fp1 = FileSimilarityGrouper.ComputeFingerprint(text1);
    var fp2 = FileSimilarityGrouper.ComputeFingerprint(text2);
    var textTextDist = FileSimilarityGrouper.Distance(fp1, fp2);

    Assert.That(textTextDist, Is.LessThan(0.35), "Two English text samples should be fairly similar");
  }

  [Test, Category("HappyPath")]
  public void Distance_TextVsRandom_IsLarge() {
    var text = System.Text.Encoding.UTF8.GetBytes(
      string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 50)));
    var rng = new Random(42);
    var random = new byte[2048];
    rng.NextBytes(random);

    var fpText = FileSimilarityGrouper.ComputeFingerprint(text);
    var fpRandom = FileSimilarityGrouper.ComputeFingerprint(random);
    var textRandomDist = FileSimilarityGrouper.Distance(fpText, fpRandom);

    Assert.That(textRandomDist, Is.GreaterThan(0.3), "Text vs random should be very different");
  }

  [Test, Category("HappyPath")]
  public void Distance_TextVsText_LessThan_TextVsRandom() {
    var text1 = System.Text.Encoding.UTF8.GetBytes(
      string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 50)));
    var text2 = System.Text.Encoding.UTF8.GetBytes(
      string.Join(' ', Enumerable.Repeat("A quick movement of the enemy will jeopardize six gunboats.", 50)));
    var rng = new Random(42);
    var random = new byte[2048];
    rng.NextBytes(random);

    var fpText1 = FileSimilarityGrouper.ComputeFingerprint(text1);
    var fpText2 = FileSimilarityGrouper.ComputeFingerprint(text2);
    var fpRandom = FileSimilarityGrouper.ComputeFingerprint(random);

    var textTextDist = FileSimilarityGrouper.Distance(fpText1, fpText2);
    var textRandomDist = FileSimilarityGrouper.Distance(fpText1, fpRandom);

    Assert.That(textTextDist, Is.LessThan(textRandomDist),
      $"text-text={textTextDist:F4} should be < text-random={textRandomDist:F4}");
  }

  [Test, Category("HappyPath")]
  public void GroupBySimilarity_SimilarFilesTogether() {
    // Two English texts + two all-zero blocks (clearly distinct from text)
    var text1 = System.Text.Encoding.UTF8.GetBytes(
      string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 50)));
    var text2 = System.Text.Encoding.UTF8.GetBytes(
      string.Join(' ', Enumerable.Repeat("Pack my box with five dozen liquor jugs.", 50)));

    var zeros1 = new byte[2048];
    var zeros2 = new byte[2048];
    Array.Fill(zeros1, (byte)0x00);
    Array.Fill(zeros2, (byte)0x01);

    var files = new[] { text1, text2, zeros1, zeros2 };
    var groups = FileSimilarityGrouper.GroupBySimilarity(files, maxGroups: 2, maxGroupSize: 1_000_000);

    Assert.That(groups.Count, Is.EqualTo(2));

    // Find which group contains text1 (index 0) and check text2 (index 1) is with it
    var groupWithText1 = groups.First(g => g.Contains(0));
    Assert.That(groupWithText1, Does.Contain(1),
      "Both text files should be in the same cluster");

    // Constant-byte files should be in the other group
    var groupWithZeros1 = groups.First(g => g.Contains(2));
    Assert.That(groupWithZeros1, Does.Contain(3),
      "Both constant-byte files should be in the same cluster");
  }

  [Test, Category("HappyPath")]
  public void GroupBySimilarity_MaxGroupSizeHonored() {
    var file1 = new byte[500];
    var file2 = new byte[500];
    var file3 = new byte[500];
    Array.Fill(file1, (byte)'A');
    Array.Fill(file2, (byte)'A');
    Array.Fill(file3, (byte)'A');

    // All identical content, but maxGroupSize=600 means at most 1 file per group
    var groups = FileSimilarityGrouper.GroupBySimilarity(
      [file1, file2, file3], maxGroups: 1, maxGroupSize: 600);

    // Each group should have total size <= 600
    foreach (var group in groups) {
      var groupSize = group.Sum(i => new[] { file1, file2, file3 }[i].Length);
      Assert.That(groupSize, Is.LessThanOrEqualTo(600));
    }

    Assert.That(groups.Count, Is.GreaterThanOrEqualTo(3),
      "With maxGroupSize=600, each 500-byte file must be alone");
  }

  [Test, Category("HappyPath")]
  public void GroupBySimilarity_SingleFile_ReturnsOneGroup() {
    var file = new byte[100];
    Array.Fill(file, (byte)42);

    var groups = FileSimilarityGrouper.GroupBySimilarity([file], maxGroups: 5, maxGroupSize: 1_000_000);

    Assert.That(groups.Count, Is.EqualTo(1));
    Assert.That(groups[0], Is.EquivalentTo(new[] { 0 }));
  }

  [Test, Category("EdgeCase")]
  public void GroupBySimilarity_EmptyInput_ReturnsEmpty() {
    var groups = FileSimilarityGrouper.GroupBySimilarity([], maxGroups: 5, maxGroupSize: 1_000_000);
    Assert.That(groups, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void GroupBySimilarity_AllFilesReturned() {
    var rng = new Random(123);
    var files = new byte[10][];
    for (var i = 0; i < 10; i++) {
      files[i] = new byte[200];
      rng.NextBytes(files[i]);
    }

    var groups = FileSimilarityGrouper.GroupBySimilarity(files, maxGroups: 3, maxGroupSize: 10_000);
    var allIndices = groups.SelectMany(g => g).OrderBy(i => i).ToList();
    Assert.That(allIndices, Is.EquivalentTo(Enumerable.Range(0, 10)),
      "Every file index must appear exactly once");
  }

  [Test, Category("HappyPath")]
  public void Distance_ZerosVsRandom_IsLarge() {
    var zeros = new byte[2048];
    var rng = new Random(42);
    var random = new byte[2048];
    rng.NextBytes(random);

    var fpZeros = FileSimilarityGrouper.ComputeFingerprint(zeros);
    var fpRandom = FileSimilarityGrouper.ComputeFingerprint(random);
    var dist = FileSimilarityGrouper.Distance(fpZeros, fpRandom);

    Assert.That(dist, Is.GreaterThan(0.3), "All-zeros vs random should be very different");
  }

  [Test, Category("HappyPath")]
  public void BigramHistogram_Sums_To_Approximately_One() {
    var rng = new Random(42);
    var data = new byte[4096];
    rng.NextBytes(data);
    var fp = FileSimilarityGrouper.ComputeFingerprint(data);
    var sum = fp.BigramHistogram.Sum();
    Assert.That(sum, Is.EqualTo(1.0).Within(0.01),
      "Normalized bigram histogram should sum to ~1.0");
  }
}
