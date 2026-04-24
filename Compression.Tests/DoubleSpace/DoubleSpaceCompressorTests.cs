using System.Text;
using Compression.Core.BuildingBlocks;

namespace Compression.Tests.DoubleSpace;

/// <summary>
/// Building-block tests for the DoubleSpace (DBLS) JM and DriveSpace (DVRS) LZ
/// compressors. These cover round-trip byte-exactness on three archetypal
/// inputs plus a few edge cases — they are the correctness gate for the
/// CVF-level integration (<see cref="DoubleSpaceTests"/>).
/// </summary>
[TestFixture]
public class DoubleSpaceCompressorTests {

  // =========================================================================
  //                             DoubleSpace (DBLS)
  // =========================================================================

  [Test, Category("RoundTrip")]
  public void DoubleSpace_RoundTrip_RandomBytes_64KiB() {
    var rng = new Random(12345);
    var original = new byte[64 * 1024];
    rng.NextBytes(original);

    var bb = new DoubleSpaceCompressor();
    var compressed = bb.Compress(original);
    var decompressed = bb.Decompress(compressed);

    Assert.That(decompressed, Is.EqualTo(original));
    // Random input is not compressible, so output is at least as big as input
    // plus token overhead — just assert decode was exact.
  }

  [Test, Category("RoundTrip")]
  public void DoubleSpace_RoundTrip_RepetitiveText() {
    var phrase = "The quick brown fox jumps over the lazy dog. ";
    var sb = new StringBuilder(phrase.Length * 1000);
    for (var i = 0; i < 1000; i++)
      sb.Append(phrase);
    var original = Encoding.ASCII.GetBytes(sb.ToString());

    var bb = new DoubleSpaceCompressor();
    var compressed = bb.Compress(original);
    var decompressed = bb.Decompress(compressed);

    Assert.That(decompressed, Is.EqualTo(original));
    Assert.That(compressed.Length, Is.LessThan(original.Length),
      "repetitive input must shrink after JM compression");
  }

  [Test, Category("RoundTrip")]
  public void DoubleSpace_RoundTrip_AllZeros_4KiB() {
    var original = new byte[4096];

    var bb = new DoubleSpaceCompressor();
    var compressed = bb.Compress(original);
    var decompressed = bb.Decompress(compressed);

    Assert.That(decompressed, Is.EqualTo(original));
    Assert.That(compressed.Length, Is.LessThan(original.Length / 4),
      "all-zero input should compress to a fraction of the original");
  }

  [Test, Category("EdgeCase")]
  public void DoubleSpace_Empty() {
    var bb = new DoubleSpaceCompressor();
    Assert.That(bb.Decompress(bb.Compress([])), Is.Empty);
  }

  [Test, Category("EdgeCase")]
  public void DoubleSpace_SingleByte() {
    var bb = new DoubleSpaceCompressor();
    var original = new byte[] { 0x42 };
    Assert.That(bb.Decompress(bb.Compress(original)), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase")]
  public void DoubleSpace_AlternatingPattern() {
    var original = new byte[2048];
    for (var i = 0; i < original.Length; i++)
      original[i] = (byte)(i & 1);
    var bb = new DoubleSpaceCompressor();
    var compressed = bb.Compress(original);
    Assert.That(bb.Decompress(compressed), Is.EqualTo(original));
    Assert.That(compressed.Length, Is.LessThan(original.Length / 2));
  }

  [Test, Category("EdgeCase")]
  public void DoubleSpace_LongRun_SameByte() {
    // Tests the max-length (323) code path via extended length encoding.
    var original = new byte[1024];
    Array.Fill(original, (byte)0xA5);
    var bb = new DoubleSpaceCompressor();
    var compressed = bb.Compress(original);
    Assert.That(bb.Decompress(compressed), Is.EqualTo(original));
    Assert.That(compressed.Length, Is.LessThan(100));
  }

  [Test, Category("HappyPath")]
  public void DoubleSpace_Descriptor_Properties() {
    var bb = new DoubleSpaceCompressor();
    Assert.That(bb.Id, Is.EqualTo("BB_DoubleSpace"));
    Assert.That(bb.DisplayName, Does.Contain("DoubleSpace"));
    Assert.That(bb.Description, Does.Contain("4 KiB"));
    Assert.That(bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
  }

  // =========================================================================
  //                             DriveSpace (DVRS)
  // =========================================================================

  [Test, Category("RoundTrip")]
  public void DriveSpace_RoundTrip_RandomBytes_64KiB() {
    var rng = new Random(98765);
    var original = new byte[64 * 1024];
    rng.NextBytes(original);

    var bb = new DriveSpaceCompressor();
    var decompressed = bb.Decompress(bb.Compress(original));
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("RoundTrip")]
  public void DriveSpace_RoundTrip_RepetitiveText() {
    var phrase = "DriveSpace is a disk compression utility from Microsoft. ";
    var sb = new StringBuilder(phrase.Length * 1000);
    for (var i = 0; i < 1000; i++)
      sb.Append(phrase);
    var original = Encoding.ASCII.GetBytes(sb.ToString());

    var bb = new DriveSpaceCompressor();
    var compressed = bb.Compress(original);
    var decompressed = bb.Decompress(compressed);

    Assert.That(decompressed, Is.EqualTo(original));
    Assert.That(compressed.Length, Is.LessThan(original.Length),
      "repetitive input must shrink after LZ compression");
  }

  [Test, Category("RoundTrip")]
  public void DriveSpace_RoundTrip_AllZeros_4KiB() {
    var original = new byte[4096];

    var bb = new DriveSpaceCompressor();
    var compressed = bb.Compress(original);
    var decompressed = bb.Decompress(compressed);

    Assert.That(decompressed, Is.EqualTo(original));
    Assert.That(compressed.Length, Is.LessThan(original.Length / 4));
  }

  [Test, Category("EdgeCase")]
  public void DriveSpace_LargeRepetitive_UsesFarDistance() {
    // Build an input where the only match back-reference is > 4 KiB away,
    // forcing the class-2 or class-3 distance code that DBLS cannot reach.
    var original = new byte[8192];
    new Random(7).NextBytes(original.AsSpan(0, 5000));
    // Echo the first 3000 bytes at offset 5000 → back-reference distance = 5000.
    Array.Copy(original, 0, original, 5000, 3000);
    // Tail is zero-filled.

    var bb = new DriveSpaceCompressor();
    Assert.That(bb.Decompress(bb.Compress(original)), Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void DriveSpace_Descriptor_Properties() {
    var bb = new DriveSpaceCompressor();
    Assert.That(bb.Id, Is.EqualTo("BB_DriveSpace"));
    Assert.That(bb.DisplayName, Does.Contain("DriveSpace"));
    Assert.That(bb.Description, Does.Contain("8 KiB"));
    Assert.That(bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
  }

  // =========================================================================
  //                        Cross-decoder compatibility
  // =========================================================================

  [Test, Category("RoundTrip")]
  public void DoubleSpace_Output_Decodable_ByDriveSpace() {
    // DVRS grammar is a superset of DBLS — a DoubleSpace-encoded stream
    // (never uses class-3 distances) must decode correctly through either
    // building block.
    var phrase = "Short strings are fine too but they compress less. ";
    var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(phrase, 50)));

    var dbls = new DoubleSpaceCompressor().Compress(original);
    var round = new DriveSpaceCompressor().Decompress(dbls);

    Assert.That(round, Is.EqualTo(original));
  }
}
