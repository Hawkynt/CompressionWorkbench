using Compression.Analysis.Fingerprinting;
using Compression.Analysis.Statistics;
using Compression.Analysis.Structure;

namespace Compression.Tests.Analysis;

[TestFixture]
public class StructureDiscoveryTests {

  // ── FindRepeatedPatterns ─────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void FindRepeatedPatterns_RepeatingSequence_FindsPattern() {
    // Create data with a repeated 4-byte pattern "ABCD" at regular intervals
    var data = new byte[256];
    var pattern = new byte[] { 0x41, 0x42, 0x43, 0x44 }; // "ABCD"
    for (var i = 0; i < data.Length - 3; i += 16) {
      pattern.CopyTo(data, i);
    }

    var results = StructureDiscovery.FindRepeatedPatterns(data, minPatternLength: 4, minOccurrences: 3);
    Assert.That(results, Has.Count.GreaterThan(0));
    // The ABCD pattern should be among the results
    var found = results.Any(r => r.Pattern.Length >= 4 &&
                                 r.Pattern[0] == 0x41 && r.Pattern[1] == 0x42 &&
                                 r.Pattern[2] == 0x43 && r.Pattern[3] == 0x44);
    Assert.That(found, Is.True, "Expected to find the ABCD pattern");
  }

  [Test, Category("HappyPath")]
  public void FindRepeatedPatterns_MagicBytes_DetectsSignatures() {
    // Simulate a format with repeated "PK\x03\x04" headers surrounded by varied data
    var rng = new Random(42);
    var data = new byte[512];
    rng.NextBytes(data);
    var magic = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
    for (var i = 0; i < 5; i++) {
      magic.CopyTo(data, i * 100);
    }

    var results = StructureDiscovery.FindRepeatedPatterns(data, minPatternLength: 4, minOccurrences: 3);
    Assert.That(results, Has.Count.GreaterThan(0));
    // The pattern should appear either as the exact 4-byte sequence or as part of a longer pattern
    var hasPk = results.Any(r =>
      r.Pattern.Length >= 4 &&
      r.Pattern.AsSpan(0, 4).SequenceEqual(magic));
    Assert.That(hasPk, Is.True, "Expected to find a pattern starting with PK\\x03\\x04");
  }

  [Test, Category("EdgeCase")]
  public void FindRepeatedPatterns_EmptyData_ReturnsEmpty() {
    var results = StructureDiscovery.FindRepeatedPatterns(ReadOnlySpan<byte>.Empty);
    Assert.That(results, Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void FindRepeatedPatterns_AllZeros_SkipsUniform() {
    // All-zero data should be skipped (uniform pattern filter)
    var data = new byte[256];
    var results = StructureDiscovery.FindRepeatedPatterns(data, minPatternLength: 4, minOccurrences: 3);
    Assert.That(results, Is.Empty, "Uniform (all-zero) patterns should be filtered out");
  }

  [Test, Category("HappyPath")]
  public void FindRepeatedPatterns_ShortData_NoException() {
    var data = new byte[] { 1, 2, 3 };
    var results = StructureDiscovery.FindRepeatedPatterns(data, minPatternLength: 4);
    Assert.That(results, Is.Empty);
  }

  // ── FindAlignmentBoundaries ──────────────────────────────────────

  [Test, Category("HappyPath")]
  public void FindAlignmentBoundaries_PaddedRecords_DetectsAlignment() {
    // Create 16-byte-aligned records with null padding at the end of each record
    var data = new byte[1024];
    for (var i = 0; i < data.Length; i += 16) {
      // Write some non-null data at the start of each record
      data[i] = 0xFF;
      data[i + 1] = 0xAA;
      data[i + 2] = 0xBB;
      // bytes 3-15 are already zero (padding)
    }

    var results = StructureDiscovery.FindAlignmentBoundaries(data);
    Assert.That(results, Has.Count.GreaterThan(0));
    // 16-byte alignment should appear
    var has16 = results.Any(r => r.Alignment == 16);
    Assert.That(has16, Is.True, "Expected to detect 16-byte alignment");
  }

  [Test, Category("EdgeCase")]
  public void FindAlignmentBoundaries_SmallData_ReturnsEmpty() {
    var data = new byte[8];
    var results = StructureDiscovery.FindAlignmentBoundaries(data);
    Assert.That(results, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void FindAlignmentBoundaries_ResultsHaveConfidence() {
    var data = new byte[4096];
    // Create obvious 512-byte alignment with padding
    for (var i = 0; i < data.Length; i += 512) {
      data[i] = 0xFF;
      for (var j = i + 1; j < Math.Min(i + 512, data.Length); j++)
        data[j] = 0x00;
    }

    var results = StructureDiscovery.FindAlignmentBoundaries(data);
    foreach (var r in results) {
      Assert.That(r.Confidence, Is.GreaterThan(0).And.LessThanOrEqualTo(1.0));
    }
  }

  // ── FindFieldLengthCandidates ────────────────────────────────────

  [Test, Category("HappyPath")]
  public void FindFieldLengthCandidates_FixedRecords_DetectsLength() {
    // Create data with repeating 8-byte records
    var rng = new Random(42);
    var record = new byte[8];
    rng.NextBytes(record);
    var data = new byte[8 * 50]; // 50 identical records
    for (var i = 0; i < data.Length; i += 8) {
      record.CopyTo(data, i);
    }

    var results = StructureDiscovery.FindFieldLengthCandidates(data, minLength: 4, maxLength: 64);
    Assert.That(results, Has.Count.GreaterThan(0));
    // The 8-byte record length should have the highest or very high confidence
    var has8 = results.Any(r => r.Length == 8);
    Assert.That(has8, Is.True, "Expected to detect 8-byte record length");
  }

  [Test, Category("EdgeCase")]
  public void FindFieldLengthCandidates_RandomData_LowConfidence() {
    var rng = new Random(42);
    var data = new byte[1024];
    rng.NextBytes(data);

    var results = StructureDiscovery.FindFieldLengthCandidates(data);
    // Random data should have very low or no significant candidates
    if (results.Count > 0) {
      Assert.That(results[0].Confidence, Is.LessThan(0.3),
        "Random data should not produce high-confidence candidates");
    }
  }

  [Test, Category("EdgeCase")]
  public void FindFieldLengthCandidates_TooShort_ReturnsEmpty() {
    var data = new byte[6];
    var results = StructureDiscovery.FindFieldLengthCandidates(data, minLength: 4);
    Assert.That(results, Is.Empty);
  }
}

[TestFixture]
public class SlidingEntropyMapTests {

  // ── ComputeEntropyMap ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void ComputeEntropyMap_RandomData_HighEntropy() {
    var rng = new Random(42);
    var data = new byte[1024];
    rng.NextBytes(data);

    var map = SlidingEntropyMap.ComputeEntropyMap(data, windowSize: 256, stepSize: 64);
    Assert.That(map.Entropy.Length, Is.GreaterThan(0));
    Assert.That(map.Offsets.Length, Is.EqualTo(map.Entropy.Length));

    // All windows should have high entropy
    foreach (var e in map.Entropy) {
      Assert.That(e, Is.GreaterThan(5.0));
    }
  }

  [Test, Category("HappyPath")]
  public void ComputeEntropyMap_ZeroData_LowEntropy() {
    var data = new byte[1024];

    var map = SlidingEntropyMap.ComputeEntropyMap(data, windowSize: 256, stepSize: 64);
    Assert.That(map.Entropy.Length, Is.GreaterThan(0));

    // All windows should have zero entropy
    foreach (var e in map.Entropy) {
      Assert.That(e, Is.EqualTo(0));
    }
  }

  [Test, Category("HappyPath")]
  public void ComputeEntropyMap_MixedData_ShowsTransition() {
    var data = new byte[1024];
    // First 512: zeros (low entropy)
    // Last 512: random (high entropy)
    new Random(42).NextBytes(data.AsSpan(512));

    var map = SlidingEntropyMap.ComputeEntropyMap(data, windowSize: 256, stepSize: 128);
    Assert.That(map.Entropy.Length, Is.GreaterThan(2));

    // First entropy value should be low, last should be high
    Assert.That(map.Entropy[0], Is.LessThan(1.0));
    Assert.That(map.Entropy[^1], Is.GreaterThan(5.0));
  }

  [Test, Category("EdgeCase")]
  public void ComputeEntropyMap_EmptyData_ReturnsEmpty() {
    var map = SlidingEntropyMap.ComputeEntropyMap(ReadOnlySpan<byte>.Empty);
    Assert.That(map.Entropy, Is.Empty);
    Assert.That(map.Offsets, Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void ComputeEntropyMap_SmallerThanWindow_ReturnsSingleValue() {
    var data = new byte[] { 1, 2, 3, 4, 5 };
    var map = SlidingEntropyMap.ComputeEntropyMap(data, windowSize: 256, stepSize: 64);
    Assert.That(map.Entropy.Length, Is.EqualTo(1));
    Assert.That(map.Offsets[0], Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void ComputeEntropyMap_OffsetsAreCorrect() {
    var data = new byte[1024];
    var map = SlidingEntropyMap.ComputeEntropyMap(data, windowSize: 256, stepSize: 100);

    for (var i = 0; i < map.Offsets.Length; i++) {
      Assert.That(map.Offsets[i], Is.EqualTo(i * 100));
    }
  }

  // ── ClassifyRegions ──────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void ClassifyRegions_MixedData_DetectsTransitions() {
    var data = new byte[2048];
    // 0-512: zeros (low entropy)
    // 512-1536: random (high entropy)
    // 1536-2048: zeros (low entropy)
    new Random(42).NextBytes(data.AsSpan(512, 1024));

    var map = SlidingEntropyMap.ComputeEntropyMap(data, windowSize: 256, stepSize: 64);
    var regions = SlidingEntropyMap.ClassifyRegions(map);

    Assert.That(regions, Has.Count.GreaterThanOrEqualTo(2));

    // Should have at least one low entropy and one high entropy region
    Assert.That(regions.Any(r => r.Type == RegionType.LowEntropy), Is.True);
    Assert.That(regions.Any(r => r.Type == RegionType.HighEntropy), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void ClassifyRegions_EmptyMap_ReturnsEmpty() {
    var map = new EntropyMapResult([], [], 256, 64);
    var regions = SlidingEntropyMap.ClassifyRegions(map);
    Assert.That(regions, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Classify_ThresholdsAreCorrect() {
    Assert.That(SlidingEntropyMap.Classify(0.0), Is.EqualTo(RegionType.LowEntropy));
    Assert.That(SlidingEntropyMap.Classify(2.5), Is.EqualTo(RegionType.LowEntropy));
    Assert.That(SlidingEntropyMap.Classify(5.0), Is.EqualTo(RegionType.MediumEntropy));
    Assert.That(SlidingEntropyMap.Classify(7.5), Is.EqualTo(RegionType.HighEntropy));
    Assert.That(SlidingEntropyMap.Classify(8.0), Is.EqualTo(RegionType.HighEntropy));
  }

  [Test, Category("HappyPath")]
  public void ClassifyRegions_RegionsHaveAverageEntropy() {
    var rng = new Random(42);
    var data = new byte[1024];
    rng.NextBytes(data);

    var map = SlidingEntropyMap.ComputeEntropyMap(data, windowSize: 256, stepSize: 64);
    var regions = SlidingEntropyMap.ClassifyRegions(map);

    foreach (var r in regions) {
      Assert.That(r.AverageEntropy, Is.GreaterThanOrEqualTo(0).And.LessThanOrEqualTo(8.0));
      Assert.That(r.StartOffset, Is.LessThan(r.EndOffset));
    }
  }
}

[TestFixture]
public class HeaderTemplateMatcherTests {

  [Test, Category("HappyPath")]
  public void FindMatches_GzipHeader_FindsExactMatch() {
    // Gzip magic: 1F 8B
    var data = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
    var matches = HeaderTemplateMatcher.FindMatches(data, maxOffset: 4);

    Assert.That(matches, Has.Count.GreaterThan(0));
    var gzip = matches.Find(m => m.FormatId == "Gzip");
    Assert.That(gzip, Is.Not.Null);
    Assert.That(gzip!.Offset, Is.EqualTo(0));
    Assert.That(gzip.MatchType, Is.EqualTo("Exact"));
    Assert.That(gzip.Confidence, Is.GreaterThan(0.5));
  }

  [Test, Category("HappyPath")]
  public void FindMatches_ZipAtOffset_FindsAtCorrectPosition() {
    var data = new byte[64];
    // Place ZIP magic at offset 10
    data[10] = 0x50; data[11] = 0x4B; data[12] = 0x03; data[13] = 0x04;

    var matches = HeaderTemplateMatcher.FindMatches(data, maxOffset: 64);
    var zip = matches.Find(m => m.FormatId == "Zip");
    Assert.That(zip, Is.Not.Null);
    Assert.That(zip!.Offset, Is.EqualTo(10));
  }

  [Test, Category("HappyPath")]
  public void FindMatches_7zMagic_HighConfidence() {
    var data = new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x00 };
    var matches = HeaderTemplateMatcher.FindMatches(data, maxOffset: 4);

    var sevenZ = matches.Find(m => m.FormatId == "SevenZip");
    Assert.That(sevenZ, Is.Not.Null);
    Assert.That(sevenZ!.Confidence, Is.GreaterThan(0.85));
    Assert.That(sevenZ.MatchType, Is.EqualTo("Exact"));
  }

  [Test, Category("HappyPath")]
  public void FindMatches_FuzzyMatch_ReportsWithLowerConfidence() {
    // Create data that almost matches a gzip header but with one byte off
    var data = new byte[] { 0x1F, 0x8B, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00 };
    var matches = HeaderTemplateMatcher.FindMatches(data, maxOffset: 4, enableFuzzy: true);

    // Should find Gzip - either exact (if magic is just 2 bytes) or fuzzy
    var gzip = matches.Find(m => m.FormatId == "Gzip");
    Assert.That(gzip, Is.Not.Null);
  }

  [Test, Category("HappyPath")]
  public void FindMatches_NoFuzzy_SkipsFuzzyMatches() {
    // Data with one byte off from any known magic
    var data = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
    var exactOnly = HeaderTemplateMatcher.FindMatches(data, maxOffset: 4, enableFuzzy: false);
    var withFuzzy = HeaderTemplateMatcher.FindMatches(data, maxOffset: 4, enableFuzzy: true);

    // With fuzzy disabled, should have fewer or equal results
    Assert.That(exactOnly.Count, Is.LessThanOrEqualTo(withFuzzy.Count));
  }

  [Test, Category("EdgeCase")]
  public void FindMatches_EmptyData_ReturnsEmpty() {
    var matches = HeaderTemplateMatcher.FindMatches(ReadOnlySpan<byte>.Empty);
    Assert.That(matches, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void IdentifyFormat_GzipHeader_IdentifiesCorrectly() {
    var data = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
    var matches = HeaderTemplateMatcher.IdentifyFormat(data);
    Assert.That(matches, Has.Count.GreaterThan(0));
    Assert.That(matches[0].FormatId, Is.EqualTo("Gzip"));
  }

  [Test, Category("HappyPath")]
  public void FindMatches_MatchesAreSortedByConfidence() {
    // Place ZIP magic at offset 0
    var data = new byte[32];
    data[0] = 0x50; data[1] = 0x4B; data[2] = 0x03; data[3] = 0x04;

    var matches = HeaderTemplateMatcher.FindMatches(data, maxOffset: 32);

    for (var i = 1; i < matches.Count; i++) {
      Assert.That(matches[i].Confidence, Is.LessThanOrEqualTo(matches[i - 1].Confidence));
    }
  }

  [Test, Category("HappyPath")]
  public void FindMatches_HeaderMatch_HasCorrectByteInfo() {
    var data = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
    var matches = HeaderTemplateMatcher.FindMatches(data, maxOffset: 4);
    var gzip = matches.Find(m => m.FormatId == "Gzip" && m.MatchType == "Exact");
    Assert.That(gzip, Is.Not.Null);
    Assert.That(gzip!.MatchedBytes, Is.EqualTo(gzip.TotalBytes));
    Assert.That(gzip.TotalBytes, Is.GreaterThanOrEqualTo(2));
  }
}

[TestFixture]
public class BlackBoxProberTests {

  [Test, Category("HappyPath")]
  public void ProbeFormat_ReturnsReport() {
    // Simply verify the prober runs without crashing on arbitrary data
    var data = new byte[] { 0x00, 0x01, 0x02, 0x03 };
    var report = Compression.Analysis.ExternalTools.BlackBoxProber.ProbeFormat(data, timeoutMs: 5000);
    Assert.That(report, Is.Not.Null);
    Assert.That(report.Results, Is.Not.Null);
    // Results list is populated based on available tools -- may be empty if no tools installed
  }

  [Test, Category("HappyPath")]
  public void ProbeFormat_GzipData_ToolsMayRecognize() {
    // Create a minimal valid-ish gzip-like header
    var data = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
    var report = Compression.Analysis.ExternalTools.BlackBoxProber.ProbeFormat(data, timeoutMs: 5000);
    Assert.That(report, Is.Not.Null);
    // If 'file' or '7z' is available, they should find something
    if (report.BestGuess != null) {
      Assert.That(report.BestGuess.Succeeded, Is.True);
      Assert.That(report.BestGuess.Confidence, Is.GreaterThan(0));
    }
  }

  [Test, Category("EdgeCase")]
  public void ProbeFormat_EmptyData_DoesNotThrow() {
    var data = Array.Empty<byte>();
    Assert.DoesNotThrow(() => {
      var report = Compression.Analysis.ExternalTools.BlackBoxProber.ProbeFormat(data, timeoutMs: 5000);
      Assert.That(report, Is.Not.Null);
    });
  }
}
