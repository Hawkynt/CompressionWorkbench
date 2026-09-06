#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;
using Compression.Tests.Support;

namespace Compression.Tests.Operations;

/// <summary>
/// Behavioural honesty test for every creatable format that advertises
/// <see cref="ILayoutOptimizable"/>. Analysis and rebuild must both execute, and
/// rebuilding with default geometry must keep whatever the reader could retrieve
/// before it ran.
/// </summary>
[TestFixture]
public class GenericLayoutOptimizableTests {

  private static IEnumerable<string> LayoutOptimizableIds() =>
    CapabilityImplementers.RegisteredIdsExposing(typeof(ILayoutOptimizable))
      .Where(id => FormatRegistry.GetArchiveOps(id) is IArchiveCreatable
                   && Enum.TryParse<FormatDetector.Format>(id, out _));

  [TestCaseSource(nameof(LayoutOptimizableIds))]
  public void AnalyzeAndRebuild_ExecuteAndPreserveProbeFilesByteForByte(string formatId) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_genlayout_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var ops = FormatRegistry.GetArchiveOps(formatId)!;
      var probe = MaintenanceOperationProbe.CreateImage(formatId, work);
      var optimizable = (ILayoutOptimizable)ops;
      int before;
      using (var source = File.OpenRead(probe.Path))
        before = MaintenanceOperationProbe.ListFiles(ops, source).Count;

      using (var source = File.OpenRead(probe.Path)) {
        var analysis = optimizable.AnalyzeLayout(source);
        Assert.That(analysis, Is.Not.Null, $"{formatId}: AnalyzeLayout returned null.");
        Assert.That(analysis.ImageSize, Is.GreaterThanOrEqualTo(0), $"{formatId}: AnalyzeLayout returned an invalid image size.");
      }

      byte[] rebuilt;
      using (var source = File.OpenRead(probe.Path))
      using (var target = new MemoryStream()) {
        optimizable.RebuildStreaming(source, target, new LayoutRebuildOptions());
        rebuilt = target.ToArray();
      }

      Assert.That(rebuilt.Length, Is.GreaterThan(0), $"{formatId}: layout rebuild produced an empty image.");
      using var rebuiltStream = new MemoryStream(rebuilt, writable: false);
      Assert.That(MaintenanceOperationProbe.ListFiles(ops, rebuiltStream), Has.Count.GreaterThanOrEqualTo(before),
        $"{formatId}: layout rebuild dropped entries the reader listed before it ran.");
      if (probe.PayloadObservable)
        MaintenanceOperationProbe.AssertProbeFiles(ops, rebuiltStream, formatId);
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }
}
