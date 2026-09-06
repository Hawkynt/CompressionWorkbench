#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;
using Compression.Tests.Support;

namespace Compression.Tests.Operations;

/// <summary>
/// Behavioural honesty test for every creatable format that advertises
/// <see cref="IArchivePurgeable"/>. Purge must execute through the public purge
/// verb, leave a container its own reader still accepts, and remove the planted
/// payload. A container whose empty form is zero bytes satisfies that too — the
/// test asks the reader, not the byte count. A format that declares it has no empty
/// instance at all must refuse the verb outright instead.
/// </summary>
[TestFixture]
public class GenericPurgeRoundTripTests {

  private static IEnumerable<string> PurgeableIds() =>
    CapabilityImplementers.RegisteredIdsExposing(typeof(IArchivePurgeable))
      .Where(id => FormatRegistry.GetArchiveOps(id) is IArchiveCreatable
                   && Enum.TryParse<FormatDetector.Format>(id, out _));

  [TestCaseSource(nameof(PurgeableIds))]
  public void Purge_ExecutesAndRemovesEveryProbeFile(string formatId) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_genpurge_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var ops = FormatRegistry.GetArchiveOps(formatId)!;
      var purgeable = (IArchivePurgeable)ops;
      if (!purgeable.CanPurgeToEmpty) {
        // The container mandates at least one member. The verb must say so rather
        // than leave behind something its own reader rejects.
        using var scratch = new MemoryStream();
        Assert.Throws<NotSupportedException>(() => purgeable.Purge(scratch),
          $"{formatId}: declares it has no empty instance, so purge has to refuse rather than attempt one.");
        return;
      }

      var probe = MaintenanceOperationProbe.CreateImage(formatId, work);

      using var stream = new MemoryStream();
      using (var source = File.OpenRead(probe.Path))
        source.CopyTo(stream);
      stream.Position = 0;
      purgeable.Purge(stream);

      Assert.DoesNotThrow(() => MaintenanceOperationProbe.ListFiles(ops, stream),
        $"{formatId}: purge returned successfully but left an unreadable container.");
      MaintenanceOperationProbe.AssertProbeFilesAbsent(ops, stream, formatId);
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }
}
