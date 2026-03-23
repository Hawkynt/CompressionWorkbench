using Compression.Analysis.TrialDecompression;
using Compression.Core.Deflate;
using Compression.Core.Streams;
using Compression.Core.Transforms;

namespace Compression.Tests.Analysis;

[TestFixture]
public class TrialDecompressorTests {

  [Test, Category("HappyPath")]
  public void TrialDeflate_ValidDeflate_Succeeds() {
    // Compress with Deflate, then trial-decompress
    var original = "Hello world, this is a test of trial decompression!"u8.ToArray();
    var compressed = DeflateCompressor.Compress(original);
    var trial = new TrialDeflate();
    var result = trial.TryDecompress(compressed, 1024, CancellationToken.None);
    Assert.That(result.Success, Is.True);
    Assert.That(result.Output, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void TrialRle_ValidRle_Succeeds() {
    // RLE encode, then trial-decode
    var original = new byte[100];
    for (var i = 0; i < 100; i++) original[i] = (byte)(i % 5);
    var encoded = RunLengthEncoding.Encode(original);
    var trial = new TrialRle();
    var result = trial.TryDecompress(encoded, 1024, CancellationToken.None);
    Assert.That(result.Success, Is.True);
    Assert.That(result.Output, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void TrialMtf_ValidMtf_Succeeds() {
    var original = "aaabbbccc"u8.ToArray();
    var encoded = MoveToFrontTransform.Encode(original);
    var trial = new TrialMtf();
    var result = trial.TryDecompress(encoded, 1024, CancellationToken.None);
    Assert.That(result.Success, Is.True);
    Assert.That(result.Output, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void TrialBwt_ValidBwt_Succeeds() {
    var original = "banana"u8.ToArray();
    var (bwt, idx) = BurrowsWheelerTransform.Forward(original);
    var trial = new TrialBwt();
    var result = trial.TryDecompress(bwt, 1024, CancellationToken.None);
    // BWT trial probes multiple indices — should succeed on at least one
    Assert.That(result.Success, Is.True);
    // The output should be one of the rotations of "banana" (depending on which index hit)
    Assert.That(result.OutputSize, Is.EqualTo(original.Length));
  }

  [Test, Category("HappyPath")]
  public void TrialGzip_ValidGzip_Succeeds() {
    var original = "The quick brown fox"u8.ToArray();
    // Compress with Gzip
    using var ms = new MemoryStream();
    using (var gz = new FileFormat.Gzip.GzipStream(ms, CompressionStreamMode.Compress, leaveOpen: true))
      gz.Write(original);
    var compressed = ms.ToArray();

    var trial = new TrialFormat("Gzip", s => new FileFormat.Gzip.GzipStream(s, CompressionStreamMode.Decompress, leaveOpen: true));
    var result = trial.TryDecompress(compressed, 1024, CancellationToken.None);
    Assert.That(result.Success, Is.True);
    Assert.That(result.Output, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void TryAll_ReturnsSuccessfulAttempts() {
    // Create RLE-encoded data — at least RLE trial should succeed
    var original = new byte[50];
    for (var i = 0; i < 50; i++) original[i] = (byte)(i % 3);
    var encoded = RunLengthEncoding.Encode(original);

    var decompressor = new TrialDecompressor(perTrialTimeoutMs: 500);
    var results = decompressor.TryAll(encoded);
    // At least RLE should succeed
    Assert.That(results.Any(r => r.Algorithm == "RLE" && r.Success), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void TrialDeflate_InvalidData_Fails() {
    var garbage = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
    var trial = new TrialDeflate();
    var result = trial.TryDecompress(garbage, 1024, CancellationToken.None);
    Assert.That(result.Success, Is.False);
  }

  [Test, Category("HappyPath")]
  public void TrialRegistry_HasStrategies() {
    Assert.That(TrialRegistry.All.Count, Is.GreaterThan(10));
  }
}
