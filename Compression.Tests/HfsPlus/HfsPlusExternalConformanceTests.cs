using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// Validates that images produced by <see cref="HfsPlusWriter"/> are accepted as
/// structurally sound by the reference HFS+ checker (<c>fsck.hfsplus</c> from
/// hfsprogs). The checker walks the volume header, the catalog and extents
/// B-trees (node descriptors, sibling links, key ordering under the declared
/// compare type, the node-allocation bitmap), the catalog hierarchy and the
/// volume allocation bitmap, reporting any deviation from Apple TN1150. A clean
/// run ("appears to be OK", exit 0) means the writer's on-disk structures match
/// what an independent implementation expects.
/// <para>
/// The tests run only on Linux when <c>fsck.hfsplus</c> is installed; otherwise
/// they are ignored so the suite stays green on machines without hfsprogs.
/// </para>
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class HfsPlusExternalConformanceTests {

  private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

  private static bool HasCommand(string name) {
    try {
      var result = RunTool("/bin/sh", $"-c \"which {name} 2>/dev/null\"");
      return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
    } catch {
      return false;
    }
  }

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_hfsfsck_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  [Test]
  public void RepresentativeImage_PassesFsckCleanly() {
    if (!IsLinux) Assert.Ignore("fsck.hfsplus conformance check is Linux-only");
    if (!HasCommand("fsck.hfsplus")) Assert.Ignore("fsck.hfsplus (hfsprogs) not installed");

    var writer = new HfsPlusWriter();

    // Root-level files of differing sizes.
    writer.AddFile("readme.txt", "root file"u8.ToArray());
    writer.AddFile("small.txt", Encoding.ASCII.GetBytes("hello world"));

    // A nested directory tree.
    writer.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    writer.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());

    // A larger, multi-block file.
    var large = new byte[20_000];
    new Random(7).NextBytes(large);
    writer.AddFile("data/random.bin", large);

    // A directory holding ~1000 files: forces the catalog to spill across many
    // leaf nodes joined by an index level, exercising B-tree node descriptors,
    // sibling links and the node-allocation bitmap.
    for (var i = 0; i < 1000; i++)
      writer.AddFile($"bulk/file{i:D4}.dat", Encoding.ASCII.GetBytes($"content-{i}"));

    AssertFsckClean(writer.Build());
  }

  [Test]
  public void MultiLevelCatalogIndex_PassesFsckCleanly() {
    if (!IsLinux) Assert.Ignore("fsck.hfsplus conformance check is Linux-only");
    if (!HasCommand("fsck.hfsplus")) Assert.Ignore("fsck.hfsplus (hfsprogs) not installed");

    // Enough records that a single index node cannot point at every leaf, so
    // the catalog grows a second index level. Every index node at one level
    // must be chained to its siblings via fLink/bLink, which fsck verifies.
    var writer = new HfsPlusWriter();
    for (var i = 0; i < 5000; i++)
      writer.AddFile($"bulk/file{i:D5}.dat", Encoding.ASCII.GetBytes($"content-{i}"));

    AssertFsckClean(writer.Build());
  }

  [Test]
  public void NonAsciiAndMixedCaseNames_PassesFsckCleanly() {
    if (!IsLinux) Assert.Ignore("fsck.hfsplus conformance check is Linux-only");
    if (!HasCommand("fsck.hfsplus")) Assert.Ignore("fsck.hfsplus (hfsprogs) not installed");

    // Names whose case-folding order differs from a raw UTF-16 byte order
    // ('Z' < 'a' as bytes, but apple < Zebra when case-folded) plus accented
    // Latin and CJK names. The catalog must be sorted by the declared
    // case-folding compare or fsck reports "Keys out of order".
    var writer = new HfsPlusWriter();
    writer.AddFile("Zebra.txt", "z"u8.ToArray());
    writer.AddFile("apple.txt", "a"u8.ToArray());
    writer.AddFile("café/naïve.txt", "u"u8.ToArray());
    writer.AddFile("日本語/ファイル.txt", "j"u8.ToArray());

    AssertFsckClean(writer.Build());
  }

  private void AssertFsckClean(byte[] image) {
    var imagePath = Path.Combine(_tmpDir, "volume.hfsplus");
    File.WriteAllBytes(imagePath, image);

    var result = RunTool("fsck.hfsplus", $"-f -n \"{imagePath}\"");
    var combined = result.StdOut + result.StdErr;

    Assert.That(combined, Does.Contain("appears to be OK"),
      $"fsck.hfsplus did not report the volume clean.\n{combined}");
    Assert.That(combined, Does.Not.Contain("found corrupt"),
      $"fsck.hfsplus reported corruption.\n{combined}");
  }

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static ToolResult RunTool(string tool, string args, int timeoutMs = 120_000) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start {tool}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(timeoutMs)) {
      try { proc.Kill(); } catch { /* best effort */ }
    }
    return new ToolResult(stdout, stderr, proc.ExitCode);
  }
}
