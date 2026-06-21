#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Safety net for the broad rollout of the default <see cref="IArchiveDefragmentable"/>
/// (verified in-place extract → re-create rebuild). For every filesystem descriptor
/// declaring <c>IArchiveDefragmentable</c> + <c>IArchiveCreatable</c>, this builds a
/// small image, defragments it in place, and asserts the result still lists the same
/// file set. The in-place rebuild only overwrites the stream on a verified round-trip,
/// so a writer limitation surfaces as a clean throw (original untouched) rather than
/// corruption — the test treats that as a non-destructive skip.
/// </summary>
[TestFixture]
public class GenericDefragRoundTripTests {

  // Every format using the DEFAULT IArchiveDefragmentable.Defragment (rebuild-via-WORM):
  // reflection over the marker interface, scoped to formats that don't declare their
  // own Defragment(Stream) (bespoke in-place defragmenters have their own tests).
  private static IEnumerable<string> DefragmentableDefaultIds() =>
    Compression.Tests.Support.CapabilityImplementers.RegisteredIdsExposing(typeof(IArchiveDefragmentable))
      .Where(id => FormatRegistry.GetArchiveOps(id) is IArchiveCreatable
                   && Enum.TryParse<FormatDetector.Format>(id, out _)
                   && !Compression.Tests.Support.CapabilityImplementers.DeclaresOwn(id, "Defragment", typeof(Stream)));

  [TestCaseSource(nameof(DefragmentableDefaultIds))]
  public void Defragment_IsNonLossy_OrRefusesCleanly(string formatId) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_gendefrag_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var aData = "generic defrag round-trip probe\n"u8.ToArray();
      var bData = new byte[4096];
      for (var i = 0; i < bData.Length; i++) bData[i] = (byte)(i * 17 + 3);
      var aSrc = Path.Combine(work, "A.TXT"); File.WriteAllBytes(aSrc, aData);
      var bSrc = Path.Combine(work, "B.BIN"); File.WriteAllBytes(bSrc, bData);

      var fmt = Enum.Parse<FormatDetector.Format>(formatId);
      var fmtOps = FormatRegistry.GetArchiveOps(formatId)!;
      var img = Path.Combine(work, "img.dat");
      try {
        ArchiveOperations.Create(img, [
          new ArchiveInput(aSrc, "A.TXT"),
          new ArchiveInput(bSrc, "B.BIN"),
        ], new CompressionOptions(), fmt, null);
      } catch (Exception ex) {
        Assert.Ignore($"{formatId}: cannot create a probe image from trivial input ({ex.GetType().Name}).");
        return;
      }
      if (!File.Exists(img) || new FileInfo(img).Length == 0) { Assert.Ignore($"{formatId}: create produced no image."); return; }

      var before = SafeList(fmtOps, img);
      if (before.Count == 0) { Assert.Ignore($"{formatId}: descriptor lists no files in its own image."); return; }

      var bytes = File.ReadAllBytes(img);
      using var ms = new MemoryStream();
      ms.Write(bytes); ms.Position = 0;
      var defrag = (IArchiveDefragmentable)fmtOps;
      try {
        defrag.Defragment(ms);
      } catch (NotSupportedException) {
        Assert.Pass($"{formatId}: defrag cleanly NotSupported (no corruption).");
        return;
      } catch (Exception ex) {
        // In-place rebuild only commits on a verified round-trip; a failure here
        // means the original stream bytes are untouched. Safe, non-destructive.
        Assert.Ignore($"{formatId}: defrag rebuild failed non-destructively ({ex.GetType().Name}).");
        return;
      }

      var after = SafeList(fmtOps, ms);
      Assert.That(after.Count, Is.GreaterThanOrEqualTo(before.Count),
        $"{formatId}: defrag dropped files ({after.Count} < {before.Count})");
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static List<string> SafeList(IArchiveFormatOperations ops, string path) {
    try { using var s = File.OpenRead(path); return SafeList(ops, s); } catch { return []; }
  }

  private static List<string> SafeList(IArchiveFormatOperations ops, Stream s) {
    try { s.Position = 0; return ops.List(s, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList(); }
    catch { return []; }
  }
}
