#pragma warning disable CA1416 // Platform compatibility — mkfs.ext4/debugfs/e2fsck are Linux-only and guarded at runtime.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Compression.Tests.Ext;

/// <summary>
/// Reference-checker gate for <see cref="FileSystem.Ext.ExtBlockMover"/> on
/// genuine <c>mkfs.ext4</c> images that use the extent tree (not the legacy
/// block-pointer map the writer emits). A real extent file is relocated through
/// the public <see cref="Compression.Registry.IFilesystemBlockMover"/> surface,
/// then handed to <c>e2fsck -fn</c> and read back with <c>debugfs</c>.
/// <para>
/// Regression guard for the extent-patch defect where <c>ee_start_hi</c> was
/// written as <c>(ushort)(newStart &gt;&gt; 32)</c> with <c>newStart</c> a
/// 32-bit value: C# masks the shift to 31, so <c>&gt;&gt; 32</c> is a no-op and
/// the high half received the LOW 16 bits of the new physical block instead of
/// zero — producing a wild physical start (e.g. <c>0x0255_00000256</c>) that
/// fsck rejects and the kernel cannot read.
/// </para>
/// Skips cleanly where the e2fsprogs tools are unavailable (non-Linux hosts).
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class ExtBlockMoverFsckTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_ext_mover_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("Conformance")]
  public void RelocateExtentFile_StaysE2fsckCleanAndReadable() {
    if (!IsLinux) Assert.Ignore("mkfs.ext4/debugfs/e2fsck run on Linux only.");
    foreach (var tool in new[] { "mkfs.ext4", "debugfs", "e2fsck", "dumpe2fs" })
      if (!HasCommand(tool)) Assert.Ignore($"{tool} (e2fsprogs) not installed.");

    const int blockSize = 4096;
    var imgPath = Path.Combine(_tmpDir, "ext4_mover.img");
    var payloadPath = Path.Combine(_tmpDir, "payload.bin");

    // Deterministic multi-block payload so the read-back comparison is stable.
    var payload = new byte[20_000];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + 7);
    File.WriteAllBytes(payloadPath, payload);

    // Build a real ext4 image with the extent tree. Disable metadata_csum/64bit/
    // journal to match what ExtBlockMover maintains (it patches the extent + the
    // block bitmap; it does not recompute bitmap checksums).
    using (var fs = File.Create(imgPath)) fs.SetLength(8L * 1024 * 1024);
    var mk = RunTool("mkfs.ext4", $"-F -q -b {blockSize} -O ^metadata_csum,^64bit,^has_journal \"{imgPath}\"");
    if (mk.ExitCode != 0) Assert.Ignore($"mkfs.ext4 failed to build the oracle image:\n{mk.StdErr}");

    var wr = RunTool("debugfs", $"-w -R \"write {payloadPath} data.bin\" \"{imgPath}\"");
    Assert.That(wr.ExitCode, Is.Zero, $"debugfs write failed: {wr.StdErr}");

    // Confirm the file really uses an extent (sanity — the bug only bites extents).
    var ext = RunTool("debugfs", $"-R \"dump_extents data.bin\" \"{imgPath}\"");
    Assert.That(ext.StdOut, Does.Not.Contain("No extents"),
      "test precondition: data.bin must be extent-mapped");

    // Physical blocks of the file (e.g. "8 9 10 11 12").
    var blocks = RunTool("debugfs", $"-R \"blocks data.bin\" \"{imgPath}\"")
      .StdOut.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
      .Select(uint.Parse).ToArray();
    Assert.That(blocks, Is.Not.Empty, "data.bin has no data blocks");
    var oldFirst = blocks[0];
    var count = blocks.Length;
    // The block list must be contiguous for a single-extent relocation.
    for (var i = 1; i < count; i++)
      Assert.That(blocks[i], Is.EqualTo(oldFirst + (uint)i), "data.bin must be a single contiguous extent");

    // Pick a contiguous free run of `count` blocks via debugfs "find free blocks".
    var freeOut = RunTool("debugfs", $"-R \"ffb {count}\" \"{imgPath}\"").StdOut;
    var free = Regex.Match(freeOut, @"Free blocks found:\s*(.+)").Groups[1].Value
      .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
      .Select(uint.Parse).ToArray();
    Assert.That(free.Length, Is.EqualTo(count), $"ffb did not return {count} free blocks: {freeOut}");
    var dst = free[0];
    for (var i = 1; i < count; i++)
      Assert.That(free[i], Is.EqualTo(dst + (uint)i), "free destination run must be contiguous");
    Assert.That(dst + (uint)count <= oldFirst || dst >= oldFirst + (uint)count, Is.True,
      "free destination run must not overlap the source");

    var oldOff = (long)oldFirst * blockSize;
    var dstOff = (long)dst * blockSize;
    var length = (long)count * blockSize;

    // Relocate through the public block-mover surface (what defrag/shrink drive).
    var d = new FileSystem.Ext.ExtFormatDescriptor();
    using (var fs = new FileStream(imgPath, FileMode.Open, FileAccess.ReadWrite)) {
      d.MoveExtent(fs, oldOff, dstOff, length, zeroSource: true);
      d.UpdateAllocationAfterMove(fs, "data.bin", oldOff, dstOff, length);
    }

    // 1) The reference checker must accept the relocated image.
    var fsck = RunTool("e2fsck", $"-fn \"{imgPath}\"");
    var combined = fsck.StdOut + "\n" + fsck.StdErr;
    Assert.That(fsck.ExitCode, Is.Zero,
      $"e2fsck rejected the relocated image (exit {fsck.ExitCode}):\n{combined}");
    foreach (var marker in new[] { "FIXED", "Repair", "WARNING", "inconsistent", "corrupt", "invalid", "illegal" })
      Assert.That(combined, Does.Not.Contain(marker).IgnoreCase,
        $"e2fsck flagged a problem ('{marker}'):\n{combined}");

    // 2) The file now lives at the new blocks and reads back byte-identical.
    var movedBlocks = RunTool("debugfs", $"-R \"blocks data.bin\" \"{imgPath}\"")
      .StdOut.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
      .Select(uint.Parse).ToArray();
    Assert.That(movedBlocks[0], Is.EqualTo(dst), "the extent must now point at the destination block");

    var outPath = Path.Combine(_tmpDir, "out.bin");
    var dump = RunTool("debugfs", $"-R \"dump data.bin {outPath}\" \"{imgPath}\"");
    Assert.That(dump.ExitCode, Is.Zero, $"debugfs dump failed: {dump.StdErr}");
    Assert.That(File.ReadAllBytes(outPath), Is.EqualTo(payload),
      "relocated file content must be byte-identical to the original");
  }

  // ── process plumbing (mirrors ExtExternalConformanceTests) ───────────────

  private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

  private static bool HasCommand(string name) {
    try {
      var psi = new ProcessStartInfo {
        FileName = "/bin/sh", Arguments = $"-c \"command -v {name}\"",
        RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
      };
      using var p = Process.Start(psi)!;
      var o = p.StandardOutput.ReadToEnd();
      p.WaitForExit(5_000);
      return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(o);
    } catch { return false; }
  }

  private static (string StdOut, string StdErr, int ExitCode) RunTool(string tool, string args, int timeoutMs = 60_000) {
    var psi = new ProcessStartInfo {
      FileName = tool, Arguments = args,
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)!;
    var so = proc.StandardOutput.ReadToEnd();
    var se = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(timeoutMs)) { try { proc.Kill(true); } catch { /* best effort */ } }
    return (so, se, proc.ExitCode);
  }
}
