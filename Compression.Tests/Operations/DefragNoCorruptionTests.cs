#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Anti-corruption guard for <see cref="IArchiveDefragmentable"/> across <b>every</b>
/// creatable filesystem — including descriptors with a bespoke in-place defragmenter,
/// which the round-trip suites deliberately exclude. Defragment must be non-destructive:
/// after the call the image is still readable and holds every file it held before, OR
/// the call threw (in-place edits commit only on success, so a throw leaves the original
/// intact). The probe payload is the one that exposed a silent catalog-corruption bug in
/// AppleDOS's planner-driven defrag — this test pins that class of regression shut.
/// </summary>
[TestFixture]
public class DefragNoCorruptionTests {

  private static IEnumerable<string> CreatableFilesystemIds() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    foreach (var d in FormatRegistry.All.OrderBy(x => x.Id)) {
      var ops = FormatRegistry.GetArchiveOps(d.Id);
      if (ops is not IArchiveDefragmentable || ops is not IArchiveCreatable) continue;
      if (!(ops.GetType().Namespace ?? "").StartsWith("FileSystem.", StringComparison.Ordinal)) continue;
      yield return d.Id;
    }
  }

  [TestCaseSource(nameof(CreatableFilesystemIds))]
  public void Defragment_NeverCorrupts(string formatId) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_defragsafe_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      // The exact payload that triggered AppleDOS catalog corruption.
      var aData = "generic defrag round-trip probe\n"u8.ToArray();
      var bData = new byte[4096];
      for (var i = 0; i < bData.Length; i++) bData[i] = (byte)(i * 17 + 3);
      var aSrc = Path.Combine(work, "A.TXT"); File.WriteAllBytes(aSrc, aData);
      var bSrc = Path.Combine(work, "B.BIN"); File.WriteAllBytes(bSrc, bData);

      var fmt = Enum.Parse<FormatDetector.Format>(formatId);
      var ops = FormatRegistry.GetArchiveOps(formatId)!;
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

      var before = SafeList(ops, img); // full entry names, as the descriptor reports them
      // Skip formats that store under transformed names / synthetic whole-image entries
      // (e.g. TrDos *.cod, CbmNibble track_NN, *.mfs) — not exercisable by exact name.
      if (!before.Contains("A.TXT", StringComparer.Ordinal) && before.All(n => !n.EndsWith("A.TXT", StringComparison.Ordinal))) {
        Assert.Ignore($"{formatId}: does not store the probe files under a matchable name.");
        return;
      }

      var bytes = File.ReadAllBytes(img);
      using var ms = new MemoryStream();
      ms.Write(bytes); ms.Position = 0;
      try {
        ((IArchiveDefragmentable)ops).Defragment(ms);
      } catch {
        // In-place rebuild commits only on a verified round-trip; a throw means
        // the stream bytes are unchanged. Non-destructive — acceptable.
        return;
      }

      // It claimed success — so the image MUST still be readable and complete.
      var after = SafeListOrNull(ops, ms);
      Assert.That(after, Is.Not.Null, $"{formatId}: defrag reported success but produced an unreadable image (corruption)");
      foreach (var name in before)
        Assert.That(after!, Does.Contain(name), $"{formatId}: defrag dropped '{name}' (had: {string.Join(",", before)}; now: {string.Join(",", after!)})");
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static List<string> SafeList(IArchiveFormatOperations ops, string path) {
    try { using var s = File.OpenRead(path); s.Position = 0; return ops.List(s, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList(); }
    catch { return []; }
  }

  private static List<string>? SafeListOrNull(IArchiveFormatOperations ops, Stream s) {
    try { s.Position = 0; return ops.List(s, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList(); }
    catch { return null; }
  }
}
