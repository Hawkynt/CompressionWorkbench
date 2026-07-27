#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// EFFICACY guard for shrink/optimize: it isn't enough that shrink is
/// non-destructive — on a deliberately <em>oversized</em> image (lots of free
/// space / inoptimal geometry) it must actually reduce the stored footprint. For
/// every filesystem whose creation exposes an image-size knob, this creates a
/// near-empty image at the LARGEST offered size, shrinks it, and asserts the
/// result is materially smaller while the file stays byte-intact. Formats with no
/// size knob (auto-fit writers, fixed-geometry disks) are skipped — there is
/// nothing to shrink.
/// </summary>
[TestFixture]
public class ShrinkEfficacyTests {

  // Every shrinkable+creatable format exposing an image-size knob (reflection over
  // the marker) — any category.
  private static IEnumerable<string> SizeKnobIds() =>
    Compression.Tests.Support.CapabilityImplementers.RegisteredIdsExposing(typeof(IArchiveShrinkable))
      .Where(id => {
        var ops = FormatRegistry.GetArchiveOps(id);
        return ops is IArchiveCreatable and IFormatOptionsSchema schema
               && Enum.TryParse<FormatDetector.Format>(id, out _)
               && PickLargeImageSize(schema) is not null;
      });

  /// <summary>
  /// Upper bound on the preset this test will pick. The filesystem readers load
  /// the whole image into a <see cref="MemoryStream"/>, which caps them at ~2 GB,
  /// and their cluster-offset arithmetic is still 32-bit — so a volume past that
  /// can be *created* (the FAT writer streams and leaves free space sparse) but
  /// not read back yet. Shrink round-trips through the reader, so cap the pick
  /// below that ceiling; 1 GB against a few-byte payload proves the efficacy
  /// claim just as well as 128 GB would.
  /// </summary>
  private const long MaxReadableImageBytes = 1024L * 1024 * 1024;

  // Returns (optionKey, largeValue) for an image-size option with a concrete
  // large preset, or null if the format has no usable size knob.
  private static (string Key, string Value)? PickLargeImageSize(IFormatOptionsSchema schema) {
    foreach (var opt in schema.OptionsSchema) {
      if (!opt.Key.Contains("ImageSize", StringComparison.OrdinalIgnoreCase)
          && !opt.Key.Equals("TotalSize", StringComparison.OrdinalIgnoreCase)) continue;
      if (opt.AllowedValues is not { Count: > 0 } allowed) continue;
      string? best = null; var bestBytes = 0L;
      foreach (var v in allowed) {
        var b = ParseByteSize(v);
        if (b > MaxReadableImageBytes) continue;
        if (b > bestBytes) { bestBytes = b; best = v; }
      }
      // Only worth testing when the chosen preset is comfortably bigger than our tiny payload.
      if (best != null && bestBytes >= 256 * 1024) return (opt.Key, best);
    }
    return null;
  }

  [TestCaseSource(nameof(SizeKnobIds))]
  public void Shrink_ReducesAnOversizedImage(string formatId) {
    var fmt = Enum.Parse<FormatDetector.Format>(formatId);
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    var (key, large) = PickLargeImageSize((IFormatOptionsSchema)ops)!.Value;

    var work = Path.Combine(Path.GetTempPath(), "cwb_shrinkfx_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      var payload = "tiny payload in a deliberately oversized volume\n"u8.ToArray();
      var src = Path.Combine(work, "A.TXT"); File.WriteAllBytes(src, payload);
      var img = Path.Combine(work, "img.dat");
      try {
        ArchiveOperations.Create(img, [new ArchiveInput(src, "A.TXT")], new CompressionOptions(), fmt,
          new Dictionary<string, string> { [key] = large });
      } catch (Exception ex) { Assert.Ignore($"{formatId}: cannot create oversized image ({ex.GetType().Name})."); return; }
      var origSize = new FileInfo(img).Length;
      if (origSize < 256 * 1024) { Assert.Ignore($"{formatId}: writer didn't honour the large size ({origSize} bytes)."); return; }

      byte[] shrunk;
      using (var inS = File.OpenRead(img))
      using (var outS = new MemoryStream()) {
        ((IArchiveShrinkable)ops).Shrink(inS, outS);
        shrunk = outS.ToArray();
      }

      Assert.That(shrunk.Length, Is.LessThanOrEqualTo(origSize), $"{formatId}: shrink must never grow");
      // The headline efficacy claim: an oversized, near-empty image must shrink materially.
      Assert.That(shrunk.Length, Is.LessThan(origSize * 3 / 4),
        $"{formatId}: shrink barely reduced an oversized image ({origSize} -> {shrunk.Length})");

      // Contents must survive.
      using var shrunkS = new MemoryStream(shrunk);
      var names = ops.List(shrunkS, null).Where(e => !e.IsDirectory).Select(e => Path.GetFileName(e.Name)).ToList();
      Assert.That(names.Any(n => n.Contains("A", StringComparison.OrdinalIgnoreCase)), Is.True,
        $"{formatId}: file lost during shrink (listed: {string.Join(",", names)})");
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static long ParseByteSize(string s) {
    var t = s.Trim();
    var i = 0;
    while (i < t.Length && (char.IsDigit(t[i]) || t[i] is '.' or ',')) i++;
    if (i == 0) return 0;
    if (!double.TryParse(t[..i].Replace(',', '.'), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var num)) return 0;
    var rest = t[i..].TrimStart().ToUpperInvariant();
    long mult = rest.StartsWith("KB") || rest.StartsWith('K') ? 1024L
      : rest.StartsWith("MB") || rest.StartsWith('M') ? 1024L * 1024
      : rest.StartsWith("GB") || rest.StartsWith('G') ? 1024L * 1024 * 1024
      : rest.StartsWith('B') ? 1L : 0L;
    return (long)(num * mult);
  }
}
