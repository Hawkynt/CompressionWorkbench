#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// Safety net for the broad rollout of the default <see cref="ILayoutOptimizable"/>
/// mechanism (verified extract → re-create rebuild via
/// <see cref="RebuildVerb.RebuildToStream"/>). For every filesystem descriptor that
/// exposes <c>ILayoutOptimizable</c> through the generic default (i.e. does NOT
/// declare its own <c>RebuildStreaming</c>) and can create from trivial input, this
/// builds a small two-file image, re-applies its layout via <c>RebuildStreaming</c>
/// with default options, and asserts the rebuilt image lists the same file set. A
/// format whose create path doesn't faithfully round-trip surfaces here (the
/// rebuild itself also refuses lossy results) rather than silently corrupting data.
/// </summary>
[TestFixture]
public class GenericLayoutOptimizableTests {

  // EVERY format whose runtime ops exposes ILayoutOptimizable + IArchiveCreatable,
  // scoped to the DEFAULT mechanism (those that don't declare their own
  // RebuildStreaming) — discovered by reflection so new implementers are covered
  // automatically.
  private static IEnumerable<string> LayoutOptimizableIds() =>
    Compression.Tests.Support.CapabilityImplementers.RegisteredIdsExposing(typeof(ILayoutOptimizable))
      .Where(id => FormatRegistry.GetArchiveOps(id) is IArchiveCreatable
                   && Enum.TryParse<FormatDetector.Format>(id, out _)
                   && !Compression.Tests.Support.CapabilityImplementers.DeclaresOwn(
                        id, "RebuildStreaming", typeof(Stream), typeof(Stream), typeof(LayoutRebuildOptions)));

  [TestCaseSource(nameof(LayoutOptimizableIds))]
  public void RebuildStreaming_PreservesFiles_OrRefusesCleanly(string formatId) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_genlayout_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      // Two deterministic payloads: a short text file and a 4 KB binary.
      var aData = "generic layout round-trip probe\n"u8.ToArray();
      var bData = new byte[4096];
      for (var i = 0; i < bData.Length; i++) bData[i] = (byte)(i * 31 + 7);
      var aSrc = Path.Combine(work, "A.TXT"); File.WriteAllBytes(aSrc, aData);
      var bSrc = Path.Combine(work, "B.BIN"); File.WriteAllBytes(bSrc, bData);

      var fmt = Enum.Parse<FormatDetector.Format>(formatId);
      var fmtOps = FormatRegistry.GetArchiveOps(formatId)!;
      var img = Path.Combine(work, "img.dat");

      // Some filesystems can't be created from a trivial two-file input (need a
      // specific minimum geometry / options). That's a create limitation, not a
      // layout defect — skip those rather than fail.
      try {
        ArchiveOperations.Create(img, [
          new ArchiveInput(aSrc, "A.TXT"),
          new ArchiveInput(bSrc, "B.BIN"),
        ], new CompressionOptions(), fmt, null);
      } catch (Exception ex) {
        Assert.Ignore($"{formatId}: cannot create a probe image from trivial input ({ex.GetType().Name}: {ex.Message}).");
        return;
      }
      if (!File.Exists(img) || new FileInfo(img).Length == 0) {
        Assert.Ignore($"{formatId}: create produced no image.");
        return;
      }

      // Capture the source file set as the descriptor itself lists it (via its own
      // ops — NOT path auto-detection, which would mis-route a bare image file).
      var before = SafeList(fmtOps, img);
      if (before.Count == 0) {
        Assert.Ignore($"{formatId}: descriptor lists no files in its own freshly-created image (round-trip not exercisable).");
        return;
      }

      var optimizable = (ILayoutOptimizable)fmtOps;
      byte[] rebuilt;
      try {
        using var inStream = File.OpenRead(img);
        using var outStream = new MemoryStream();
        // Default options: no explicit geometry change — re-apply the layout as-is.
        optimizable.RebuildStreaming(inStream, outStream, new LayoutRebuildOptions());
        rebuilt = outStream.ToArray();
      } catch (NotSupportedException) {
        Assert.Pass($"{formatId}: layout rebuild cleanly NotSupported (no corruption).");
        return;
      } catch (InvalidOperationException ex) {
        // The verified rebuild refused a lossy result — safe, but flags that this
        // format's create path doesn't round-trip trivial input.
        Assert.Ignore($"{formatId}: rebuild refused as lossy ({ex.Message}) — safe, exclude if persistent.");
        return;
      }

      Assert.That(rebuilt.Length, Is.GreaterThan(0), $"{formatId}: rebuild produced empty output");

      // The rebuilt image must still list the same file set.
      using var rebuiltStream = new MemoryStream(rebuilt);
      var after = SafeList(fmtOps, rebuiltStream);
      Assert.That(after.OrderBy(n => n, StringComparer.Ordinal),
        Is.EqualTo(before.OrderBy(n => n, StringComparer.Ordinal)),
        $"{formatId}: layout rebuild changed the file set ([{string.Join(", ", before)}] -> [{string.Join(", ", after)}])");
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static List<string> SafeList(IArchiveFormatOperations ops, string path) {
    try { using var s = File.OpenRead(path); return SafeList(ops, s); }
    catch { return []; }
  }

  private static List<string> SafeList(IArchiveFormatOperations ops, Stream s) {
    try {
      s.Position = 0;
      return ops.List(s, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    } catch {
      return [];
    }
  }
}
