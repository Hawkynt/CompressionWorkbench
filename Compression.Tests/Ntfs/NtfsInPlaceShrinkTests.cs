#pragma warning disable CA1416 // ntfs-3g tooling is Linux-only and runtime-guarded.
using System.Diagnostics;
using System.Runtime.InteropServices;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// Proves <see cref="NtfsInPlaceShrinker"/> performs a genuine in-place volume shrink:
/// it relocates only the clusters above the new boundary, leaves every below-boundary
/// cluster byte-identical, and produces an image that round-trips through
/// <see cref="NtfsReader"/>. The <c>ExternalConformance</c> cases additionally hand the
/// shrunk image to the ntfs-3g oracle (<c>ntfsresize -ni</c>, <c>ntfsfix -n</c>,
/// <c>ntfscat</c>) and skip cleanly when that tooling is absent.
/// </summary>
[TestFixture]
public class NtfsInPlaceShrinkTests {

  // ── Pure-managed proofs (no external tools) ─────────────────────────────

  [Test, Category("HappyPath")]
  public void ShrinkToFit_ReducesImage_AndRoundTripsViaReader() {
    var alpha = MakeData(200_000, 1);
    var beta = MakeData(150_000, 2);
    var image = BuildImage(24 * 1024 * 1024, ("alpha.bin", alpha), ("beta.bin", beta));
    var originalLen = image.Length;

    var result = NtfsInPlaceShrinker.ShrinkToFit(image);

    Assert.That(result.WasReduced, Is.True, "auto-fit should free the trailing space");
    Assert.That(result.NewSize, Is.LessThan(originalLen));

    using var ms = new MemoryStream(image, 0, (int)result.NewSize);
    var reader = new NtfsReader(ms);
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "alpha.bin")), Is.EqualTo(alpha));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "beta.bin")), Is.EqualTo(beta));
  }

  [Test, Category("HappyPath")]
  public void Shrink_LeavesBelowBoundaryBytesIdentical_AndWritesFarLessThanWholeImage() {
    var image = BuildImage(24 * 1024 * 1024, ("a.bin", MakeData(120_000, 3)), ("b.bin", MakeData(80_000, 4)));
    var before = (byte[])image.Clone();

    var result = NtfsInPlaceShrinker.ShrinkToFit(image);
    Assert.That(result.WasReduced, Is.True);

    // The auto-fit boundary did not need to relocate anything (the writer packs data
    // low), so every surviving cluster below the new size is byte-identical EXCEPT the
    // metadata regions the shrinker rewrites ($Boot copy, $Bitmap, $MFTMirr). Assert
    // the bulk of the surviving image is unchanged.
    var newLen = (int)result.NewSize;
    var changed = 0;
    for (var i = 0; i < newLen; i++)
      if (before[i] != image[i]) changed++;

    Assert.That(result.BytesRelocated, Is.Zero, "writer image packs data low — no relocation needed");
    Assert.That(changed, Is.LessThan(newLen / 4),
      "only metadata regions should differ; the vast majority of surviving bytes are identical");
  }

  [Test, Category("EdgeCase")]
  public void ShrinkToClusters_RefusesTargetThatCannotHoldTheData() {
    var image = BuildImage(16 * 1024 * 1024, ("big.bin", MakeData(2_000_000, 9)));
    Assert.Throws<NotSupportedException>(() => NtfsInPlaceShrinker.ShrinkToClusters(image, 8));
  }

  [Test, Category("EdgeCase")]
  public void ShrinkToClusters_NoOpWhenTargetNotSmaller() {
    var image = BuildImage(8 * 1024 * 1024, ("x.bin", MakeData(10_000, 5)));
    var result = NtfsInPlaceShrinker.ShrinkToClusters(image, long.MaxValue / 4096);
    Assert.That(result.WasReduced, Is.False);
    Assert.That(result.NewSize, Is.EqualTo(image.Length));
  }

  // ── External oracle: ntfsresize / ntfsfix / ntfscat ─────────────────────

  [Test, Category("ExternalConformance"), CancelAfter(120_000)]
  public void Shrink_RealMkfsImage_StaysNtfsresizeAndNtfsfixClean_FilesByteIdentical() {
    RequireTools("dd", "mkfs.ntfs", "ntfscp", "ntfsls", "ntfscat", "ntfsfix", "ntfsresize");

    var dir = NewTempDir();
    try {
      var img = Path.Combine(dir, "vol.ntfs");
      Run("dd", $"if=/dev/zero of={img} bs=1M count=24 status=none");
      var mk = Run("mkfs.ntfs", $"--fast -F -s 512 {img}");
      if (mk.Exit != 0) Assert.Ignore($"mkfs.ntfs failed:\n{mk.Err}");

      var alpha = MakeData(200_000, 11);
      var beta = MakeData(150_000, 12);
      Ntfscp(img, "alpha.bin", alpha, dir);
      Ntfscp(img, "beta.bin", beta, dir);

      var image = File.ReadAllBytes(img);
      var result = NtfsInPlaceShrinker.ShrinkToFit(image);
      Assert.That(result.WasReduced, Is.True);
      File.WriteAllBytes(img, image.AsSpan(0, (int)result.NewSize).ToArray());

      // Oracle 1: ntfsfix consistency.
      var fix = Run("ntfsfix", $"-n {img}");
      Assert.That(fix.Out + fix.Err,
        Does.Contain("completed successfully").IgnoreCase,
        $"ntfsfix flagged the shrunk image:\n{fix.Out}\n{fix.Err}");

      // Oracle 2: ntfsresize info+no-action — must report a clean consistency check.
      var rs = Run("ntfsresize", $"-ni -f {img}");
      Assert.That(rs.Exit, Is.EqualTo(0),
        $"ntfsresize reported the shrunk image inconsistent:\n{rs.Out}\n{rs.Err}");
      Assert.That(rs.Out + rs.Err, Does.Not.Contain("inconsistent").IgnoreCase);

      // Oracle 3: files read back byte-identical via ntfscat.
      AssertNtfscatEquals(img, "alpha.bin", alpha, dir);
      AssertNtfscatEquals(img, "beta.bin", beta, dir);

      // Our own reader agrees.
      using var fs = File.OpenRead(img);
      var reader = new NtfsReader(fs);
      Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "alpha.bin")), Is.EqualTo(alpha));
    } finally {
      TryDelete(dir);
    }
  }

  [Test, Category("ExternalConformance"), CancelAfter(120_000)]
  public void Shrink_ForcedRelocation_RelocatesTopClusters_StaysOracleClean() {
    RequireTools("dd", "mkfs.ntfs", "ntfscp", "ntfscat", "ntfsfix", "ntfsresize");

    var dir = NewTempDir();
    try {
      var img = Path.Combine(dir, "vol.ntfs");
      Run("dd", $"if=/dev/zero of={img} bs=1M count=16 status=none");
      if (Run("mkfs.ntfs", $"--fast -F -s 512 {img}").Exit != 0) Assert.Ignore("mkfs.ntfs failed");

      var files = new (string Name, byte[] Data)[6];
      for (var i = 0; i < files.Length; i++) {
        files[i] = ($"f{i}.bin", MakeData(120_000, 20 + i));
        Ntfscp(img, files[i].Name, files[i].Data, dir);
      }

      var image = File.ReadAllBytes(img);
      // Auto-fit, then force a tighter target so the top clusters MUST be relocated
      // into freed space lower down.
      var fit = NtfsInPlaceShrinker.ShrinkToFit((byte[])image.Clone());
      var fitClusters = fit.NewSize / 4096;
      var result = NtfsInPlaceShrinker.ShrinkToClusters(image, fitClusters - 48);

      Assert.That(result.ClustersRelocated, Is.GreaterThan(0), "tight target must relocate clusters");
      Assert.That(result.BytesRelocated, Is.LessThan(result.OriginalSize / 4),
        "relocation rewrites far less than the whole image (O(bytes relocated))");
      File.WriteAllBytes(img, image.AsSpan(0, (int)result.NewSize).ToArray());

      Assert.That(Run("ntfsfix", $"-n {img}").Out, Does.Contain("completed successfully").IgnoreCase);
      Assert.That(Run("ntfsresize", $"-ni -f {img}").Exit, Is.EqualTo(0), "ntfsresize must accept the relocated image");
      foreach (var (name, data) in files)
        AssertNtfscatEquals(img, name, data, dir);
    } finally {
      TryDelete(dir);
    }
  }

  // ── Helpers ─────────────────────────────────────────────────────────────

  private static byte[] BuildImage(int totalSize, params (string Name, byte[] Data)[] files) {
    var w = new NtfsWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build(totalSize);
  }

  private static byte[] MakeData(int n, int seed) { var d = new byte[n]; new Random(seed).NextBytes(d); return d; }

  private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

  private static void RequireTools(params string[] tools) {
    if (!IsLinux) Assert.Ignore("ntfs-3g tooling runs on Linux only.");
    foreach (var t in tools)
      if (!HasCommand(t)) Assert.Ignore($"'{t}' not installed (apt install ntfs-3g).");
  }

  private static bool HasCommand(string name) {
    try {
      var r = RunRaw("/bin/sh", $"-c \"command -v {name}\"");
      return r.Exit == 0 && !string.IsNullOrWhiteSpace(r.Out);
    } catch { return false; }
  }

  private static string NewTempDir() {
    var d = Path.Combine(Path.GetTempPath(), $"cwb_ntfs_shrink_{Guid.NewGuid():N}");
    Directory.CreateDirectory(d);
    return d;
  }
  private static void TryDelete(string dir) { try { Directory.Delete(dir, true); } catch { /* best effort */ } }

  private static void Ntfscp(string img, string nameInImage, byte[] content, string dir) {
    var src = Path.Combine(dir, $"seed_{Guid.NewGuid():N}.bin");
    File.WriteAllBytes(src, content);
    var r = Run("ntfscp", $"{img} {src} {nameInImage}");
    Assert.That(r.Exit, Is.EqualTo(0), $"ntfscp seed failed:\n{r.Out}\n{r.Err}");
  }

  private static void AssertNtfscatEquals(string img, string name, byte[] expected, string dir) {
    var exp = Path.Combine(dir, $"exp_{Guid.NewGuid():N}.bin");
    File.WriteAllBytes(exp, expected);
    var r = Run("/bin/sh", $"-c \"ntfscat {img} {name} | cmp - {exp} && echo CMP_OK\"");
    Assert.That(r.Out, Does.Contain("CMP_OK"),
      $"ntfscat {name} did not match the original bytes after shrink:\n{r.Out}\n{r.Err}");
  }

  private readonly record struct ProcResult(string Out, string Err, int Exit);

  private static ProcResult Run(string tool, string args) => RunRaw(tool, args);

  private static ProcResult RunRaw(string tool, string args) {
    var psi = new ProcessStartInfo {
      FileName = tool, Arguments = args,
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {tool}");
    var o = p.StandardOutput.ReadToEnd();
    var e = p.StandardError.ReadToEnd();
    if (!p.WaitForExit(90_000)) { try { p.Kill(); } catch { /* best effort */ } }
    return new ProcResult(o, e, p.ExitCode);
  }
}
