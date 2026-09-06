using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Compression.Tests.ReiserFs;

/// <summary>
/// Spec-conformance check of the ReiserFS v3.6 writer against the authoritative
/// third-party tool <c>reiserfsck</c> (reiserfsprogs). A representative image —
/// root files, a nested directory tree, and a directory large enough to force a
/// multi-leaf S+tree with an internal block — is written to disk and handed to
/// <c>reiserfsck --check</c>. The filesystem must be reported clean ("No
/// corruptions found"); any superblock, S+tree, item-header, stat-data,
/// directory-item, bitmap or key-ordering defect surfaces here.
///
/// The test skips cleanly when <c>reiserfsck</c> is not installed or when not
/// running on Linux, so it never blocks CI on platforms that lack the tool.
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class ReiserFsExternalConformanceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_reiserfsck_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("RoundTrip")]
  public void RepresentativeImage_PassesReiserfsckCheck() {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
      Assert.Ignore("reiserfsck conformance is a Linux-only check");
    if (!HasCommand("reiserfsck"))
      Assert.Ignore("reiserfsck (reiserfsprogs) not installed");

    // Build an image exercising every structural path the writer can take:
    //   - several files directly in the root directory
    //   - a nested directory tree (docs/, docs/api/)
    //   - a small file and a larger one (multi-hundred-byte DIRECT tail)
    //   - a directory with ~1000 entries, which overflows a single 4 KiB leaf
    //     and forces a real S+tree (multiple leaves + an internal block).
    var writer = new FileSystem.ReiserFs.ReiserFsWriter();
    writer.AddFile("readme.txt", "root file content"u8.ToArray());
    writer.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    writer.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());

    var larger = new byte[3000];
    for (var i = 0; i < larger.Length; i++)
      larger[i] = (byte)(i % 251);
    writer.AddFile("docs/big.bin", larger);

    for (var i = 0; i < 1000; i++)
      writer.AddFile($"many/file{i:D4}", Encoding.ASCII.GetBytes($"content-{i:D4}"));

    var imagePath = Path.Combine(_tmpDir, "ours.reiserfs");
    using (var fs = File.Create(imagePath))
      writer.WriteTo(fs);

    // reiserfsck --check is read-only but prompts for confirmation, requiring the
    // literal word "Yes" on stdin before it proceeds.
    var result = RunTool("reiserfsck", $"--check \"{imagePath}\"", stdin: "Yes\n");

    var combined = result.StdOut + "\n" + result.StdErr;
    Assert.Multiple(() => {
      Assert.That(combined, Does.Contain("No corruptions found"),
        $"reiserfsck did not report the filesystem clean.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
      Assert.That(combined, Does.Not.Contain("fix-fixable"),
        "reiserfsck reported fixable corruptions");
      Assert.That(combined, Does.Not.Contain("vpf-"),
        "reiserfsck reported a structural (vpf-) defect");
      Assert.That(combined, Does.Not.Contain("bad_path"),
        "reiserfsck reported an S+tree internal/leaf consistency defect");
    });
  }

  /// <summary>
  /// A volume deep enough to need two internal levels over the leaves must pass
  /// reiserfsck as well. One internal node indexes 170 children at a 4 KiB
  /// blocksize, so past roughly 4 800 small files the writer stacks a second
  /// level and <c>s_tree_height</c> reaches 4 — the point at which every
  /// internal node used to claim level 2 and the tool answered "block 8387: The
  /// level of the node (2) is not correct, (3) expected".
  /// </summary>
  [Test, Category("RoundTrip")]
  public void DeepTreeImage_PassesReiserfsckCheck() {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
      Assert.Ignore("reiserfsck conformance is a Linux-only check");
    if (!HasCommand("reiserfsck"))
      Assert.Ignore("reiserfsck (reiserfsprogs) not installed");

    var writer = new FileSystem.ReiserFs.ReiserFsWriter();
    // Nested directories, long names and bodies past the DIRECT cutoff, so item
    // packing — which drives how many leaves the tree needs — varies across the
    // volume rather than repeating one shape.
    var rng = new Random(1234);
    for (var i = 0; i < 6000; i++) {
      var name = (i % 7) switch {
        0 => $"a/f{i:D6}.bin",
        1 => $"a/b/f{i:D6}.bin",
        2 => $"a/b/c/{new string('x', 120)}_{i:D6}.bin",
        3 => $"top{i:D6}.bin",
        4 => $"d{i % 37:D3}/f{i:D6}.bin",
        5 => $"a/b/c/d/e/f{i:D6}.bin",
        _ => $"big/f{i:D6}.bin",
      };
      byte[] payload;
      if (i % 70 == 6) {
        payload = new byte[9000 + i % 5000];   // multi-block INDIRECT body
        rng.NextBytes(payload);
      } else if (i % 21 == 3) {
        payload = new byte[1100 + i % 2900];   // just past the DIRECT cutoff
        rng.NextBytes(payload);
      } else {
        payload = Encoding.ASCII.GetBytes($"c-{i:D6}");
      }
      writer.AddFile(name, payload);
    }

    var imagePath = Path.Combine(_tmpDir, "deep.reiserfs");
    using (var fs = File.Create(imagePath))
      writer.WriteTo(fs);

    // The volume has to actually be deep, or the check proves nothing.
    using (var img = File.OpenRead(imagePath)) {
      var sb = new byte[128];
      img.Position = 65536;
      img.ReadExactly(sb);
      Assert.That(BitConverter.ToUInt16(sb, 68), Is.GreaterThanOrEqualTo(4),
        "the generated volume did not reach s_tree_height 4");
    }

    var result = RunTool("reiserfsck", $"--check \"{imagePath}\"", stdin: "Yes\n");
    var combined = result.StdOut + "\n" + result.StdErr;
    Assert.Multiple(() => {
      Assert.That(result.ExitCode, Is.EqualTo(0),
        $"reiserfsck exited {result.ExitCode}.\nstdout:\n{Tail(result.StdOut)}\nstderr:\n{Tail(result.StdErr)}");
      Assert.That(combined, Does.Contain("No corruptions found"),
        $"reiserfsck did not report the filesystem clean.\nstdout:\n{Tail(result.StdOut)}");
      Assert.That(combined, Does.Not.Contain("is not correct"),
        "reiserfsck rejected a node's level");
      Assert.That(combined, Does.Not.Contain("vpf-"),
        "reiserfsck reported a structural (vpf-) defect");
    });
  }

  /// <summary>Last few kilobytes of a tool's output — reiserfsck's progress spinner floods the rest.</summary>
  private static string Tail(string text)
    => text.Length <= 4000 ? text : text[^4000..];

  // ── Process helpers (mirrors OsIntegrationTests) ───────────────────────

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static bool HasCommand(string name) {
    try {
      var result = RunTool("/bin/sh", $"-c \"which {name} 2>/dev/null\"");
      return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
    } catch {
      return false;
    }
  }

  private static ToolResult RunTool(string tool, string args, string? stdin = null, int timeoutMs = 120_000) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      RedirectStandardInput = stdin != null,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start {tool}");
    if (stdin != null) {
      proc.StandardInput.Write(stdin);
      proc.StandardInput.Close();
    }
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(timeoutMs)) {
      try { proc.Kill(); } catch { /* best effort */ }
    }
    return new ToolResult(stdout, stderr, proc.ExitCode);
  }
}
