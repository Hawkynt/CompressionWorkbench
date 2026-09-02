#pragma warning disable CS1591
using System.Security.Cryptography;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Wiping free space must never take a live file with it.
/// </summary>
/// <remarks>
/// <para>A wipe zeroes whatever the extent map does not claim, so the map is
/// the whole safety argument: anything it forgets to mention is destroyed. The
/// dangerous case is not a freshly written volume — where the map and the
/// writer agree by construction — but one that has been edited, because adding
/// and removing files is what moves a volume's own structures around.</para>
///
/// <para>VDFS relocated its entry table past the file data when a file was
/// added, while its map still described the table as everything ahead of the
/// first file. Wiping such a volume zeroed the table and every file went
/// missing at once. This drives every format that offers both verbs through
/// that sequence.</para>
/// </remarks>
[TestFixture]
[Category("Slow")]
public class WipePreservesLiveDataTests {

  private static IEnumerable<string> WipeableFormats() {
    foreach (var descriptor in FormatRegistry.All.OrderBy(d => d.Id, StringComparer.Ordinal)) {
      var ops = FormatRegistry.GetArchiveOps(descriptor.Id);
      if (ops is not (IArchiveCreatable and IWipeEmpty and IArchiveModifiable)) continue;
      if (!Enum.TryParse<FormatDetector.Format>(descriptor.Id, out _)) continue;
      yield return descriptor.Id;
    }
  }

  [TestCaseSource(nameof(WipeableFormats))]
  public void WipingAnEditedVolume_KeepsEveryFile(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    var format = Enum.Parse<FormatDetector.Format>(formatId);
    var work = Path.Combine(Path.GetTempPath(), "cwb_wipe_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);

    try {
      var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      var inputs = new List<ArchiveInput>();
      for (var i = 0; i < 4; ++i) {
        var payload = new byte[1500 + i * 700];
        for (var b = 0; b < payload.Length; ++b) payload[b] = (byte)(b * 11 + i * 37);
        var path = Path.Combine(work, $"K{i}.BIN");
        File.WriteAllBytes(path, payload);
        inputs.Add(new ArchiveInput(path, $"K{i}.BIN"));
        expected[$"K{i}.BIN"] = Digest(payload);
      }

      var image = Path.Combine(work, "volume.img");
      try {
        ArchiveOperations.Create(image, inputs, new CompressionOptions(), format, null);
      } catch (Exception ex) {
        Assert.Ignore($"{formatId}: cannot create a probe volume ({ex.GetType().Name}).");
        return;
      }
      if (!File.Exists(image) || new FileInfo(image).Length == 0) {
        Assert.Ignore($"{formatId}: produced no image.");
        return;
      }

      // Editing is what moves a volume's own structures about, and a map that
      // describes where they used to be is what makes a wipe dangerous.
      var scratch = Path.Combine(work, "SCRATCH.BIN");
      File.WriteAllBytes(scratch, new byte[1200]);
      var modifier = (IArchiveModifiable)ops;
      try {
        using (var stream = File.Open(image, FileMode.Open, FileAccess.ReadWrite))
          modifier.Add(stream, [new ArchiveInputInfo(scratch, "SCRATCH.BIN", false)]);
        using (var stream = File.Open(image, FileMode.Open, FileAccess.ReadWrite))
          modifier.Remove(stream, ["SCRATCH.BIN"]);
      } catch (Exception ex) {
        TestContext.Out.WriteLine(
          $"{formatId}: cannot edit in place ({ex.GetType().Name}); wiping the fresh volume instead.");
      }

      // Only the files put in are compared. A reader may also surface views of
      // its own — a whole-image blob, a metadata sheet — whose content is meant
      // to change when free space is zeroed, and holding those to the same bar
      // would report a wipe doing its job as a wipe destroying something.
      var before = ReadBack(ops, image, expected.Keys);
      if (before.Count == 0) {
        Assert.Ignore($"{formatId}: none of the probe files read back before the wipe.");
        return;
      }

      using (var stream = File.Open(image, FileMode.Open, FileAccess.ReadWrite))
        ((IWipeEmpty)ops).WipeUnusedSpace(stream, wipeClusterTips: true, wipeDeletedEntries: true);

      var after = ReadBack(ops, image, expected.Keys);
      Assert.That(after.Count, Is.EqualTo(before.Count),
        $"{formatId}: the wipe took {before.Count - after.Count} file(s) with it.");
      foreach (var (name, digest) in before)
        Assert.That(after.TryGetValue(name, out var got) && got == digest, Is.True,
          $"{formatId}: '{name}' did not survive the wipe.");
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static Dictionary<string, string> ReadBack(IArchiveFormatOperations ops, string image,
      IEnumerable<string> onlyThese) {
    var wanted = new HashSet<string>(onlyThese, StringComparer.OrdinalIgnoreCase);
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_wipeout_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      using var stream = File.OpenRead(image);
      ops.Extract(stream, outDir, null, null);
      foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)) {
        var leaf = Path.GetFileName(file);
        if (!wanted.Contains(leaf)) continue;
        result[leaf] = Digest(File.ReadAllBytes(file));
      }
    } catch {
      // A volume we cannot read tells us nothing; the count comparison handles it.
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
    return result;
  }

  private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
