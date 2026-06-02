using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.DoubleSpace;

namespace Compression.Tests.DoubleSpace;

/// <summary>
/// Regression tests for the per-cluster shrink-or-store invariant: the writer
/// MUST emit a stored CVF run (MDFAT flags=1) whenever a cluster's compressed
/// payload would not actually shrink the input. This invariant has to hold at
/// EVERY effort level (0 / +, 1 / +, 2+ / ++), not just the historical greedy
/// path. Without it, "Optimize" with the highest-effort method would penalise
/// incompressible regions (e.g. JPEG / MP3 / ZIP-inside-CVF) by paying the
/// LZ-token overhead for no benefit.
/// </summary>
[TestFixture]
public class DoubleSpaceShrinkOrStoreTests {

  /// <summary>
  /// Cryptographically random bytes — should be incompressible at every
  /// effort level. Each cluster's compressed output ought to be larger than
  /// the raw 4 KiB, triggering the shrink-or-store fallback.
  /// </summary>
  private static byte[] IncompressiblePayload(int clusters, int seed) {
    var data = new byte[clusters * 4096];
    new Random(seed).NextBytes(data);
    return data;
  }

  private static byte[] BuildCvf(CvfVariant variant, string method, byte[] data) {
    var w = new DoubleSpaceWriter {
      Variant = variant,
      MethodName = method,
    };
    w.AddFile("DATA.BIN", data);
    return w.Build();
  }

  private static (int Stored, int Compressed) CountMdfatFlags(byte[] cvf) {
    var mdfatStart = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(44));
    var mdfatLen = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(48));
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(cvf.AsSpan(11));
    if (bytesPerSector == 0) bytesPerSector = 512;
    var entries = (int)(mdfatLen * bytesPerSector / 4u);
    var stored = 0;
    var compressed = 0;
    for (var i = 0; i < entries; i++) {
      var off = (int)(mdfatStart * bytesPerSector + i * 4);
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(off));
      var flags = (entry >> 28) & 0xFu;
      if (flags == 1u) stored++;
      else if (flags == 2u) compressed++;
    }
    return (stored, compressed);
  }

  // =========================================================================
  //  Per-cluster shrink-or-store at every DS LZ77 effort level
  // =========================================================================

  /// <summary>
  /// For incompressible (random) input across multiple clusters, the writer
  /// must store each cluster verbatim AT EVERY EFFORT LEVEL. The CVF must not
  /// be measurably larger than the stored-only variant; otherwise the writer
  /// is paying compression overhead for no compression gain.
  /// </summary>
  [TestCase("ds-lz77",   Category = "Invariant")]
  [TestCase("ds-lz77+",  Category = "Invariant")]
  [TestCase("ds-lz77++", Category = "Invariant")]
  public void Writer_IncompressibleClusters_StoredVerbatim_AtAllEffortLevels_DoubleSpace(string method) {
    var data = IncompressiblePayload(clusters: 8, seed: unchecked((int)0xCAFEBABE));

    var stored = BuildCvf(CvfVariant.DoubleSpace60, "stored", data);
    var effortLeveled = BuildCvf(CvfVariant.DoubleSpace60, method, data);

    // The effort-leveled CVF must not be larger than the stored variant —
    // any expansion means a cluster was emitted compressed despite being
    // larger, which violates the shrink-or-store fallback.
    Assert.That(effortLeveled.Length, Is.LessThanOrEqualTo(stored.Length),
      $"{method}: incompressible input must fall back to stored runs (file size grew vs stored variant — fallback not honoured)");

    // Every MDFAT entry must be flagged as stored, never compressed.
    var (_, compressed) = CountMdfatFlags(effortLeveled);
    Assert.That(compressed, Is.EqualTo(0),
      $"{method}: incompressible input produced {compressed} compressed clusters — should be 0");

    // Round-trip must still recover the original bytes.
    using var ms = new MemoryStream(effortLeveled);
    var r = new DoubleSpaceReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  /// <summary>
  /// DriveSpace 6.22 variant — same invariant. The 8 KiB window doesn't
  /// help with truly random data either, so every cluster must still
  /// fall back to stored.
  /// </summary>
  [TestCase("ds-lz77",   Category = "Invariant")]
  [TestCase("ds-lz77+",  Category = "Invariant")]
  [TestCase("ds-lz77++", Category = "Invariant")]
  public void Writer_IncompressibleClusters_StoredVerbatim_AtAllEffortLevels_DriveSpace(string method) {
    var data = IncompressiblePayload(clusters: 6, seed: unchecked((int)0xFEEDFACE));

    var stored = BuildCvf(CvfVariant.DriveSpace62, "stored", data);
    var effortLeveled = BuildCvf(CvfVariant.DriveSpace62, method, data);

    Assert.That(effortLeveled.Length, Is.LessThanOrEqualTo(stored.Length),
      $"DriveSpace {method}: incompressible input must fall back to stored runs");

    var (_, compressed) = CountMdfatFlags(effortLeveled);
    Assert.That(compressed, Is.EqualTo(0),
      $"DriveSpace {method}: incompressible input produced {compressed} compressed clusters — should be 0");

    using var ms = new MemoryStream(effortLeveled);
    var r = new DoubleSpaceReader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  // =========================================================================
  //  Per-cluster shrink-or-store for MS LZH (DriveSpace 3 / Plus! Pack)
  // =========================================================================

  /// <summary>
  /// Same invariant for the MS LZH codec used by DriveSpace 3 (Win95 Plus!
  /// Pack). The MS LZH compressor's fallback path lives in DsCompression's
  /// CompressMsLzh: if the encoded payload doesn't fit the 12-bit CVF size
  /// cap or is no smaller than the raw input, a stored run is returned —
  /// and this must hold at every effort tier (ms-lzh / ms-lzh+ / ms-lzh++).
  /// </summary>
  [TestCase("ms-lzh",   Category = "Invariant")]
  [TestCase("ms-lzh+",  Category = "Invariant")]
  [TestCase("ms-lzh++", Category = "Invariant")]
  public void Writer_IncompressibleClusters_StoredVerbatim_DriveSpace3(string method) {
    var data = IncompressiblePayload(clusters: 6, seed: unchecked((int)0xDEADBEEF));

    var stored = BuildCvf(CvfVariant.DriveSpace3, "stored", data);
    var msLzh = BuildCvf(CvfVariant.DriveSpace3, method, data);

    Assert.That(msLzh.Length, Is.LessThanOrEqualTo(stored.Length),
      $"{method}: incompressible input must fall back to stored runs");

    var (_, compressed) = CountMdfatFlags(msLzh);
    Assert.That(compressed, Is.EqualTo(0),
      $"{method}: incompressible input produced {compressed} compressed clusters — should be 0");

    using var ms = new MemoryStream(msLzh);
    var r = new DoubleSpaceReader(ms);
    Assert.That(r.IsDriveSpace3, Is.True);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }
}
