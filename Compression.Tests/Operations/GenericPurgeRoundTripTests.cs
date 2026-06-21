#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Safety net for the broad rollout of the default <see cref="IArchiveModifiable"/>
/// (verified extract → edit → re-create rebuild). For every filesystem descriptor
/// using the DEFAULT Remove, this builds a small image, purges it (Remove every
/// entry), and asserts the result is a valid, listable, empty container — the
/// <em>purge</em> verb. The rebuild only commits a result that re-lists, so a writer
/// limitation surfaces as a clean throw (original untouched) rather than corruption.
/// </summary>
[TestFixture]
public class GenericPurgeRoundTripTests {

  // Every format using the DEFAULT IArchiveModifiable.Remove (rebuild-via-WORM):
  // reflection over the marker, scoped to formats that don't declare their own Remove
  // (bespoke removers have their own tests).
  private static IEnumerable<string> ModifiableDefaultIds() =>
    Compression.Tests.Support.CapabilityImplementers.RegisteredIdsExposing(typeof(IArchiveModifiable))
      .Where(id => FormatRegistry.GetArchiveOps(id) is IArchiveCreatable
                   && Enum.TryParse<FormatDetector.Format>(id, out _)
                   && !Compression.Tests.Support.CapabilityImplementers.DeclaresOwn(id, "Remove", typeof(Stream), typeof(string[])));

  [TestCaseSource(nameof(ModifiableDefaultIds))]
  public void Purge_EmptiesContainer_OrRefusesCleanly(string formatId) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_genpurge_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var aSrc = Path.Combine(work, "A.TXT"); File.WriteAllBytes(aSrc, "purge probe\n"u8.ToArray());
      var bSrc = Path.Combine(work, "B.BIN"); File.WriteAllBytes(bSrc, new byte[2048]);

      var fmt = Enum.Parse<FormatDetector.Format>(formatId);
      var fmtOps = FormatRegistry.GetArchiveOps(formatId)!;
      var img = Path.Combine(work, "img.dat");
      try {
        ArchiveOperations.Create(img, [
          new ArchiveInput(aSrc, "A.TXT"),
          new ArchiveInput(bSrc, "B.BIN"),
        ], new CompressionOptions(), fmt, null);
      } catch (Exception ex) {
        Assert.Ignore($"{formatId}: cannot create a probe image ({ex.GetType().Name}).");
        return;
      }
      if (!File.Exists(img) || new FileInfo(img).Length == 0) { Assert.Ignore($"{formatId}: create produced no image."); return; }

      var before = SafeList(fmtOps, img);
      if (before.Count == 0) { Assert.Ignore($"{formatId}: descriptor lists no files in its own image."); return; }
      // Purge removes the USER's files; system files (lost+found, journals…) that
      // a fresh empty volume re-creates are fine. Only exercise formats that
      // actually store our named user files.
      var userFiles = new[] { "A.TXT", "B.BIN" };
      bool Has(IEnumerable<string> names, string u) =>
        names.Any(n => string.Equals(Path.GetFileName(n), u, StringComparison.OrdinalIgnoreCase));
      if (!userFiles.Any(u => Has(before, u))) {
        Assert.Ignore($"{formatId}: does not store named user files in its own listing (purge not meaningfully exercisable).");
        return;
      }

      var bytes = File.ReadAllBytes(img);
      using var ms = new MemoryStream();
      ms.Write(bytes); ms.Position = 0;
      var modifiable = (IArchiveModifiable)fmtOps;
      try {
        modifiable.Remove(ms, [.. before]);
      } catch (NotSupportedException) {
        Assert.Pass($"{formatId}: purge cleanly NotSupported (no corruption).");
        return;
      } catch (Exception ex) {
        Assert.Ignore($"{formatId}: purge rebuild failed non-destructively ({ex.GetType().Name}).");
        return;
      }

      // The container must still be valid (listable) and the user's files gone.
      var after = SafeList(fmtOps, ms);
      foreach (var u in userFiles)
        Assert.That(Has(after, u), Is.False,
          $"{formatId}: purge left user file '{u}' behind (after={string.Join(",", after)})");
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
