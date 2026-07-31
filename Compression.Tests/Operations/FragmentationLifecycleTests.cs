#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;
using Compression.Tests.Support;

namespace Compression.Tests.Operations;

/// <summary>
/// Drives a filesystem through the whole maintenance lifecycle on a volume that
/// has been deliberately fragmented, and checks after every step that the files
/// are still there — with third-party software wherever the host has some.
/// </summary>
/// <remarks>
/// <para>The shape of one run:</para>
/// <list type="number">
///   <item><description>create a volume of alternating keep/filler files;</description></item>
///   <item><description>remove every filler, leaving holes between the keepers;</description></item>
///   <item><description>add a file large enough that it has to be split across
///   those holes — that is the fragmentation the rest of the run operates
///   on;</description></item>
///   <item><description>defragment towards the end of the volume, then
///   hole-filling, then towards the start;</description></item>
///   <item><description>shrink, then optimise.</description></item>
/// </list>
///
/// <para>After each of those, every payload must read back byte-exact: through
/// this repository's own reader always, and additionally through the host
/// kernel's driver for that filesystem, 7-Zip, or the filesystem's own
/// <c>fsck</c> when one of those is available. A format that offers no verb for
/// a step has that step skipped, and a step that no third-party tool can witness
/// says so in the assertion message rather than quietly passing as verified.</para>
/// </remarks>
[TestFixture]
[Category("ExternalFsInterop")]
public class FragmentationLifecycleTests {

  /// <summary>Payload of one file. Small enough to keep the run quick, large
  /// enough that most allocators need several blocks for it.</summary>
  private const int FileBytes = 48 * 1024;

  /// <summary>Files the volume starts with, alternating keep and filler.</summary>
  private const int InitialFiles = 8;

  /// <summary>
  /// Formats worth driving: they must be creatable, defragmentable, and have a
  /// reader on this host that is not ours — the point of the exercise is the
  /// outside opinion.
  /// </summary>
  private static IEnumerable<string> LifecycleFormats() {
    foreach (var descriptor in FormatRegistry.All.OrderBy(d => d.Id, StringComparer.Ordinal)) {
      var ops = FormatRegistry.GetArchiveOps(descriptor.Id);
      if (ops is not (IArchiveCreatable and IArchiveDefragmentable)) continue;
      if (!Enum.TryParse<FormatDetector.Format>(descriptor.Id, out _)) continue;
      if (!ThirdPartyFsCheck.IsSupported(descriptor.Id)) continue;
      yield return descriptor.Id;
    }
  }

  [TestCaseSource(nameof(LifecycleFormats))]
  public void FragmentedVolume_SurvivesDefragEachWay_ThenShrinkAndOptimise(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    var format = Enum.Parse<FormatDetector.Format>(formatId);
    var work = Path.Combine(Path.GetTempPath(), "cwb_frag_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);

    try {
      // ── 1. a volume of alternating keep/filler files ────────────────────
      var sources = new List<ArchiveInput>();
      var keep = new List<byte[]>();
      var fillerNames = new List<string>();
      for (var i = 0; i < InitialFiles; ++i) {
        var payload = Payload(i);
        var name = (i % 2 == 0 ? "KEEP" : "FILL") + i.ToString("D2");
        var path = Path.Combine(work, name + ".BIN");
        File.WriteAllBytes(path, payload);
        sources.Add(new ArchiveInput(path, name + ".BIN"));
        if (i % 2 == 0) keep.Add(payload); else fillerNames.Add(name + ".BIN");
      }

      var image = Path.Combine(work, "volume.img");
      try {
        ArchiveOperations.Create(image, sources, new CompressionOptions(), format, null);
      } catch (Exception ex) {
        Assert.Ignore($"{formatId}: cannot create a {InitialFiles}-file probe volume ({ex.GetType().Name}: " +
                      $"{ex.Message.Split('\n')[0]}).");
        return;
      }
      if (!File.Exists(image) || new FileInfo(image).Length == 0) {
        Assert.Ignore($"{formatId}: produced no image.");
        return;
      }

      // ── 2. punch holes ──────────────────────────────────────────────────
      var fragmented = false;
      if (ops is IArchiveModifiable modifier) {
        try {
          using (var stream = File.Open(image, FileMode.Open, FileAccess.ReadWrite))
            modifier.Remove(stream, fillerNames.ToArray());

          // ── 3. a file that has to land in more than one hole ─────────────
          var bigPayload = Payload(0x5A, FileBytes * 3);
          var bigPath = Path.Combine(work, "BIG.BIN");
          File.WriteAllBytes(bigPath, bigPayload);
          using (var stream = File.Open(image, FileMode.Open, FileAccess.ReadWrite))
            modifier.Add(stream, [new ArchiveInputInfo(bigPath, "BIG.BIN", false)]);
          keep.Add(bigPayload);
          fragmented = true;
        } catch (Exception ex) {
          // A format that cannot mutate in place still gets the rest of the
          // lifecycle, just without the holes.
          TestContext.Out.WriteLine(
            $"{formatId}: could not fragment in place ({ex.GetType().Name}); " +
            "continuing with the freshly created layout.");
          keep.Clear();
          for (var i = 0; i < InitialFiles; ++i) keep.Add(Payload(i));
        }
      } else {
        keep.Clear();
        for (var i = 0; i < InitialFiles; ++i) keep.Add(Payload(i));
      }

      var runs = CountRuns(ops, image);
      TestContext.Out.WriteLine(
        $"{formatId}: {(fragmented ? "fragmented" : "unfragmented")} volume, " +
        $"{new FileInfo(image).Length:N0} bytes, {runs} used extents.");

      Verify(formatId, ops, image, keep, "after fragmenting");

      // ── 4. defragment, one mode at a time ───────────────────────────────
      var defragmenter = (IArchiveDefragmentable)ops;
      foreach (var mode in new[] { DefragMode.ConsolidateAtEnd, DefragMode.FillHolesLazy,
                                   DefragMode.ConsolidateAtStart }) {
        var applied = true;
        try {
          using var stream = File.Open(image, FileMode.Open, FileAccess.ReadWrite);
          defragmenter.Defragment(stream, new DefragOptions { Mode = mode });
        } catch (NotSupportedException) {
          applied = false;   // the format says it cannot lay files out that way
        }
        if (!applied) {
          TestContext.Out.WriteLine($"{formatId}: {mode} is not offered; skipped.");
          continue;
        }
        Verify(formatId, ops, image, keep, $"after Defragment({mode})");
      }

      // ── 5. shrink ───────────────────────────────────────────────────────
      if (ops is IArchiveShrinkable shrinkable) {
        var shrunk = image + ".shrunk";
        try {
          using (var input = File.OpenRead(image))
          using (var output = File.Create(shrunk))
            shrinkable.Shrink(input, output);
          File.Move(shrunk, image, overwrite: true);
          Verify(formatId, ops, image, keep, "after Shrink");
        } finally {
          if (File.Exists(shrunk)) File.Delete(shrunk);
        }
      }

      // ── 6. optimise ─────────────────────────────────────────────────────
      if (ops is ILayoutOptimizable optimizable) {
        LayoutAnalysis? analysis = null;
        try {
          using var stream = File.OpenRead(image);
          analysis = optimizable.AnalyzeLayout(stream);
        } catch (Exception ex) {
          TestContext.Out.WriteLine($"{formatId}: AnalyzeLayout declined ({ex.GetType().Name}).");
        }

        if (analysis != null) {
          var rebuilt = image + ".opt";
          var rebuiltOk = false;
          try {
            using (var input = File.OpenRead(image))
            using (var output = File.Create(rebuilt))
              optimizable.RebuildStreaming(input, output,
                new LayoutRebuildOptions { UnitSize = analysis.OptimalUnitSize });
            rebuiltOk = new FileInfo(rebuilt).Length > 0;
          } catch (Exception ex) {
            TestContext.Out.WriteLine($"{formatId}: RebuildStreaming declined ({ex.GetType().Name}).");
          }

          if (rebuiltOk) {
            File.Move(rebuilt, image, overwrite: true);
            Verify(formatId, ops, image, keep, "after optimise");
          } else if (File.Exists(rebuilt)) {
            File.Delete(rebuilt);
          }
        }
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// Every payload must read back through our own reader, and — when the host
  /// has one — through software that is not ours.
  /// </summary>
  private static void Verify(string formatId, IArchiveFormatOperations ops, string image,
      IReadOnlyList<byte[]> expected, string stage) {
    var ours = ReadBackOurselves(ops, image);
    foreach (var payload in expected)
      Assert.That(ours.Contains(Hash(payload)), Is.True,
        $"{formatId} {stage}: a payload of {payload.Length:N0} bytes did not come back " +
        $"through our own reader ({ours.Count} files read).");

    var third = ThirdPartyFsCheck.ReadBack(formatId, image, expected);
    if (third.Ran)
      Assert.That(third.Ok, Is.True, $"{formatId} {stage}: {third.Tool} did not read the payload back — {third.Detail}");
    else
      TestContext.Out.WriteLine($"{formatId} {stage}: no third-party reader ran — {third.Detail}");

    var fsck = ThirdPartyFsCheck.Fsck(formatId, image);
    if (fsck.Ran)
      Assert.That(fsck.Ok, Is.True, $"{formatId} {stage}: {fsck.Tool} reported a problem — {fsck.Detail}");
  }

  private static HashSet<string> ReadBackOurselves(IArchiveFormatOperations ops, string image) {
    var digests = new HashSet<string>(StringComparer.Ordinal);
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_ours_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      using (var stream = File.OpenRead(image))
        ops.Extract(stream, outDir, null, null);
      foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
        digests.Add(Hash(File.ReadAllBytes(file)));
    } catch {
      // An unreadable image simply yields nothing, and the caller's assertion
      // then names which payload went missing.
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
    return digests;
  }

  /// <summary>Used extents, when the format can say — the fragmentation witness.</summary>
  private static int CountRuns(IArchiveFormatOperations ops, string image) {
    if (ops is not IFilesystemExtentMap map) return -1;
    try {
      using var stream = File.OpenRead(image);
      return map.EnumerateExtents(stream).Count(e => e.Kind == DefragBlockKind.Used);
    } catch {
      return -1;
    }
  }

  private static byte[] Payload(int seed, int length = FileBytes) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i)
      data[i] = (byte)(i * 31 + seed * 7);
    return data;
  }

  private static string Hash(byte[] data)
    => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
}
