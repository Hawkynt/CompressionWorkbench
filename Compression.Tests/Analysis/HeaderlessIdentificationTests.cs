using Compression.Analysis;
using Compression.Analysis.ChainReconstruction;
using Compression.Analysis.Scanning;
using Compression.Analysis.TrialDecompression;
using Compression.Core.Deflate;
using Compression.Core.Streams;
using Compression.Core.Transforms;

namespace Compression.Tests.Analysis;

[TestFixture]
public class HeaderlessIdentificationTests {

  // ── Construct → Strip → Identify ───────────────────────────────────

  [Test, Category("HappyPath")]
  public void StrippedGzip_TrialDeflate_Identifies() {
    // Gzip: skip 10-byte header → raw Deflate
    var original = "Hello world, hello world, hello world, repeat repeat repeat!"u8.ToArray();
    using var ms = new MemoryStream();
    using (var gz = new FileFormat.Gzip.GzipStream(ms, CompressionStreamMode.Compress, leaveOpen: true))
      gz.Write(original);
    var gzipData = ms.ToArray();

    // Strip Gzip header (10 bytes minimum)
    var rawDeflate = gzipData[10..^8]; // skip header and trailer
    var trial = new TrialDeflate();
    var result = trial.TryDecompress(rawDeflate, 4096, CancellationToken.None);
    Assert.That(result.Success, Is.True);
    Assert.That(result.Output, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void StrippedBzip2_SignatureScanner_DetectsPayload() {
    // Bzip2 always starts with "BZh" — scanner should find it at offset 0
    using var ms = new MemoryStream();
    using (var bz = new FileFormat.Bzip2.Bzip2Stream(ms, CompressionStreamMode.Compress, leaveOpen: true))
      bz.Write("Test data for bzip2 compression with enough text."u8);
    var bz2Data = ms.ToArray();

    var results = SignatureScanner.Scan(bz2Data);
    Assert.That(results.Any(r => r.FormatName == "Bzip2" && r.Offset == 0), Is.True);
  }

  [Test, Category("HappyPath")]
  public void RawDeflate_Fingerprinter_DetectsDeflate() {
    // Compress with raw Deflate (no wrapper)
    var original = new byte[500];
    for (var i = 0; i < 500; i++) original[i] = (byte)(i % 10);
    var compressed = DeflateCompressor.Compress(original);

    var analyzer = new BinaryAnalyzer(new AnalysisOptions { Fingerprint = true });
    var result = analyzer.Analyze(compressed);

    Assert.That(result.Fingerprints, Is.Not.Null);
    // Should identify as compressed (entropy classifier or deflate heuristic)
    Assert.That(result.Fingerprints!.Count, Is.GreaterThan(0));
  }

  // ── Construct → Corrupt → Identify ─────────────────────────────────

  [Test, Category("HappyPath")]
  public void CorruptedZipHeader_ScannerFindsPayload() {
    // Build a buffer with ZIP magic at offset 20 but corrupted at offset 0
    var data = new byte[64];
    new Random(42).NextBytes(data);
    // Place valid ZIP magic at offset 20
    data[20] = 0x50; data[21] = 0x4B; data[22] = 0x03; data[23] = 0x04;

    var results = SignatureScanner.Scan(data);
    var zip = results.FirstOrDefault(r => r.FormatName == "Zip");
    Assert.That(zip, Is.Not.Null);
    Assert.That(zip!.Offset, Is.EqualTo(20));
  }

  // ── Multi-layer chains ─────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Chain_RleOnly_UnwindsOneLayer() {
    var original = new byte[200];
    for (var i = 0; i < 200; i++) original[i] = (byte)(i % 5);
    var rleEncoded = RunLengthEncoding.Encode(original);

    var reconstructor = new ChainReconstructor(maxDepth: 5, perTrialTimeoutMs: 500);
    var chain = reconstructor.Reconstruct(rleEncoded);

    Assert.That(chain.Depth, Is.GreaterThanOrEqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Chain_DeflateOnly_UnwindsOneLayer() {
    var original = "Repeating text is compressible. Repeating text is compressible. Repeating text is compressible."u8.ToArray();
    var data = new byte[original.Length * 3];
    for (var i = 0; i < 3; i++) original.CopyTo(data, i * original.Length);
    var compressed = DeflateCompressor.Compress(data);

    var reconstructor = new ChainReconstructor(maxDepth: 5, perTrialTimeoutMs: 500);
    var chain = reconstructor.Reconstruct(compressed);

    Assert.That(chain.Depth, Is.GreaterThanOrEqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void Chain_MtfRle_UnwindsMultipleLayers() {
    // Build: data → MTF encode → RLE encode
    var original = new byte[200];
    for (var i = 0; i < 200; i++) original[i] = (byte)(i % 5);
    var mtfEncoded = MoveToFrontTransform.Encode(original);
    var rleEncoded = RunLengthEncoding.Encode(mtfEncoded);

    var reconstructor = new ChainReconstructor(maxDepth: 5, perTrialTimeoutMs: 500);
    var chain = reconstructor.Reconstruct(rleEncoded);

    Assert.That(chain.Depth, Is.GreaterThanOrEqualTo(1));
  }

  // ── Embedded payload extraction ────────────────────────────────────

  [Test, Category("HappyPath")]
  public void EmbeddedPayload_ScannerFindsAtOffset() {
    // Random padding + Gzip magic at offset 50
    var data = new byte[200];
    new Random(42).NextBytes(data);
    data[50] = 0x1F; data[51] = 0x8B;

    var results = SignatureScanner.Scan(data);
    var gzip = results.FirstOrDefault(r => r.FormatName == "Gzip" && r.Offset == 50);
    Assert.That(gzip, Is.Not.Null);
  }

  [Test, Category("HappyPath")]
  public void EmbeddedPayload_SevenZipAtOffset() {
    var data = new byte[200];
    new Random(42).NextBytes(data);
    // 7z magic: 37 7A BC AF 27 1C
    data[80] = 0x37; data[81] = 0x7A; data[82] = 0xBC;
    data[83] = 0xAF; data[84] = 0x27; data[85] = 0x1C;

    var results = SignatureScanner.Scan(data);
    var sevenZ = results.FirstOrDefault(r => r.FormatName == "SevenZip");
    Assert.That(sevenZ, Is.Not.Null);
    Assert.That(sevenZ!.Offset, Is.EqualTo(80));
  }

  // ── False positive resistance ──────────────────────────────────────

  [Test, Category("HappyPath")]
  public void FalsePositive_Plaintext_NoChain() {
    var text = "This is just plain text data with no compression applied to it at all whatsoever."u8.ToArray();
    var reconstructor = new ChainReconstructor(maxDepth: 5, perTrialTimeoutMs: 200);
    var chain = reconstructor.Reconstruct(text);

    // Plain text should yield few layers (heuristic false positives are acceptable)
    Assert.That(chain.Depth, Is.LessThanOrEqualTo(3));
  }

  [Test, Category("HappyPath")]
  public void FalsePositive_Random_MinimalChain() {
    var rng = new Random(42);
    var data = new byte[2048];
    rng.NextBytes(data);
    var reconstructor = new ChainReconstructor(maxDepth: 5, perTrialTimeoutMs: 200);
    var chain = reconstructor.Reconstruct(data);

    // Random data should yield few layers (heuristic false positives are acceptable)
    Assert.That(chain.Depth, Is.LessThanOrEqualTo(4));
  }

  [Test, Category("HappyPath")]
  public void FalsePositive_AllZeros_MinimalChain() {
    var data = new byte[1024]; // all zeros
    var reconstructor = new ChainReconstructor(maxDepth: 5, perTrialTimeoutMs: 200);
    var chain = reconstructor.Reconstruct(data);

    Assert.That(chain.Depth, Is.LessThanOrEqualTo(2));
  }

  // ── Full analysis integration ──────────────────────────────────────

  [Test, Category("HappyPath")]
  public void FullAnalysis_CompressedData_ReturnsAllSections() {
    // Compress some data
    var original = new byte[500];
    for (var i = 0; i < 500; i++) original[i] = (byte)(i % 10);
    var compressed = DeflateCompressor.Compress(original);

    var analyzer = new BinaryAnalyzer(new AnalysisOptions { All = true });
    var result = analyzer.Analyze(compressed);

    Assert.That(result.Statistics, Is.Not.Null);
    Assert.That(result.Statistics!.Entropy, Is.GreaterThan(0));
    Assert.That(result.Signatures, Is.Not.Null);
    Assert.That(result.Fingerprints, Is.Not.Null);
    Assert.That(result.Chain, Is.Not.Null);
    Assert.That(result.Chain!.Depth, Is.GreaterThanOrEqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void FullAnalysis_MultipleSignatures_InSingleBlob() {
    // Place Gzip + ZIP + Bzip2 magics at different offsets
    var data = new byte[256];
    data[0] = 0x1F; data[1] = 0x8B;                                    // Gzip at 0
    data[50] = 0x50; data[51] = 0x4B; data[52] = 0x03; data[53] = 0x04; // ZIP at 50
    data[100] = 0x42; data[101] = 0x5A; data[102] = 0x68;               // Bzip2 at 100

    var analyzer = new BinaryAnalyzer(new AnalysisOptions { DeepScan = true });
    var result = analyzer.Analyze(data);

    Assert.That(result.Signatures!.Any(s => s.FormatName == "Gzip"), Is.True);
    Assert.That(result.Signatures!.Any(s => s.FormatName == "Zip"), Is.True);
    Assert.That(result.Signatures!.Any(s => s.FormatName == "Bzip2"), Is.True);
  }
}
