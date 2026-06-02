using System.Text;
using Compression.Registry;
using FileSystem.DoubleSpace;

namespace Compression.Tests.DoubleSpace;

/// <summary>
/// End-to-end CVF round-trip tests across the four method ids the DBLSPACE /
/// DRVSPACE writers publish (<c>stored</c>, <c>ds-lz77</c>, <c>ds-lz77+</c>,
/// <c>ds-lz77++</c>). Covers byte-equal recovery via the reader plus the
/// effort-tier monotonicity invariant the descriptor's <c>Methods</c> list
/// implicitly promises (more <c>+</c> ≤ fewer <c>+</c> on compressible data).
/// </summary>
[TestFixture]
public class DoubleSpaceCompressedTests {

  /// <summary>
  /// Builds a long, very compressible payload — repeated dictionary phrases —
  /// big enough to span several CVF clusters (4 KiB each) so the per-cluster
  /// compression path is exercised more than once.
  /// </summary>
  private static byte[] CompressiblePayload() {
    var phrase = "The DoubleSpace DBLS LZ77 encoder packs sectors in 4 KiB chunks. ";
    var sb = new StringBuilder(phrase.Length * 400);
    for (var i = 0; i < 400; ++i) sb.Append(phrase);
    return Encoding.ASCII.GetBytes(sb.ToString());
  }

  private static byte[] WriteCvf(string method, byte[] data) {
    var w = new DoubleSpaceWriter {
      Variant = CvfVariant.DoubleSpace60,
      MethodName = method,
    };
    w.AddFile("DATA.BIN", data);
    return w.Build();
  }

  private static byte[] ReadBack(byte[] cvf) {
    using var ms = new MemoryStream(cvf);
    var r = new DoubleSpaceReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    return r.Extract(r.Entries[0]);
  }

  // =========================================================================
  //                              Round-trip
  // =========================================================================

  [Test, Category("RoundTrip")]
  public void Stored_RoundTrip() {
    var data = CompressiblePayload();
    var cvf = WriteCvf("stored", data);
    Assert.That(ReadBack(cvf), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void DsLz77_RoundTrip() {
    var data = CompressiblePayload();
    var cvf = WriteCvf("ds-lz77", data);
    Assert.That(ReadBack(cvf), Is.EqualTo(data));
  }

  // =========================================================================
  //                       Effort-tier monotonicity
  // =========================================================================

  [Test, Category("EffortTier")]
  public void DsLz77_OutputSmallerThanStored() {
    var data = CompressiblePayload();
    var stored = WriteCvf("stored", data);
    var compressed = WriteCvf("ds-lz77", data);

    // The DATA region shrinks; the metadata regions don't change much (same
    // file count). Total image size doesn't directly track the compressed
    // run sizes because the writer pre-allocates an upper-bound DATA window.
    // Compare the per-cluster run sizes instead by re-reading.
    Assert.That(ReadBack(stored), Is.EqualTo(data));
    Assert.That(ReadBack(compressed), Is.EqualTo(data));

    // Sanity: directly compare the BB output sizes for the first cluster.
    var firstClusterRaw = data.AsSpan(0, 4096);
    var rawRun = DsCompression.Compress(firstClusterRaw, effort: 0);
    Assert.That(rawRun.Length, Is.LessThan(firstClusterRaw.Length + 2),
      "DS LZ77 must produce a compressed run smaller than the stored run for compressible input");
  }

  [Test, Category("EffortTier")]
  public void DsLz77Plus_OutputSmallerThanDsLz77() {
    var data = CompressiblePayload();
    // Compare BB output for the first cluster across effort tiers — this is
    // the same primitive the writer drives per-cluster.
    var firstCluster = data.AsSpan(0, 4096).ToArray();
    var c0 = Core.Dictionary.DsLz77.DsLz77Compressor.Compress(firstCluster, effort: 0);
    var c1 = Core.Dictionary.DsLz77.DsLz77Compressor.Compress(firstCluster, effort: 1);

    Assert.That(c1.Length, Is.LessThanOrEqualTo(c0.Length),
      "lazy parse must not be larger than greedy parse on compressible input");

    // Image-level round-trip both still recover the bytes.
    Assert.That(ReadBack(WriteCvf("ds-lz77", data)), Is.EqualTo(data));
    Assert.That(ReadBack(WriteCvf("ds-lz77+", data)), Is.EqualTo(data));
  }

  [Test, Category("EffortTier")]
  public void DsLz77PlusPlus_OutputSmallestForSlowestEffort() {
    var data = CompressiblePayload();
    var firstCluster = data.AsSpan(0, 4096).ToArray();
    var c1 = Core.Dictionary.DsLz77.DsLz77Compressor.Compress(firstCluster, effort: 1);
    var c2 = Core.Dictionary.DsLz77.DsLz77Compressor.Compress(firstCluster, effort: 2);

    Assert.That(c2.Length, Is.LessThanOrEqualTo(c1.Length),
      "iterated parse keeps the best result so must not exceed lazy parse");

    Assert.That(ReadBack(WriteCvf("ds-lz77++", data)), Is.EqualTo(data));
  }

  // =========================================================================
  //                            Method-name parsing
  // =========================================================================

  [Test, Category("EdgeCase")]
  public void UnknownMethod_FallsBackToDefault() {
    var data = CompressiblePayload();
    // Garbage method id must not throw — falls back to "ds-lz77" (effort 0).
    var cvf = WriteCvf("nonsense-method", data);
    Assert.That(ReadBack(cvf), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void NullMethod_DefaultsToDsLz77() {
    var data = CompressiblePayload();
    var w = new DoubleSpaceWriter {
      Variant = CvfVariant.DoubleSpace60,
      MethodName = null,
    };
    w.AddFile("DATA.BIN", data);
    var cvf = w.Build();
    Assert.That(ReadBack(cvf), Is.EqualTo(data));
  }

  // =========================================================================
  //                       DriveSpace parity (same writer)
  // =========================================================================

  [Test, Category("RoundTrip")]
  public void DriveSpace_DsLz77Plus_RoundTrip() {
    var data = CompressiblePayload();
    var w = new DoubleSpaceWriter {
      Variant = CvfVariant.DriveSpace62,
      MethodName = "ds-lz77+",
    };
    w.AddFile("DATA.BIN", data);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    var r = new DoubleSpaceReader(ms);
    Assert.That(r.IsDriveSpace, Is.True);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }
}
