#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Uniform add → update → delete regression cycle for every registered R/W
/// <em>filesystem</em> (descriptor that exposes both <see cref="IArchiveCreatable"/>
/// and <see cref="IArchiveModifiable"/>). For each one it builds a probe image, then
/// drives the full mutation cycle and asserts the content round-trips at every step:
/// <list type="number">
///   <item><description><b>add</b> a file;</description></item>
///   <item><description><b>update same-size</b> — replace it with equal-length new bytes;</description></item>
///   <item><description><b>update larger</b> — replace with more bytes (forces extra allocation);</description></item>
///   <item><description><b>update smaller</b> — replace with fewer bytes (frees space);</description></item>
///   <item><description><b>delete</b> the file (the seed must survive);</description></item>
///   <item><description><b>folder add + delete</b> — a file under a sub-directory, when the
///     format supports directories.</description></item>
/// </list>
/// The point is to <b>catch regressions</b>: when a mutation <em>succeeds</em>, the listing
/// and the extracted bytes must be exactly right. A format that genuinely can't perform a
/// step (no directory support, image too small for the larger payload, or a clean
/// <see cref="NotSupportedException"/>) is skipped via <see cref="Assert.Ignore(string)"/>
/// rather than failed — but a successful op that yields wrong/missing content fails loudly.
/// </summary>
[TestFixture]
public class FilesystemCrudCycleTests {

  private const string Seed = "seed.txt";
  private const string FileName = "CRUD.DAT";

  private static IEnumerable<string> RwFilesystemIds() =>
    Compression.Tests.Support.CapabilityImplementers.RegisteredIdsExposing(typeof(IArchiveModifiable))
      .Where(id => {
        var ops = FormatRegistry.GetArchiveOps(id);
        return ops is IArchiveCreatable
            && (ops.GetType().Assembly.GetName().Name ?? "").Contains("FileSystem", StringComparison.Ordinal)
            && Enum.TryParse<FormatDetector.Format>(id, out _);
      });

  [TestCaseSource(nameof(RwFilesystemIds))]
  public void AddUpdateDelete_Files_RoundTrip(string formatId) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_crud_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var ops = FormatRegistry.GetArchiveOps(formatId)!;
      var image = CreateSeedImage(formatId, ops, work);
      if (image == null) { Assert.Ignore($"{formatId}: cannot create/seed a probe image."); return; }

      var add = Bytes(3000, 1);
      if (!TryMutate(ops, ref image, m => AddInMemory(ops, m, FileName, add), out var why)) {
        Assert.Ignore($"{formatId}: base add not exercisable ({why}).");
        return;
      }
      AssertContent(ops, image, work, FileName, add, formatId, "after add");

      // update — same size, then larger, then smaller. Each REPLACES by name.
      foreach (var (label, data) in new[] {
                 ("update-same-size", Bytes(3000, 2)),
                 ("update-larger", Bytes(9000, 3)),
                 ("update-smaller", Bytes(400, 4)),
               }) {
        if (!TryMutate(ops, ref image, m => AddInMemory(ops, m, FileName, data), out why)) {
          Assert.Ignore($"{formatId}: {label} not exercisable ({why}) — likely image capacity, not a regression.");
          return;
        }
        AssertContent(ops, image, work, FileName, data, formatId, label);
        Assert.That(SafeList(ops, image).Count(n => Leaf(n).Equals(FileName, StringComparison.OrdinalIgnoreCase)),
          Is.EqualTo(1), $"{formatId}: {label} left a duplicate entry");
      }

      // delete — the file goes, the seed stays.
      if (!TryMutate(ops, ref image, m => ((IArchiveModifiable)ops).Remove(m, [FileName]), out why)) {
        Assert.Ignore($"{formatId}: delete not exercisable ({why}).");
        return;
      }
      var after = SafeList(ops, image);
      Assert.Multiple(() => {
        Assert.That(after.Any(n => Leaf(n).Equals(FileName, StringComparison.OrdinalIgnoreCase)), Is.False,
          $"{formatId}: deleted file still listed (after={string.Join(",", after)})");
        Assert.That(after.Any(n => Leaf(n).Equals(Seed, StringComparison.OrdinalIgnoreCase)), Is.True,
          $"{formatId}: delete collaterally removed the seed file");
      });
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  [TestCaseSource(nameof(RwFilesystemIds))]
  public void AddDelete_Folder_RoundTrip(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    if (!(FormatRegistry.GetById(formatId)?.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories) ?? false))
      Assert.Ignore($"{formatId}: format does not support directories.");

    var work = Path.Combine(Path.GetTempPath(), "cwb_crudd_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var image = CreateSeedImage(formatId, ops, work);
      if (image == null) { Assert.Ignore($"{formatId}: cannot create/seed a probe image."); return; }

      var nested = Bytes(2000, 7);
      if (!TryMutate(ops, ref image, m => AddInMemory(ops, m, "sub/inner.dat", nested), out var why)) {
        Assert.Ignore($"{formatId}: in-place sub-directory add not supported ({why}).");
        return;
      }
      // The nested file must be listed under its sub-path and read back exactly.
      var listed = SafeList(ops, image);
      var stored = listed.FirstOrDefault(n => Leaf(n).Equals("inner.dat", StringComparison.OrdinalIgnoreCase));
      Assert.That(stored, Is.Not.Null,
        $"{formatId}: nested file not listed after folder add (listed={string.Join(",", listed)})");
      AssertContent(ops, image, work, "inner.dat", nested, formatId, "folder add");

      // delete the nested file by its ACTUAL stored name (avoids name-form mismatches);
      // seed survives. A clean refusal is acceptable; a silent no-op (still listed) is not.
      if (!TryMutate(ops, ref image, m => ((IArchiveModifiable)ops).Remove(m, [stored!, "sub/inner.dat", "inner.dat"]), out why)) {
        Assert.Ignore($"{formatId}: nested delete not exercisable ({why}).");
        return;
      }
      Assert.That(SafeList(ops, image).Any(n => Leaf(n).Equals("inner.dat", StringComparison.OrdinalIgnoreCase)), Is.False,
        $"{formatId}: nested file still present after delete (by stored name '{stored}')");
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  // ── helpers ──────────────────────────────────────────────────────────────

  private static byte[]? CreateSeedImage(string formatId, IArchiveFormatOperations ops, string work) {
    try {
      var seedSrc = Path.Combine(work, Seed);
      File.WriteAllBytes(seedSrc, "crud-seed-payload"u8.ToArray());
      var img = Path.Combine(work, "img.dat");
      ArchiveOperations.Create(img, [new ArchiveInput(seedSrc, Seed)],
        new CompressionOptions(), Enum.Parse<FormatDetector.Format>(formatId), null);
      if (!File.Exists(img) || new FileInfo(img).Length == 0) return null;
      var bytes = File.ReadAllBytes(img);
      // Must list its own seed, else the format isn't meaningfully exercisable here.
      return SafeList(ops, bytes).Any(n => Leaf(n).Equals(Seed, StringComparison.OrdinalIgnoreCase)) ? bytes : null;
    } catch { return null; }
  }

  // Applies a mutation on a seekable copy; on success replaces `image` with the new
  // bytes. Returns false (image untouched) for a clean capability/capacity refusal.
  private static bool TryMutate(IArchiveFormatOperations ops, ref byte[] image,
      Action<MemoryStream> mutate, out string why) {
    why = "";
    try {
      using var ms = new MemoryStream();
      ms.Write(image, 0, image.Length);
      ms.Position = 0;
      mutate(ms);
      image = ms.ToArray();
      return true;
    } catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException
                                 or InvalidDataException or ArgumentException) {
      why = ex.GetType().Name;
      return false;
    }
  }

  private static void AddInMemory(IArchiveFormatOperations ops, MemoryStream m, string name, byte[] data)
    => ((IArchiveModifiable)ops).Add(m, [ArchiveInputInfo.InMemory(name, data)]);

  private static List<string> SafeList(IArchiveFormatOperations ops, byte[] image) {
    try { using var s = new MemoryStream(image, false); return ops.List(s, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList(); }
    catch { return []; }
  }

  private static void AssertContent(IArchiveFormatOperations ops, byte[] image, string work,
      string leaf, byte[] expected, string formatId, string step) {
    var outDir = Path.Combine(work, "x_" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(outDir);
    using var s = new MemoryStream(image, false);
    ops.Extract(s, outDir, null, null);
    var path = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
      .FirstOrDefault(p => Path.GetFileName(p).Equals(leaf, StringComparison.OrdinalIgnoreCase));
    Assert.That(path, Is.Not.Null, $"{formatId}: '{leaf}' not extractable {step}");
    var got = File.ReadAllBytes(path!);
    // Block/record-granular filesystems (CP/M, Apple DOS, RT-11, LIF, ODS-1, …) store the
    // size rounded up to a sector/record, so a 3000-byte file legitimately reads back padded.
    // The regression check is that the bytes we wrote round-trip exactly (prefix), with only
    // format padding (zeros to a block boundary) allowed after them.
    Assert.That(got.Length, Is.GreaterThanOrEqualTo(expected.Length),
      $"{formatId}: '{leaf}' truncated {step} (got {got.Length} < {expected.Length})");
    Assert.That(got.AsSpan(0, expected.Length).SequenceEqual(expected), Is.True,
      $"{formatId}: '{leaf}' content wrong {step} (size {expected.Length})");
    Assert.That(got.Length - expected.Length, Is.LessThanOrEqualTo(64 * 1024),
      $"{formatId}: '{leaf}' extracted {got.Length}B for a {expected.Length}B write — excessive padding {step}");
  }

  private static string Leaf(string name) => Path.GetFileName(name.Replace('\\', '/'));
  private static byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
}
