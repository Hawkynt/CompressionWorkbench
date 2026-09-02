#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Anti-corruption guard for <see cref="IArchiveDefragmentable"/> across <b>every</b>
/// creatable format (filesystems and archives, bespoke implementations included).
/// Defragment must be non-destructive: after the call the image is still readable and
/// lists the <em>exact same multiset of entry names</em> it listed before, OR the call
/// threw (in-place edits/rebuilds commit only on success, so a throw leaves the original
/// intact). This pins shut the whole corruption class found via fuzzing — AppleDOS's
/// planner mangling the catalog, and the MIX/LFD rebuilds re-hashing or duplicating
/// entries because their content-addressed identities can't survive extract→recreate.
/// </summary>
[TestFixture]
[Category("Slow")]
public class DefragNoCorruptionTests {

  // EVERY creatable defragmentable format (reflection over the marker) — filesystems
  // AND archives, bespoke AND default. The anti-corruption invariant is universal.
  private static IEnumerable<string> DefragmentableCreatableIds() =>
    Compression.Tests.Support.CapabilityImplementers.RegisteredIdsExposing(typeof(IArchiveDefragmentable))
      .Where(id => FormatRegistry.GetArchiveOps(id) is IArchiveCreatable);

  // A handful of payloads — corruption was payload-dependent (AppleDOS only mangled
  // on certain catalog layouts), so probe several.
  private static byte[] Payload(int kind, int n) {
    var b = new byte[n];
    for (var i = 0; i < n; i++)
      b[i] = kind switch { 0 => 0, 1 => (byte)(i * 17 + 3), 2 => (byte)(i * 31 + 7), _ => 0xFF };
    return b;
  }

  [TestCaseSource(nameof(DefragmentableCreatableIds))]
  public void Defragment_NeverCorrupts(string formatId) {
    if (!Enum.TryParse<FormatDetector.Format>(formatId, out var fmt))
      Assert.Ignore($"{formatId}: registry id is not a FormatDetector.Format enum value (can't target via Create).");
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    var exercised = false;

    foreach (var (kind, sz) in new[] { (0, 4096), (1, 4096), (2, 2048), (3, 8192) }) {
      var work = Path.Combine(Path.GetTempPath(), "cwb_dnc_" + Guid.NewGuid().ToString("N")[..8]);
      Directory.CreateDirectory(work);
      try {
        var aSrc = Path.Combine(work, "A.TXT"); File.WriteAllBytes(aSrc, "defrag corruption probe\n"u8.ToArray());
        var bSrc = Path.Combine(work, "B.BIN"); File.WriteAllBytes(bSrc, Payload(kind, sz));
        var img = Path.Combine(work, "img.dat");
        try {
          ArchiveOperations.Create(img, [new ArchiveInput(aSrc, "A.TXT"), new ArchiveInput(bSrc, "B.BIN")],
            new CompressionOptions(), fmt, null);
        } catch { continue; } // create can't take trivial input for this format/payload
        if (!File.Exists(img) || new FileInfo(img).Length == 0) continue;

        var before = SafeList(ops, img);
        if (before == null || before.Count == 0) continue;
        exercised = true;

        var bytes = File.ReadAllBytes(img);
        using var ms = new MemoryStream();
        ms.Write(bytes); ms.Position = 0;
        try {
          ((IArchiveDefragmentable)ops).Defragment(ms);
        } catch {
          continue; // threw → original bytes untouched (non-destructive)
        }

        var after = SafeList(ops, ms);
        Assert.That(after, Is.Not.Null,
          $"{formatId}: defrag reported success but produced an UNREADABLE image (kind={kind}, sz={sz})");
        Assert.That(Sorted(after!), Is.EqualTo(Sorted(before)),
          $"{formatId}: defrag changed the entry set (kind={kind}, sz={sz})\n  before: {string.Join(",", before)}\n  after:  {string.Join(",", after!)}");
      } finally {
        try { Directory.Delete(work, true); } catch { /* best effort */ }
      }
    }

    if (!exercised) Assert.Ignore($"{formatId}: not exercisable from trivial input.");
  }

  private static List<string> Sorted(List<string> xs) { var c = new List<string>(xs); c.Sort(StringComparer.Ordinal); return c; }

  private static List<string>? SafeList(IArchiveFormatOperations ops, string path) {
    try { using var s = File.OpenRead(path); s.Position = 0; return ops.List(s, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList(); }
    catch { return null; }
  }

  private static List<string>? SafeList(IArchiveFormatOperations ops, Stream s) {
    try { s.Position = 0; return ops.List(s, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList(); }
    catch { return null; }
  }
}
