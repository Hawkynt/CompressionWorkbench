using System.Diagnostics;
using Compression.Registry;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// External-tool validation for APFS mutated images. Apple's <c>fsck_apfs</c>
/// is macOS-only, <c>apfs-fuse</c> ships read-only check code only, and
/// <c>libfsapfs</c> (Debian package <c>libfsapfs-utils</c>) is read-only too —
/// none of them apply mutations, but they all reject structurally broken images.
/// <para>
/// Each test below produces an APFS image mutated by our modifier, writes it to
/// a temporary file, and asks the available external tool to inspect it. The
/// test passes iff the external tool returns success (exit code 0); if the tool
/// is missing (no WSL, no APFS utilities installed, no admin privileges to
/// install) the test is silently skipped via <see cref="Assert.Ignore"/>.
/// </para>
/// <para>
/// The honest primary acceptance gate remains the in-process
/// <see cref="ApfsStructuralValidator"/> — see
/// <see cref="ApfsFullScopeMutationTests"/>. These external tests are the
/// belt-and-braces validation when an APFS reader is available.
/// </para>
/// </summary>
[TestFixture, Category("ExternalInterop")]
public class ApfsPostMutationExternalTests {

  private static string? _wslApfsinfo;
  private static bool _probed;

  /// <summary>
  /// Probes for an APFS-reading tool inside WSL. Returns the tool name when
  /// found, null when nothing usable is available.
  /// </summary>
  private static string? FindWslApfsinfo() {
    if (_probed) return _wslApfsinfo;
    _probed = true;
    try {
      var psi = new ProcessStartInfo("wsl", "-- bash -c \"which apfsinfo 2>/dev/null\"") {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
      };
      using var proc = Process.Start(psi);
      if (proc == null) return null;
      proc.WaitForExit(5000);
      var stdout = proc.StandardOutput.ReadToEnd().Trim();
      _wslApfsinfo = string.IsNullOrEmpty(stdout) ? null : "apfsinfo";
      return _wslApfsinfo;
    } catch {
      return null;
    }
  }

  /// <summary>
  /// Writes <paramref name="image"/> to a temp file and runs <c>apfsinfo</c>
  /// on it under WSL. Returns the tool's exit code, or -1 when the tool is missing.
  /// </summary>
  private static (int Exit, string Stdout, string Stderr) RunApfsinfo(byte[] image) {
    var tool = FindWslApfsinfo();
    if (tool == null) return (-1, "", "tool not found");

    var tmpFile = Path.Combine(Path.GetTempPath(), $"cwb_apfs_test_{Guid.NewGuid():N}.apfs");
    try {
      File.WriteAllBytes(tmpFile, image);
      // Translate to WSL path.
      var wslPath = "/mnt/" + char.ToLowerInvariant(tmpFile[0]) +
                    tmpFile.Substring(2).Replace('\\', '/');
      var psi = new ProcessStartInfo("wsl", $"-- apfsinfo \"{wslPath}\"") {
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
      };
      using var proc = Process.Start(psi);
      if (proc == null) return (-1, "", "could not start wsl");
      proc.WaitForExit(30000);
      return (proc.ExitCode, proc.StandardOutput.ReadToEnd(), proc.StandardError.ReadToEnd());
    } finally {
      try { File.Delete(tmpFile); } catch { /* best effort */ }
    }
  }

  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new ApfsWriter();
    w.SetMinImageSize(4 * 1024 * 1024);
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    ms.Write(w.Build());
    ms.Position = 0;
    return ms;
  }

  /// <summary>
  /// Given a mutated APFS image (add nested path forcing intermediate dir
  /// inode synthesis), when <c>apfsinfo</c> inspects it, then it returns
  /// success — i.e. the structural format passes a real APFS reader.
  /// </summary>
  [Test]
  public void ApfsInfo_AcceptsAddNestedPath() {
    if (FindWslApfsinfo() == null)
      Assert.Ignore("apfsinfo not available in WSL (libfsapfs-utils not installed " +
                    "or no admin privileges to install).");

    using var img = BuildImage(("seed.txt", "S"u8.ToArray()));
    ((IArchiveModifiable)new ApfsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("sub/dir/added.txt", "added"u8.ToArray())]);

    img.Position = 0;
    var bytes = img.ToArray();
    var (exit, stdout, stderr) = RunApfsinfo(bytes);
    if (exit < 0) Assert.Ignore("apfsinfo invocation failed: " + stderr);

    Assert.That(exit, Is.EqualTo(0),
      $"apfsinfo rejected the mutated image. stdout=[{stdout}] stderr=[{stderr}]");
  }

  /// <summary>
  /// Given an image whose FS-tree was split by our modifier (many records,
  /// internal-index root above leaves), when <c>apfsinfo</c> inspects it,
  /// then it accepts the split tree structure.
  /// </summary>
  [Test]
  public void ApfsInfo_AcceptsSplitFsTree() {
    if (FindWslApfsinfo() == null)
      Assert.Ignore("apfsinfo not available in WSL.");

    var initial = new List<(string, byte[])>();
    for (var i = 0; i < 80; i++)
      initial.Add(($"big_dir/file_with_a_long_name_to_force_split_{i:000}.dat", new byte[32]));
    using var img = BuildImage([.. initial]);

    ((IArchiveModifiable)new ApfsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("big_dir/added_file.dat", "ADD"u8.ToArray())]);

    img.Position = 0;
    var bytes = img.ToArray();
    var (exit, stdout, stderr) = RunApfsinfo(bytes);
    if (exit < 0) Assert.Ignore("apfsinfo invocation failed: " + stderr);

    Assert.That(exit, Is.EqualTo(0),
      $"apfsinfo rejected the split-tree image. stdout=[{stdout}] stderr=[{stderr}]");
  }
}
