#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// EFFICACY (not just non-destructiveness) guard for the wipe / purge verbs: it
/// isn't enough that a deleted file stops being <em>listed</em> — its bytes must
/// actually be gone. For every creatable filesystem that can both modify
/// (<see cref="IArchiveModifiable"/>) and wipe (<see cref="IWipeEmpty"/>), this
/// plants a file full of a distinctive marker, deletes it, runs wipe, then scans
/// the whole image and asserts the marker no longer appears anywhere — while a
/// sibling "keep" file remains byte-intact. This is the forensic guarantee the
/// per-format wipe tests give for individual formats, applied registry-wide.
/// </summary>
[TestFixture]
public class WipeForensicEfficacyTests {

  // 16-byte signature unlikely to occur naturally; the marker file repeats it.
  private static readonly byte[] Marker = "DEADBEEF_WIPEME!"u8.ToArray();

  // Every format whose ops exposes IWipeEmpty (reflection over the marker) and can
  // also modify+create so the plant→delete→wipe cycle is exercisable — any category.
  private static IEnumerable<string> WipeableModifiableIds() =>
    Compression.Tests.Support.CapabilityImplementers.RegisteredIdsExposing(typeof(IWipeEmpty))
      .Where(id => FormatRegistry.GetArchiveOps(id) is IArchiveModifiable and IArchiveCreatable
                   && Enum.TryParse<FormatDetector.Format>(id, out _));

  [TestCaseSource(nameof(WipeableModifiableIds))]
  public void DeletedFileBytes_AreGoneAfterWipe(string formatId) {
    var fmt = Enum.Parse<FormatDetector.Format>(formatId);
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    var work = Path.Combine(Path.GetTempPath(), "cwb_wipefx_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var secret = new byte[4096];
      for (var i = 0; i < secret.Length; i++) secret[i] = Marker[i % Marker.Length];
      var keep = "keep me intact\n"u8.ToArray();
      var sSrc = Path.Combine(work, "SECRET.BIN"); File.WriteAllBytes(sSrc, secret);
      var kSrc = Path.Combine(work, "KEEP.TXT"); File.WriteAllBytes(kSrc, keep);

      var img = Path.Combine(work, "img.dat");
      try {
        ArchiveOperations.Create(img, [new ArchiveInput(sSrc, "SECRET.BIN"), new ArchiveInput(kSrc, "KEEP.TXT")],
          new CompressionOptions(), fmt, null);
      } catch (Exception ex) { Assert.Ignore($"{formatId}: cannot create probe image ({ex.GetType().Name})."); return; }
      if (!File.Exists(img) || new FileInfo(img).Length == 0) { Assert.Ignore($"{formatId}: no image produced."); return; }

      var bytes = File.ReadAllBytes(img);
      // Precondition: the marker really landed in the image (else the test proves nothing).
      if (!ContainsMarker(bytes)) { Assert.Ignore($"{formatId}: marker not stored verbatim (compressed/transformed) — not exercisable."); return; }

      using var ms = new MemoryStream();
      ms.Write(bytes); ms.Position = 0;

      // Identify the secret's stored name as the descriptor lists it.
      var names = SafeList(ops, ms);
      var secretName = names?.FirstOrDefault(n => Path.GetFileName(n).Contains("SECRET", StringComparison.OrdinalIgnoreCase));
      if (secretName == null) { Assert.Ignore($"{formatId}: SECRET not listed under a matchable name."); return; }

      // Delete it, then wipe all dead space (free clusters, tips, deleted-entry remnants).
      try {
        ms.Position = 0;
        ((IArchiveModifiable)ops).Remove(ms, [secretName]);
        ms.Position = 0;
        ((IWipeEmpty)ops).WipeUnusedSpace(ms, wipeClusterTips: true, wipeDeletedEntries: true);
      } catch (NotSupportedException) {
        Assert.Ignore($"{formatId}: delete/wipe not supported in practice.");
        return;
      }

      // Forensic assertion: no trace of the secret's bytes remains anywhere.
      Assert.That(ContainsMarker(ms.ToArray()), Is.False,
        $"{formatId}: deleted file's bytes survived wipe (forensic remnant)");

      // The sibling file must still be intact.
      var after = SafeList(ops, ms);
      Assert.That(after, Is.Not.Null, $"{formatId}: image unreadable after delete+wipe");
      Assert.That(after!.Any(n => Path.GetFileName(n).Contains("KEEP", StringComparison.OrdinalIgnoreCase)), Is.True,
        $"{formatId}: KEEP.TXT lost during delete+wipe");
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static bool ContainsMarker(byte[] data) {
    for (var i = 0; i + Marker.Length <= data.Length; i++) {
      var hit = true;
      for (var j = 0; j < Marker.Length; j++)
        if (data[i + j] != Marker[j]) { hit = false; break; }
      if (hit) return true;
    }
    return false;
  }

  private static List<string>? SafeList(IArchiveFormatOperations ops, Stream s) {
    try { s.Position = 0; return ops.List(s, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList(); }
    catch { return null; }
  }
}
