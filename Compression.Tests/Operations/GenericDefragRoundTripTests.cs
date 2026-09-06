#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;
using Compression.Tests.Support;

namespace Compression.Tests.Operations;

/// <summary>
/// Behavioural honesty test for every creatable format that advertises
/// <see cref="IArchiveDefragmentable"/>. A checked defrag capability must execute;
/// throwing <see cref="NotSupportedException"/> or otherwise refusing the operation
/// is a failing capability claim, not a successful safety result. Where the format's
/// reader hands the planted payload back verbatim, it must still do so afterwards.
/// </summary>
[TestFixture]
public class GenericDefragRoundTripTests {

  private static IEnumerable<string> DefragmentableIds() =>
    CapabilityImplementers.RegisteredIdsExposing(typeof(IArchiveDefragmentable))
      .Where(id => FormatRegistry.GetArchiveOps(id) is IArchiveCreatable
                   && Enum.TryParse<FormatDetector.Format>(id, out _));

  [TestCaseSource(nameof(DefragmentableIds))]
  public void Defragment_ExecutesAndPreservesProbeFilesByteForByte(string formatId) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_gendefrag_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var ops = FormatRegistry.GetArchiveOps(formatId)!;
      var probe = MaintenanceOperationProbe.CreateImage(formatId, work);

      using var stream = new MemoryStream();
      using (var source = File.OpenRead(probe.Path))
        source.CopyTo(stream);
      stream.Position = 0;
      var before = MaintenanceOperationProbe.ListFiles(ops, stream).Count;
      stream.Position = 0;
      ((IArchiveDefragmentable)ops).Defragment(stream);

      Assert.That(stream.Length, Is.GreaterThan(0), $"{formatId}: defrag produced an empty image.");
      Assert.That(MaintenanceOperationProbe.ListFiles(ops, stream), Has.Count.GreaterThanOrEqualTo(before),
        $"{formatId}: defrag dropped entries the reader listed before it ran.");
      if (probe.PayloadObservable)
        MaintenanceOperationProbe.AssertProbeFiles(ops, stream, formatId);
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }
}
