#pragma warning disable CS1591

using System.Diagnostics;
using System.Text;
using FileSystem.Jffs2;

namespace Compression.Tests.Jffs2;

/// <summary>
/// Forward gate for the JFFS2 writer: a volume it builds is handed to the tools
/// that own the format, not back to our own reader.
/// </summary>
/// <remarks>
/// <para>Two validators, in rising order of authority:</para>
/// <list type="bullet">
///   <item><b><c>jffs2dump -c</c></b> (mtd-utils) walks the node log and
///   recomputes every checksum. It prints "Wrong hdr_crc" / "Wrong node_crc"
///   and carries on, so the verdict is its output, not its exit code — it exits
///   zero on a volume it has just declared broken.</item>
///   <item><b>The kernel's own jffs2 driver.</b> JFFS2 lives on MTD rather than
///   on a block device, so there is no loop mount for it; the volume goes into
///   an <c>mtdram</c> device inside the libguestfs appliance, which boots a real
///   kernel, and the files are read back through the driver.</item>
/// </list>
///
/// <para>Neither had ever been pointed at this writer. Both had something to
/// say: every node carried an ordinary CRC-32 where JFFS2 wants
/// <c>crc32(0, ...)</c> with no pre- or post-inversion, and every data node of a
/// file claimed to be version 1, so the kernel rebuilt any file past one page
/// out of a single fragment. The first fault made the volume unmountable
/// outright; the second silently returned the wrong bytes.</para>
/// </remarks>
[TestFixture]
[Category("ExternalConformance")]
public class Jffs2ExternalConformanceTests {

  /// <summary>Erase block the volume is built for, and the mtdram geometry to match.</summary>
  private const int EraseBlockSize = 64 * 1024;

  /// <summary>
  /// JFFS2 refuses a volume with fewer than five erase blocks, so the simulated
  /// flash is always larger than the image written into the front of it.
  /// </summary>
  private const int FlashKiB = 1024;

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_jffs2_conf_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── probes ────────────────────────────────────────────────────────────

  private sealed record Probe(string Path, byte[] Content);

  private static byte[] Bytes(int n, int seed) {
    var random = new Random(seed);
    var buffer = new byte[n];
    random.NextBytes(buffer);
    return buffer;
  }

  /// <summary>
  /// The shapes that have caught defects: an empty file, one that fits a single
  /// data node, ones that straddle the 4 KiB fragment boundary in both
  /// directions, a nested path, and a name that is neither short nor ASCII.
  /// </summary>
  private static Probe[] ProbeSet() => [
    new("empty.bin", []),
    new("hello.txt", Encoding.ASCII.GetBytes("hello world from jffs2")),
    new("page-1.bin", Bytes(4095, 11)),
    new("page.bin", Bytes(4096, 12)),
    new("page+1.bin", Bytes(4097, 13)),
    new("spans.bin", Bytes(20_000, 14)),
    new("sub/nested.txt", Encoding.ASCII.GetBytes("nested file data")),
    new("sub/deep/leaf.bin", Bytes(777, 15)),
    new("grüße-mit-einem-recht-langen-namen.txt", Encoding.UTF8.GetBytes("äöü ß € 漢字")),
  ];

  private string BuildOurImage() {
    var writer = new Jffs2Writer(EraseBlockSize);
    foreach (var probe in ProbeSet())
      writer.AddFile(probe.Path, probe.Content);
    var path = Path.Combine(this._tmpDir, "ours.jffs2");
    File.WriteAllBytes(path, writer.Build());
    return path;
  }

  // ── tool plumbing ─────────────────────────────────────────────────────

  private readonly record struct ToolResult(string StdOut, string StdErr, int ExitCode) {
    public string Combined => this.StdOut + "\n" + this.StdErr;
  }

  private static bool TryFromPath(string tool, out string fullPath) {
    fullPath = string.Empty;
    var pathEnv = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrEmpty(pathEnv)) return false;
    foreach (var dir in pathEnv.Split(Path.PathSeparator)) {
      if (string.IsNullOrWhiteSpace(dir)) continue;
      string candidate;
      try { candidate = Path.Combine(dir.Trim(), tool); } catch { continue; }
      if (File.Exists(candidate)) { fullPath = candidate; return true; }
    }
    return false;
  }

  private static string RequireTool(string tool, string package) {
    if (TryFromPath(tool, out var path)) return path;
    Assert.Ignore($"{tool} is not on PATH (it ships with {package}); nothing to check the volume against.");
    return string.Empty;
  }

  private static ToolResult Run(string tool, string arguments, string? stdin = null,
      int timeoutMs = 600_000) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = arguments,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      RedirectStandardInput = stdin != null,
      UseShellExecute = false,
      CreateNoWindow = true,
      // The probe set carries a non-ASCII name, and the tools speak UTF-8
      // whatever the console the test host happens to have.
      StandardOutputEncoding = new UTF8Encoding(false),
      StandardErrorEncoding = new UTF8Encoding(false),
    };
    try {
      using var process = Process.Start(psi)!;
      if (stdin != null) {
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
      }

      var stdout = process.StandardOutput.ReadToEndAsync();
      var stderr = process.StandardError.ReadToEndAsync();
      if (!process.WaitForExit(timeoutMs)) {
        try { process.Kill(true); } catch { /* best effort */ }
        return new ToolResult(string.Empty, $"{tool} did not finish within {timeoutMs} ms", -1);
      }

      return new ToolResult(stdout.Result, stderr.Result, process.ExitCode);
    } catch (Exception ex) {
      return new ToolResult(string.Empty, ex.Message, -1);
    }
  }

  // ── jffs2dump ─────────────────────────────────────────────────────────

  /// <summary>
  /// Lines <c>jffs2dump</c> prints for a checksum that does not recompute. It
  /// keeps walking afterwards and still exits zero, so the output is the verdict.
  /// </summary>
  private static string[] ChecksumComplaints(string dumpOutput)
    => dumpOutput
      .Split('\n')
      .Where(line => line.Contains("Wrong ", StringComparison.Ordinal))
      .Select(line => line.Trim())
      .ToArray();

  [Test]
  public void EveryNodeChecksumRecomputesUnderJffs2dump() {
    var tool = RequireTool("jffs2dump", "mtd-utils");
    var image = this.BuildOurImage();

    var dump = Run(tool, $"-c \"{image}\"");
    var complaints = ChecksumComplaints(dump.Combined);

    Assert.That(complaints, Is.Empty,
      "jffs2dump recomputed the checksums of a volume this writer built and disagreed:\n"
      + string.Join('\n', complaints));

    // The dump has to have found the volume at all — an empty walk would pass
    // the assertion above without checking anything.
    Assert.That(dump.Combined, Does.Contain("Dirent"),
      $"jffs2dump found no directory entries in the volume:\n{dump.Combined}");
  }

  [Test]
  public void Jffs2dumpRejectsATamperedNode() {
    var tool = RequireTool("jffs2dump", "mtd-utils");
    var image = this.BuildOurImage();

    // A twelve-byte cleanmarker opens the volume and the root directory's inode
    // node follows it, so that node's ino field sits at 12 + 12. It is inside
    // what node_crc covers and outside what hdr_crc covers, so one flipped bit
    // there has to surface as a node_crc complaint — otherwise the check above
    // is rubber-stamping whatever it is handed.
    var bytes = File.ReadAllBytes(image);
    bytes[24] ^= 0x40;
    var tampered = Path.Combine(this._tmpDir, "tampered.jffs2");
    File.WriteAllBytes(tampered, bytes);

    var dump = Run(tool, $"-c \"{tampered}\"");
    var complaints = ChecksumComplaints(dump.Combined);
    Assert.That(complaints, Is.Not.Empty,
      $"jffs2dump accepted a node whose payload was altered under its checksum:\n{dump.Combined}");
    Assert.That(complaints.Any(c => c.Contains("node_crc", StringComparison.Ordinal)), Is.True,
      $"the altered byte should have failed node_crc, not something else:\n{dump.Combined}");
  }

  [Test]
  public void Jffs2readerListsEveryEntryOfTheRootDirectory() {
    var tool = RequireTool("jffs2reader", "mtd-utils");
    var image = this.BuildOurImage();

    var listing = Run(tool, $"\"{image}\" -d /").Combined;

    // jffs2reader resolves a directory by picking the newest dirent it holds,
    // so entries that share a version reduce to one. The names at the top of
    // the tree are what the listing must account for.
    var roots = ProbeSet()
      .Select(p => p.Path)
      .Where(p => !p.Contains('/', StringComparison.Ordinal))
      .ToArray();

    var missing = roots.Where(name => !listing.Contains(name, StringComparison.Ordinal)).ToArray();
    Assert.That(missing, Is.Empty,
      $"jffs2reader did not list {string.Join(", ", missing)} in the root directory:\n{listing}");
  }

  // ── the kernel driver, through the libguestfs appliance ───────────────

  /// <summary>
  /// Puts the volume into an <c>mtdram</c> device inside the appliance, mounts it
  /// with the kernel's jffs2 driver and prints one <c>md5sum</c> line per file.
  /// </summary>
  private static string ApplianceScript(int flashKiB, int eraseKiB) {
    var shell = string.Join("; ", [
      $"modprobe mtdram total_size={flashKiB} erase_size={eraseKiB}",
      "modprobe mtdblock",
      "modprobe jffs2",
      "dd if=/dev/sda of=/dev/mtdblock0 bs=65536 2>/dev/null",
      "mkdir -p /probe",
      "mount -t jffs2 /dev/mtdblock0 /probe && echo MOUNT-OK",
      "cd /probe && find . -type f -exec md5sum {} +",
    ]);
    return "run\ndebug sh \"" + shell + "\"\n";
  }

  [Test]
  public void KernelDriverMountsTheVolumeAndReadsEveryFileBack() {
    var tool = RequireTool("guestfish", "libguestfs");
    var image = this.BuildOurImage();

    var result = Run(tool, $"--ro -a \"{image}\"", ApplianceScript(FlashKiB, EraseBlockSize / 1024));
    var output = result.Combined;

    Assert.That(output, Does.Contain("MOUNT-OK"),
      "the kernel's jffs2 driver would not mount a volume this writer built:\n" + output);

    // md5sum prints "<digest>  ./<path>"; the paths are what find walked, so the
    // set of them is the directory tree the driver saw.
    var seen = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var line in output.Split('\n')) {
      var parts = line.Trim().Split("  ", 2);
      if (parts.Length != 2 || parts[0].Length != 32) continue;
      if (!parts[1].StartsWith("./", StringComparison.Ordinal)) continue;
      seen[parts[1][2..]] = parts[0];
    }

    var probes = ProbeSet();
    Assert.That(seen.Keys, Is.EquivalentTo(probes.Select(p => p.Path)),
      "the file set the jffs2 driver listed is not the one that was written:\n" + output);

    foreach (var probe in probes) {
      var expected = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(probe.Content));
      Assert.That(seen[probe.Path], Is.EqualTo(expected),
        $"the jffs2 driver read '{probe.Path}' back as different bytes than were written.");
    }
  }

  // ── reverse direction ─────────────────────────────────────────────────

  [Test]
  public void OurReaderMatchesAVolumeMkfsJffs2Built() {
    var tool = RequireTool("mkfs.jffs2", "mtd-utils");

    var source = Path.Combine(this._tmpDir, "src");
    var probes = ProbeSet();
    foreach (var probe in probes) {
      var target = Path.Combine(source, probe.Path);
      Directory.CreateDirectory(Path.GetDirectoryName(target)!);
      File.WriteAllBytes(target, probe.Content);
    }

    var image = Path.Combine(this._tmpDir, "native.jffs2");
    var made = Run(tool, $"-r \"{source}\" -o \"{image}\" -e 0x{EraseBlockSize:x} -n");
    if (made.ExitCode != 0)
      Assert.Ignore($"mkfs.jffs2 would not build a probe volume here (exit {made.ExitCode}): {made.Combined}");

    var reader = new Jffs2FileReader(File.ReadAllBytes(image));
    var files = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, StringComparer.Ordinal);

    foreach (var probe in probes) {
      Assert.That(files.ContainsKey(probe.Path), Is.True,
        $"'{probe.Path}' is on the volume mkfs.jffs2 wrote and our reader did not find it.");
      Assert.That(reader.Extract(files[probe.Path]), Is.EqualTo(probe.Content),
        $"'{probe.Path}' does not read back as the bytes mkfs.jffs2 was given.");
    }
  }
}
