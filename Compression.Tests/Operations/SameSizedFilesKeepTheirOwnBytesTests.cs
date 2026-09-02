#pragma warning disable CS1591
using System.Security.Cryptography;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Two files of the same length must not come back holding each other's bytes.
/// </summary>
/// <remarks>
/// <para>Nothing else here could have found this. Every probe set in this
/// codebase — the lifecycle test, the wipe tests, the verb harness — gives each
/// file a length of its own, because a spread of sizes is what exercises
/// allocation. A set like that never asks whether a file is identified by what it
/// is or merely by how big it is.</para>
///
/// <para>It showed up sideways: a set of files full of holes had two members that
/// happened to be twenty thousand bytes each, and after a defragmentation each
/// held the other's contents. Same length, right length, wrong file. Nothing
/// reports an error, both files read perfectly, and the only sign is that the
/// bytes belong to somebody else.</para>
/// </remarks>
[TestFixture]
public class SameSizedFilesKeepTheirOwnBytesTests {

  private const int Length = 20_000;

  private static IEnumerable<string> DefragmentableFormats() {
    foreach (var descriptor in FormatRegistry.All.OrderBy(d => d.Id, StringComparer.Ordinal)) {
      var ops = FormatRegistry.GetArchiveOps(descriptor.Id);
      if (ops is not (IArchiveCreatable and IArchiveDefragmentable)) continue;
      if (!Enum.TryParse<FormatDetector.Format>(descriptor.Id, out _)) continue;
      yield return descriptor.Id;
    }
  }

  [TestCaseSource(nameof(DefragmentableFormats)), Category("Regression")]
  public void FilesOfOneLength_KeepTheirOwnContents(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    var format = Enum.Parse<FormatDetector.Format>(formatId);
    var work = Path.Combine(Path.GetTempPath(), "cwb_same_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);

    try {
      // Six files, one length, six unmistakably different contents.
      var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
      var inputs = new List<ArchiveInput>();
      for (var i = 0; i < 6; ++i) {
        var data = new byte[Length];
        for (var j = 0; j < data.Length; ++j) data[j] = (byte)(i * 40 + 1);
        var name = $"SAME{i}.BIN";
        var path = Path.Combine(work, name);
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInput(path, name));
        expected[name] = data;
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
          continue;   // a placement a format is entitled to decline, and says so
        }

        var after = ReadBack(ops, image, expected);
        foreach (var (name, want) in expected) {
          if (!before.ContainsKey(name)) continue;

          Assert.That(after.TryGetValue(name, out var got), Is.True,
            $"{formatId} after {mode}: '{name}' went missing.");

          if (got == Digest(want)) continue;

          // Whose bytes are these? A swap is a different fault from corruption,
          // and saying which it is saves the next person the same afternoon.
          var owner = expected.FirstOrDefault(kv => Digest(kv.Value) == got).Key;
          Assert.Fail($"{formatId} after {mode}: '{name}' came back holding "
            + (owner != null ? $"'{owner}'s bytes — files of one length were matched by size, not identity."
                             : "bytes belonging to no probe file."));
        }
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

  private static Dictionary<string, string> ReadBack(IArchiveFormatOperations ops, string image,
      Dictionary<string, byte[]> expected) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_sameout_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      using (var stream = File.OpenRead(image))
        ops.Extract(stream, outDir, null, null);

      foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)) {
        var leaf = Path.GetFileName(file);
        if (!expected.TryGetValue(leaf, out var want)) continue;

        var got = File.ReadAllBytes(file);
        // Trailing padding is the format's own granularity, not a swap. What
        // proves identity is that the content STARTS with this file's bytes and
        // the rest is zero — no other probe can satisfy that, since the probes
        // differ from their first byte. The cap only stops the tolerance from
        // swallowing an unbounded tail, so it has to clear a real allocation
        // unit: a PS1 memory-card save owns whole 8 KiB blocks, and 4096 was
        // under that, which read the format's own block granularity as a file
        // holding bytes that belong to nobody.
        if (got.Length > want.Length && got.Length - want.Length < 64 * 1024
            && got.AsSpan(0, want.Length).SequenceEqual(want)
            && !got.AsSpan(want.Length).ContainsAnyExcept((byte)0))
          got = want;

        result[leaf] = Digest(got);
      }
    } catch {
      // Unreadable tells us nothing; the missing-file assertion covers it.
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
    return result;
  }
}
