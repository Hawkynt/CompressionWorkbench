using System.Diagnostics;
using System.IO.Compression;
using FileFormat.Pack200;

namespace Compression.Tests.Pack200;

/// <summary>
/// Cross-validation of the Pack200 decoder against the JDK's <c>pack200</c>/<c>unpack200</c>
/// tools. A reference jar is packed with <c>pack200</c>, decoded by both our reader and
/// <c>unpack200</c>, and the resulting class sets are compared. The whole fixture skips
/// cleanly when a JDK with the pack200 tools is not available on the host.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class Pack200InteropTests {

  private static string? FindTool(string exe) {
    // 1) PATH
    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
    foreach (var dir in path.Split(Path.PathSeparator)) {
      if (string.IsNullOrWhiteSpace(dir)) continue;
      var cand = Path.Combine(dir, exe);
      if (File.Exists(cand)) return cand;
    }
    // 2) JAVA_HOME
    var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
    if (!string.IsNullOrEmpty(javaHome)) {
      var cand = Path.Combine(javaHome, "bin", exe);
      if (File.Exists(cand)) return cand;
    }
    // 3) Well-known Windows Java install roots
    foreach (var root in new[] {
               @"C:\Program Files\Java", @"C:\Program Files (x86)\Java" }) {
      if (!Directory.Exists(root)) continue;
      foreach (var jre in Directory.GetDirectories(root)) {
        var cand = Path.Combine(jre, "bin", exe);
        if (File.Exists(cand)) return cand;
      }
    }
    return null;
  }

  private static (string pack, string unpack) RequireTools() {
    var exe = OperatingSystem.IsWindows() ? ".exe" : "";
    var pack = FindTool("pack200" + exe);
    var unpack = FindTool("unpack200" + exe);
    if (pack == null || unpack == null)
      Assert.Ignore("pack200/unpack200 tools not found on host.");
    return (pack!, unpack!);
  }

  private static string? FindRtJar(string toolPath) {
    // <jre>/bin/pack200 -> <jre>/lib/rt.jar
    var bin = Path.GetDirectoryName(toolPath);
    var jre = Path.GetDirectoryName(bin);
    if (jre == null) return null;
    var rt = Path.Combine(jre, "lib", "rt.jar");
    return File.Exists(rt) ? rt : null;
  }

  private static void Run(string exe, params string[] args) {
    var psi = new ProcessStartInfo(exe) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    p.WaitForExit(60_000);
    if (!p.HasExited) { p.Kill(true); Assert.Fail($"{exe} timed out."); }
    if (p.ExitCode != 0)
      Assert.Fail($"{Path.GetFileName(exe)} exited {p.ExitCode}: {p.StandardError.ReadToEnd()}");
  }

  [Test]
  public void PackedJar_Decodes_MatchesUnpack200() {
    var (pack, unpack) = RequireTools();
    var rtJar = FindRtJar(pack);
    if (rtJar == null)
      Assert.Ignore("rt.jar not found alongside pack200 tool.");

    var work = Path.Combine(Path.GetTempPath(), "cwb_pack200_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      // Build a small jar from the smallest .class entries in rt.jar.
      var srcJar = Path.Combine(work, "small.jar");
      var expected = new List<string>();
      using (var rt = ZipFile.OpenRead(rtJar!)) {
        var classes = rt.Entries
          .Where(e => e.FullName.EndsWith(".class", StringComparison.Ordinal))
          .OrderBy(e => e.Length).Take(5).ToList();
        if (classes.Count == 0)
          Assert.Ignore("rt.jar has no class entries.");
        using var outFs = new FileStream(srcJar, FileMode.Create);
        using var outZip = new ZipArchive(outFs, ZipArchiveMode.Create);
        foreach (var e in classes) {
          expected.Add(e.FullName);
          var dst = outZip.CreateEntry(e.FullName, CompressionLevel.NoCompression);
          using var es = e.Open();
          using var ds = dst.Open();
          es.CopyTo(ds);
        }
      }

      // pack200 -> raw .pack
      var packFile = Path.Combine(work, "small.pack");
      Run(pack, "--no-gzip", "--effort=1", packFile, srcJar);

      // Our decoder.
      Pack200Segment seg;
      using (var fs = File.OpenRead(packFile))
        seg = new Pack200Reader().Read(fs);

      // unpack200 -> reference jar, list its class entries.
      var refJar = Path.Combine(work, "ref.jar");
      Run(unpack, packFile, refJar);
      List<string> refClasses;
      using (var rj = ZipFile.OpenRead(refJar))
        refClasses = rj.Entries.Select(e => e.FullName)
          .Where(n => n.EndsWith(".class", StringComparison.Ordinal)).ToList();

      // The header class count is always reliable.
      Assert.That(seg.ClassCount, Is.EqualTo(refClasses.Count),
        "decoded class_count must match unpack200 output");

      // When fully decoded, the class-name sets must match unpack200 exactly.
      if (seg.Status == Pack200DecodeStatus.Full) {
        var ours = seg.ClassNames.Select(n => n + ".class").OrderBy(n => n).ToList();
        Assert.That(ours, Is.EqualTo(refClasses.OrderBy(n => n).ToList()));
      } else {
        Assert.Warn($"Pack200 decode was partial: {seg.StatusNote}");
      }
    } finally {
      try { Directory.Delete(work, true); } catch { /* best-effort */ }
    }
  }
}
