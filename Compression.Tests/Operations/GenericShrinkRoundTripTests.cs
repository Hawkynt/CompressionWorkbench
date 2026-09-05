#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;
using Compression.Tests.Support;

namespace Compression.Tests.Operations;

/// <summary>
/// Behavioural honesty test for every creatable format that advertises
/// <see cref="IArchiveShrinkable"/>. Shrink must execute, must never grow the
/// container, and where the format's reader hands the planted payload back
/// verbatim it must still do so afterwards.
/// </summary>
[TestFixture]
public class GenericShrinkRoundTripTests {

  private static IEnumerable<string> ShrinkableIds() =>
    CapabilityImplementers.RegisteredIdsExposing(typeof(IArchiveShrinkable))
      .Where(id => FormatRegistry.GetArchiveOps(id) is IArchiveCreatable
                   && Enum.TryParse<FormatDetector.Format>(id, out _));

  [TestCaseSource(nameof(ShrinkableIds))]
  public void Shrink_ExecutesNeverGrowsAndPreservesProbeFilesByteForByte(string formatId) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_genshrink_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var ops = FormatRegistry.GetArchiveOps(formatId)!;
      var probe = MaintenanceOperationProbe.CreateImage(formatId, work);
      var originalSize = new FileInfo(probe.Path).Length;
      int before;
      using (var source = File.OpenRead(probe.Path))
        before = MaintenanceOperationProbe.ListFiles(ops, source).Count;

      byte[] shrunk;
      using (var source = File.OpenRead(probe.Path))
      using (var target = new MemoryStream()) {
        ((IArchiveShrinkable)ops).Shrink(source, target);
        shrunk = target.ToArray();
      }

      Assert.That(shrunk.Length, Is.GreaterThan(0), $"{formatId}: shrink produced an empty image.");
      Assert.That(shrunk.LongLength, Is.LessThanOrEqualTo(originalSize),
        $"{formatId}: shrink grew the image from {originalSize} to {shrunk.LongLength} bytes.");

      using var rebuilt = new MemoryStream(shrunk, writable: false);
      Assert.That(MaintenanceOperationProbe.ListFiles(ops, rebuilt), Has.Count.GreaterThanOrEqualTo(before),
        $"{formatId}: shrink dropped entries the reader listed before it ran.");
      if (probe.PayloadObservable)
        MaintenanceOperationProbe.AssertProbeFiles(ops, rebuilt, formatId);
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }
}
