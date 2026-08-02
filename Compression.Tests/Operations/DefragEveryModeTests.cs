#pragma warning disable CS1591
using System.Security.Cryptography;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Every layout a format offers has to give the files back unchanged.
/// </summary>
/// <remarks>
/// <para>The check that existed compared the list of names before and after,
/// in one mode. A file can keep its name and lose its bytes, and most of the
/// faults found in these formats did exactly that — a chain relinked to the
/// wrong place, a run repointed to a record belonging to something else. So
/// this compares contents, and does it for each mode, because the ones that
/// pack against the tail exercise orderings the others never reach.</para>
///
/// <para>A format may say it does not offer a mode; that is an answer, not a
/// failure. Anything else escaping is.</para>
/// </remarks>
[TestFixture]
public class DefragEveryModeTests {

  private static readonly DefragMode[] Modes = [
    DefragMode.ConsolidateAtStart,
    DefragMode.ConsolidateAtEnd,
    DefragMode.FillHolesLazy,
  ];

  private static IEnumerable<string> DefragmentableFormats() {
    foreach (var descriptor in FormatRegistry.All.OrderBy(d => d.Id, StringComparer.Ordinal)) {
      var ops = FormatRegistry.GetArchiveOps(descriptor.Id);
      if (ops is not (IArchiveCreatable and IArchiveDefragmentable)) continue;
      if (!Enum.TryParse<FormatDetector.Format>(descriptor.Id, out _)) continue;
      yield return descriptor.Id;
    }
  }

  [TestCaseSource(nameof(DefragmentableFormats))]
  public void EveryMode_GivesTheFilesBackUnchanged(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    var format = Enum.Parse<FormatDetector.Format>(formatId);
    var work = Path.Combine(Path.GetTempPath(), "cwb_modes_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);

    try {
      var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      var inputs = new List<ArchiveInput>();
      for (var i = 0; i < 4; ++i) {
        var payload = new byte[900 + i * 500];
        for (var b = 0; b < payload.Length; ++b) payload[b] = (byte)(b * 7 + i * 53);
        var path = Path.Combine(work, $"M{i}.BIN");
        File.WriteAllBytes(path, payload);
        inputs.Add(new ArchiveInput(path, $"M{i}.BIN"));
        expected[$"M{i}.BIN"] = Digest(payload);
      }

      var pristine = Path.Combine(work, "pristine.img");
      try {
        ArchiveOperations.Create(pristine, inputs, new CompressionOptions(), format, null);
      } catch (Exception ex) {
        Assert.Ignore($"{formatId}: cannot create a probe volume ({ex.GetType().Name}).");
        return;
      }
      if (!File.Exists(pristine) || new FileInfo(pristine).Length == 0) {
        Assert.Ignore($"{formatId}: produced no image.");
        return;
      }

      var baseline = ReadBack(ops, pristine, expected.Keys);
      if (baseline.Count == 0) {
        Assert.Ignore($"{formatId}: none of the probe files read back before defragmenting.");
        return;
      }

      foreach (var mode in Modes) {
        var copy = Path.Combine(work, $"{mode}.img");
        File.Copy(pristine, copy, true);

        try {
          using var stream = File.Open(copy, FileMode.Open, FileAccess.ReadWrite);
          ((IArchiveDefragmentable)ops).Defragment(stream, new DefragOptions { Mode = mode });
        } catch (NotSupportedException) {
          continue;                       // the format says it does not lay out that way
        }

        var after = ReadBack(ops, copy, expected.Keys);
        Assert.That(after.Count, Is.EqualTo(baseline.Count),
          $"{formatId}: {mode} lost {baseline.Count - after.Count} file(s).");
        foreach (var (name, digest) in baseline)
          Assert.That(after.TryGetValue(name, out var got) && got == digest, Is.True,
            $"{formatId}: {mode} did not give '{name}' back unchanged.");
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static Dictionary<string, string> ReadBack(IArchiveFormatOperations ops, string image,
      IEnumerable<string> onlyThese) {
    var wanted = new HashSet<string>(onlyThese, StringComparer.OrdinalIgnoreCase);
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_modeout_" + Guid.NewGuid().ToString("N")[..8]);
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
      // An unreadable volume shows up as a missing file, which the count catches.
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
    return result;
  }

  private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
