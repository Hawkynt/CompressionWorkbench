using Compression.Registry;
using FileSystem.DoubleSpace;
using FileSystem.DriveSpace3;

namespace Compression.Tests.DoubleSpace;

/// <summary>
/// End-to-end defrag verification for all three CVF variants
/// (DoubleSpace 6.0, DriveSpace 6.22, DriveSpace 3 / Plus! Pack 1995).
/// Asserts the canonical defrag invariant: stream length unchanged + every
/// file byte-identical across the operation, for both
/// <see cref="DefragMode.ConsolidateAtStart"/> and
/// <see cref="DefragMode.ConsolidateAtEnd"/>.
/// </summary>
[TestFixture]
public class CvfDefragIntegrationTests {

  // =========================================================================
  //                              Fixtures
  // =========================================================================

  private static List<(string Name, byte[] Data)> MakeMixedFiles(int seed) {
    var rng = new Random(seed);

    // Compressible file 1: highly redundant ASCII (LZ77 sweet spot).
    var compressibleA = System.Text.Encoding.ASCII.GetBytes(string.Concat(
      Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 100)));

    // Compressible file 2: all-zeros region with a short prefix.
    var compressibleB = new byte[2048];
    "PREAMBLE-"u8.ToArray().CopyTo(compressibleB, 0);

    // Incompressible file: random bytes — exercises the per-cluster
    // shrink-or-store fallback at every effort tier.
    var randomA = new byte[3000];
    rng.NextBytes(randomA);

    // Mixed file: short compressible header + random body.
    var mixed = new byte[2500];
    "MIXED:HEADER"u8.ToArray().CopyTo(mixed, 0);
    rng.NextBytes(mixed.AsSpan(64));

    // Small file: ensures we exercise the <32-byte stored-by-policy path too.
    var tiny = "SMALL"u8.ToArray();

    return [
      ("ALPHA.TXT",     compressibleA),
      ("BETA.BIN",      compressibleB),
      ("RANDOM.DAT",    randomA),
      ("DIR/MIX.DAT",   mixed),
      ("DIR/SUB/T.TXT", tiny),
    ];
  }

  private static byte[] BuildCvf(CvfVariant variant, List<(string Name, byte[] Data)> files) {
    var w = new DoubleSpaceWriter { Variant = variant };
    foreach (var (n, d) in files) w.AddFile(n, d);
    return w.Build();
  }

  private static Dictionary<string, byte[]> Snapshot(MemoryStream image) {
    image.Position = 0;
    using var r = new DoubleSpaceReader(image);
    var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      result[e.Name] = r.Extract(e);
    }
    return result;
  }

  /// <summary>
  /// Runs the canonical defrag invariant check for a given variant/descriptor
  /// pair and defrag mode: build → snapshot → defrag → assert size unchanged
  /// + file count unchanged + byte-identical contents per file.
  /// </summary>
  private static void AssertDefragPreservesSizeAndContents(
      CvfVariant variant, IArchiveDefragmentable descriptor, DefragMode mode) {
    var files = MakeMixedFiles(seed: 0xC0FFEE ^ (int)variant ^ (int)mode);
    var image = BuildCvf(variant, files);

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    var originalLength = ms.Length;

    var before = Snapshot(ms);
    Assert.That(before, Has.Count.EqualTo(files.Count),
      $"{variant}: pre-defrag snapshot must surface every input file");

    ms.Position = 0;
    descriptor.Defragment(ms, new DefragOptions { Mode = mode });

    Assert.That(ms.Length, Is.EqualTo(originalLength),
      $"{variant}/{mode}: defrag must not change the stream length");

    var after = Snapshot(ms);
    Assert.That(after.Keys, Is.EquivalentTo(before.Keys),
      $"{variant}/{mode}: defrag must preserve the file set");

    foreach (var (name, bytes) in before)
      Assert.That(after[name], Is.EqualTo(bytes),
        $"{variant}/{mode}: file '{name}' bytes changed across defrag");
  }

  // =========================================================================
  //                       DoubleSpace 6.0 (DBLS, MS-DOS 6.0)
  // =========================================================================

  [Test, Category("Defrag")]
  public void DoubleSpace60_Defrag_ConsolidateAtStart_PreservesSizeAndContents() {
    AssertDefragPreservesSizeAndContents(
      CvfVariant.DoubleSpace60,
      new DoubleSpaceFormatDescriptor(),
      DefragMode.ConsolidateAtStart);
  }

  [Test, Category("Defrag")]
  public void DoubleSpace60_Defrag_ConsolidateAtEnd_PreservesSizeAndContents() {
    AssertDefragPreservesSizeAndContents(
      CvfVariant.DoubleSpace60,
      new DoubleSpaceFormatDescriptor(),
      DefragMode.ConsolidateAtEnd);
  }

  // =========================================================================
  //                       DriveSpace 6.22 (DVRS, MS-DOS 6.22)
  // =========================================================================

  [Test, Category("Defrag")]
  public void DriveSpace62_Defrag_ConsolidateAtStart_PreservesSizeAndContents() {
    AssertDefragPreservesSizeAndContents(
      CvfVariant.DriveSpace62,
      new DriveSpaceFormatDescriptor(),
      DefragMode.ConsolidateAtStart);
  }

  [Test, Category("Defrag")]
  public void DriveSpace62_Defrag_ConsolidateAtEnd_PreservesSizeAndContents() {
    AssertDefragPreservesSizeAndContents(
      CvfVariant.DriveSpace62,
      new DriveSpaceFormatDescriptor(),
      DefragMode.ConsolidateAtEnd);
  }

  // =========================================================================
  //                  DriveSpace 3 (DVR3, Win95 Plus! Pack 1995)
  // =========================================================================

  [Test, Category("Defrag")]
  public void DriveSpace3_Defrag_ConsolidateAtStart_PreservesSizeAndContents() {
    AssertDefragPreservesSizeAndContents(
      CvfVariant.DriveSpace3,
      new DriveSpace3FormatDescriptor(),
      DefragMode.ConsolidateAtStart);
  }

  [Test, Category("Defrag")]
  public void DriveSpace3_Defrag_ConsolidateAtEnd_PreservesSizeAndContents() {
    AssertDefragPreservesSizeAndContents(
      CvfVariant.DriveSpace3,
      new DriveSpace3FormatDescriptor(),
      DefragMode.ConsolidateAtEnd);
  }
}
