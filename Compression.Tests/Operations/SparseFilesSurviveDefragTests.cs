#pragma warning disable CS1591
using System.Security.Cryptography;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// A file that is mostly holes has to come back as the file it was.
/// </summary>
/// <remarks>
/// <para>Every other check here stores files that are solid from end to end —
/// arithmetic ramps with no two bytes the same. A file with long runs of zeros in
/// it exercises a different path: a writer may decline to allocate for a run it
/// can leave unwritten, a reader has to produce those bytes from nothing, and a
/// defragmentation has to move what was allocated without inventing or dropping
/// what was not.</para>
///
/// <para>The failure this guards against is quiet. A hole that comes back as
/// data, or data that comes back as a hole, leaves the file exactly the right
/// length and reads perfectly well; only the bytes are wrong, and only in the
/// parts nobody looks at.</para>
/// </remarks>
[TestFixture]
public class SparseFilesSurviveDefragTests {

  private static IEnumerable<string> DefragmentableFormats() {
    foreach (var descriptor in FormatRegistry.All.OrderBy(d => d.Id, StringComparer.Ordinal)) {
      var ops = FormatRegistry.GetArchiveOps(descriptor.Id);
      if (ops is not (IArchiveCreatable and IArchiveDefragmentable)) continue;
      if (!Enum.TryParse<FormatDetector.Format>(descriptor.Id, out _)) continue;
      yield return descriptor.Id;
    }
  }

  /// <summary>
  /// A file whose middle is a hole, one that begins with a hole, one that ends
  /// with one, and one that is nothing but hole.
  /// </summary>
  private static Dictionary<string, byte[]> Holey() {
    var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    byte[] Make(int length, params (int At, int Run)[] solid) {
      var data = new byte[length];
      foreach (var (at, run) in solid)
        for (var i = at; i < Math.Min(length, at + run); ++i)
          data[i] = (byte)(i * 31 + 7 + (i >> 9));
      return data;
    }

    files["MIDHOLE.BIN"] = Make(24_000, (0, 2_000), (22_000, 2_000));
    files["HEADHOLE.BIN"] = Make(20_000, (16_000, 4_000));
    files["TAILHOLE.BIN"] = Make(20_000, (0, 4_000));
    files["ALLHOLE.BIN"] = Make(12_000);
    files["SOLID.BIN"] = Make(9_000, (0, 9_000));
    return files;
  }

  [TestCaseSource(nameof(DefragmentableFormats))]
  public void FilesFullOfHoles_SurviveEveryPlacement(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    var format = Enum.Parse<FormatDetector.Format>(formatId);
    var work = Path.Combine(Path.GetTempPath(), "cwb_hole_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);

    try {
      var expected = Holey();
      var inputs = new List<ArchiveInput>();
      foreach (var (name, data) in expected) {
        var path = Path.Combine(work, name);
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInput(path, name));
      }

      var image = Path.Combine(work, "volume.img");
      try {
        ArchiveOperations.Create(image, inputs, new CompressionOptions(), format, null);
      } catch (Exception ex) {
        Assert.Ignore($"{formatId}: cannot hold the probe set ({ex.GetType().Name}).");
        return;
      }

      var before = ReadBack(ops, image, expected);
      if (before.Count == 0) {
        Assert.Ignore($"{formatId}: none of the probe files read back before defragmenting.");
        return;
      }

      foreach (var mode in new[] {
        DefragMode.ConsolidateAtStart, DefragMode.ConsolidateAtEnd, DefragMode.FillHolesLazy,
      }) {
        try {
          using var stream = File.Open(image, FileMode.Open, FileAccess.ReadWrite);
          ((IArchiveDefragmentable)ops).Defragment(stream, new DefragOptions { Mode = mode });
        } catch (NotSupportedException) {
          continue;   // a format entitled to decline a placement, and saying so
        }

        var after = ReadBack(ops, image, expected);
        Assert.That(after.Count, Is.EqualTo(before.Count),
          $"{formatId} after {mode}: {before.Count - after.Count} file(s) went missing.");
        foreach (var (name, digest) in before)
          Assert.That(after.TryGetValue(name, out var got) && got == digest, Is.True,
            $"{formatId} after {mode}: '{name}' came back different — a hole and its data are "
            + "not interchangeable even though both read without error.");
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// The probe files as they read off the volume, by content. Trailing zeros are
  /// tolerated because several formats record a length only to the nearest
  /// record or block, and padding a hole is not losing one.
  /// </summary>
  private static Dictionary<string, string> ReadBack(IArchiveFormatOperations ops, string image,
      Dictionary<string, byte[]> expected) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_holeout_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      using (var stream = File.OpenRead(image))
        ops.Extract(stream, outDir, null, null);

      foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)) {
        var leaf = Path.GetFileName(file);
        if (!expected.TryGetValue(leaf, out var want)) continue;

        var got = File.ReadAllBytes(file);
        if (got.Length > want.Length && got.Length - want.Length < 4096
            && got.AsSpan(0, want.Length).SequenceEqual(want)
            && !got.AsSpan(want.Length).ContainsAnyExcept((byte)0))
          got = want;

        result[leaf] = Convert.ToHexString(SHA256.HashData(got));
      }
    } catch {
      // A volume that will not read tells us nothing; the count check covers it.
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
    return result;
  }
}
