#pragma warning disable CA1416 // mkfs.ext4 / debugfs / e2fsck are Linux-only and runtime-guarded.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FileSystem.Ext;

namespace Compression.Tests.Ext;

/// <summary>
/// Oracle-backed proof that <see cref="ExtInPlaceShrinker.ShrinkToBlocks"/> performs a
/// genuine in-place ext2/3/4 shrink <b>with block relocation</b>: real
/// <c>mkfs.ext4 -O ^64bit</c> images (tested both with and without
/// <c>metadata_csum</c>) are populated via <c>debugfs write</c>, the low blocks are
/// freed so the file data sits high in the volume, then the image is shrunk to a target
/// below that data — forcing whole-run relocation. The result is handed to the
/// e2fsprogs oracle: <c>e2fsck -fn</c> must exit 0 with no problem markers, every file
/// must <c>debugfs dump</c> byte-identical, the image must be smaller, and
/// <see cref="ExtInPlaceShrinker.ShrinkResult.BlocksRelocated"/> must be &gt; 0.
/// <para>
/// Refusal cases (a multi-group image; an indirect-block file) assert the shrinker
/// throws <see cref="NotSupportedException"/> rather than emitting a corrupt image.
/// </para>
/// Skips cleanly where the e2fsprogs tools are unavailable (non-Linux hosts).
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class ExtInPlaceShrinkRelocateFsckTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_ext_shrink_reloc_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  // ── Relocation shrink, several small files + a nested dir + one multi-block file ──

  [TestCase(false), Category("Conformance")] // without metadata_csum
  [TestCase(true)]                            // with metadata_csum
  public void RelocateShrink_SeveralFiles_NestedDir_MultiBlock_StaysCleanAndIdentical(bool csum) {
    RequireTools("mkfs.ext4", "debugfs", "e2fsck", "dumpe2fs");

    const int blockSize = 1024;
    var img = Path.Combine(_tmpDir, "reloc.img");
    Run("dd", $"if=/dev/zero of={img} bs=1024 count=8000 status=none");
    var csumOpt = csum ? "metadata_csum" : "^metadata_csum";
    // Single block group (8000 < 8192 blocks/group); no 64bit, no journal, no resize
    // inode (keeps the volume simple + single-group).
    var mk = Run("mkfs.ext4", $"-F -q -b {blockSize} -O {csumOpt},^64bit,^has_journal,^resize_inode {img}");
    if (mk.Exit != 0) Assert.Ignore($"mkfs.ext4 failed: {mk.Err}");
    Assert.That(GroupCount(img), Is.EqualTo(1), "test needs a single-group image");

    // Payloads. The "big" file is multi-block and must move as a unit.
    var big = MakeData(60_000);
    var n1 = MakeData(1_400);
    var n2 = MakeData(1_400);
    var nested = MakeData(2_500);
    WriteHostFile("big.bin", big);
    WriteHostFile("n1.bin", n1);
    WriteHostFile("n2.bin", n2);
    WriteHostFile("nested.bin", nested);

    // Occupy the low region with a filler so the real files are forced HIGH, then free
    // it — leaving the file data above a large block of free low space to relocate into.
    var filler = MakeData(4_000_000);
    WriteHostFile("filler.bin", filler);
    DebugfsW(img, $"write {Host("filler.bin")} filler.bin");

    DebugfsW(img, $"write {Host("big.bin")} big.bin");
    DebugfsW(img, $"write {Host("n1.bin")} n1.bin");
    DebugfsW(img, $"write {Host("n2.bin")} n2.bin");
    DebugfsW(img, "mkdir /sub");
    DebugfsW(img, $"write {Host("nested.bin")} /sub/nested.bin");

    DebugfsW(img, "rm /filler.bin");
    Run("e2fsck", $"-fy {img}"); // settle bitmaps after the deletion

    // Lowest data block of any surviving file → the boundary must sit below it so every
    // file's data is above the boundary and must relocate.
    var lowestFile = new[] { "big.bin", "n1.bin", "n2.bin", "/sub/nested.bin" }
      .Select(f => FirstBlock(img, f)).Min();
    Assert.That(lowestFile, Is.GreaterThan(1000u),
      "files should sit high after freeing the filler; otherwise the test wouldn't force relocation");
    var target = lowestFile; // everything at/above lowestFile must move below it

    long originalLen, newLen, relocated;
    using (var fs = new FileStream(img, FileMode.Open, FileAccess.ReadWrite)) {
      originalLen = fs.Length;
      var result = ExtInPlaceShrinker.ShrinkToBlocks(fs, target);
      newLen = result.NewSize;
      relocated = result.BlocksRelocated;
      Assert.That(result.WasReduced, Is.True, "image must actually shrink");
      Assert.That(result.BlocksRelocated, Is.GreaterThan(0), "this target forces relocation");
      Assert.That(result.BytesRelocated, Is.EqualTo(result.BlocksRelocated * blockSize));
    }
    Assert.That(new FileInfo(img).Length, Is.EqualTo(newLen));
    Assert.That(newLen, Is.LessThan(originalLen));
    TestContext.Out.WriteLine($"csum={csum}: target={target} blocks, relocated={relocated} blocks, {originalLen}->{newLen} bytes");

    // 1) Oracle: e2fsck -fn clean.
    AssertE2fsckClean(img, $"reloc csum={csum}");

    // 2) Oracle: every file dumps byte-identical.
    AssertDumpEquals(img, "big.bin", big);
    AssertDumpEquals(img, "n1.bin", n1);
    AssertDumpEquals(img, "n2.bin", n2);
    AssertDumpEquals(img, "/sub/nested.bin", nested);
  }

  // ── ShrinkToFit never relocates (boundary one past the highest in-use block) ─────

  [Test, Category("Conformance")]
  public void ShrinkToFit_OnRealImage_TrimsTrailingFree_NoRelocation() {
    RequireTools("mkfs.ext4", "debugfs", "e2fsck");
    const int blockSize = 1024;
    var img = Path.Combine(_tmpDir, "fit.img");
    Run("dd", $"if=/dev/zero of={img} bs=1024 count=8000 status=none");
    var mk = Run("mkfs.ext4", $"-F -q -b {blockSize} -O metadata_csum,^64bit,^has_journal,^resize_inode {img}");
    if (mk.Exit != 0) Assert.Ignore($"mkfs.ext4 failed: {mk.Err}");

    var payload = MakeData(40_000);
    WriteHostFile("p.bin", payload);
    DebugfsW(img, $"write {Host("p.bin")} p.bin");
    Run("e2fsck", $"-fy {img}");

    long newLen;
    using (var fs = new FileStream(img, FileMode.Open, FileAccess.ReadWrite)) {
      var result = ExtInPlaceShrinker.ShrinkToFit(fs);
      Assert.That(result.WasReduced, Is.True);
      Assert.That(result.BlocksRelocated, Is.Zero, "auto-fit never relocates");
      newLen = result.NewSize;
    }
    Assert.That(new FileInfo(img).Length, Is.EqualTo(newLen));
    AssertE2fsckClean(img, "fit");
    AssertDumpEquals(img, "p.bin", payload);
  }

  // ── Refusal: multi-group image must throw (mover assumes group 0) ────────────────

  [Test, Category("EdgeCase")]
  public void RelocateShrink_RefusesMultiGroupImage() {
    RequireTools("mkfs.ext4", "debugfs", "dumpe2fs");
    const int blockSize = 1024;
    var img = Path.Combine(_tmpDir, "multigroup.img");
    // 20000 blocks @ 8192/group → 3 groups (last group starts at block 16384).
    Run("dd", $"if=/dev/zero of={img} bs=1024 count=20000 status=none");
    var mk = Run("mkfs.ext4", $"-F -q -b {blockSize} -O metadata_csum,^64bit,^has_journal {img}");
    if (mk.Exit != 0) Assert.Ignore($"mkfs.ext4 failed: {mk.Err}");
    Assert.That(GroupCount(img), Is.GreaterThan(1), "test needs a multi-group image");

    // Push a file into the LAST block group via a large filler, then free the filler.
    // This makes the target land inside the last group (past the block-group-drop guard
    // and the metadata floor) so the relocation path is genuinely entered — and must be
    // refused because the inode lookup assumes group 0.
    var filler = MakeData(17_000_000);
    WriteHostFile("filler.bin", filler);
    DebugfsW(img, $"write {Host("filler.bin")} filler.bin");
    var payload = MakeData(60_000);
    WriteHostFile("hi.bin", payload);
    DebugfsW(img, $"write {Host("hi.bin")} hi.bin");
    DebugfsW(img, "rm /filler.bin");
    Run("e2fsck", $"-fy {img}");

    var hiFirst = FirstBlock(img, "hi.bin");
    Assert.That(hiFirst, Is.GreaterThan(16384u), "hi.bin must land in the last block group");

    using var fs = new FileStream(img, FileMode.Open, FileAccess.ReadWrite);
    // Target below the high file's data → relocation is attempted; multi-group must refuse.
    Assert.Throws<NotSupportedException>(() => ExtInPlaceShrinker.ShrinkToBlocks(fs, hiFirst));
  }

  // ── Refusal: an indirect-block file above the boundary must throw ────────────────

  [Test, Category("EdgeCase")]
  public void RelocateShrink_RefusesIndirectBlockFile() {
    RequireTools("mkfs.ext2", "debugfs", "e2fsck", "dumpe2fs");
    const int blockSize = 1024;
    var img = Path.Combine(_tmpDir, "indirect.img");
    Run("dd", $"if=/dev/zero of={img} bs=1024 count=8000 status=none");
    // ext2 with ^extent → files use the legacy direct/indirect block map. A file > 12
    // blocks therefore needs an indirect block, which the mover cannot relocate.
    var mk = Run("mkfs.ext2", $"-F -q -b {blockSize} -O ^extent,^64bit,^resize_inode,^metadata_csum {img}");
    if (mk.Exit != 0) Assert.Ignore($"mkfs.ext2 failed: {mk.Err}");
    Assert.That(GroupCount(img), Is.EqualTo(1), "test needs a single-group image");

    // Force the file high (so it would otherwise be a relocation candidate) and make it
    // > 12 blocks so it uses an indirect block.
    var filler = MakeData(4_000_000);
    WriteHostFile("filler.bin", filler);
    DebugfsW(img, $"write {Host("filler.bin")} filler.bin");
    var big = MakeData(30_000); // ~30 blocks > 12 → indirect
    WriteHostFile("ind.bin", big);
    DebugfsW(img, "write " + Host("ind.bin") + " ind.bin");
    DebugfsW(img, "rm /filler.bin");
    Run("e2fsck", $"-fy {img}");

    var first = FirstBlock(img, "ind.bin");
    Assert.That(first, Is.GreaterThan(1000u), "indirect file must sit high to be a relocation candidate");

    using var fs = new FileStream(img, FileMode.Open, FileAccess.ReadWrite);
    Assert.Throws<NotSupportedException>(() => ExtInPlaceShrinker.ShrinkToBlocks(fs, first));
  }

  // ── Helpers ──────────────────────────────────────────────────────────────────────

  private static byte[] MakeData(int n) { var d = new byte[n]; for (var i = 0; i < n; i++) d[i] = (byte)(i * 31 + 7); return d; }

  private string Host(string name) => Path.Combine(_tmpDir, name.Replace('/', '_'));
  private void WriteHostFile(string name, byte[] data) => File.WriteAllBytes(Host(name), data);

  private void DebugfsW(string img, string cmd) {
    var r = Run("debugfs", $"-w -R \"{cmd}\" {img}");
    Assert.That(r.Exit, Is.Zero, $"debugfs '{cmd}' failed: {r.Err}");
  }

  private uint FirstBlock(string img, string file) {
    // The "blocks" command lists every physical data block of the file (extent-mapped or
    // block-mapped); the smallest is the file's lowest physical block.
    var blocks = Run("debugfs", $"-R \"blocks {file}\" {img}").Out
      .Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
      .Where(t => uint.TryParse(t, out _)).Select(uint.Parse).ToArray();
    Assert.That(blocks, Is.Not.Empty, $"{file} reported no data blocks");
    return blocks.Min();
  }

  private uint GroupCount(string img) {
    var bc = ReadNum(img, "Block count:");
    var bpg = ReadNum(img, "Blocks per group:");
    return (uint)((bc + bpg - 1) / bpg);
  }

  private long ReadNum(string img, string label) {
    var line = Run("dumpe2fs", $"-h {img}").Out.Split('\n').FirstOrDefault(l => l.Contains(label))
               ?? Run("dumpe2fs", img).Out.Split('\n').First(l => l.Contains(label));
    return long.Parse(Regex.Match(line, @"\d+").Value);
  }

  private void AssertDumpEquals(string img, string file, byte[] expected) {
    var outFile = Path.Combine(_tmpDir, $"dump_{Guid.NewGuid():N}.bin");
    var d = Run("debugfs", $"-R \"dump {file} {outFile}\" {img}");
    Assert.That(d.Exit, Is.Zero, $"debugfs dump {file} failed: {d.Err}");
    Assert.That(File.ReadAllBytes(outFile), Is.EqualTo(expected),
      $"{file} must read back byte-identical after relocation shrink");
  }

  private static void AssertE2fsckClean(string img, string label) {
    var r = Run("e2fsck", $"-fn {img}");
    var combined = r.Out + "\n" + r.Err;
    Assert.That(r.Exit, Is.EqualTo(0),
      $"e2fsck rejected the {label} image (exit {r.Exit}):\n{combined}");
    foreach (var marker in new[] { "FIXED", "Repair", "WARNING", "inconsistent", "corrupt", "invalid", "illegal" })
      Assert.That(combined, Does.Not.Contain(marker).IgnoreCase,
        $"e2fsck flagged '{marker}' on the {label} image:\n{combined}");
  }

  private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

  private static void RequireTools(params string[] tools) {
    if (!IsLinux) Assert.Ignore("e2fsprogs/mkfs run on Linux only.");
    foreach (var t in tools)
      if (!HasCommand(t)) Assert.Ignore($"'{t}' not installed.");
  }

  private static bool HasCommand(string name) {
    try {
      var r = Run("/bin/sh", $"-c \"command -v {name}\"");
      return r.Exit == 0 && !string.IsNullOrWhiteSpace(r.Out);
    } catch { return false; }
  }

  private readonly record struct ProcResult(string Out, string Err, int Exit);

  private static ProcResult Run(string tool, string args) {
    var psi = new ProcessStartInfo {
      FileName = tool, Arguments = args,
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {tool}");
    var o = p.StandardOutput.ReadToEnd();
    var e = p.StandardError.ReadToEnd();
    if (!p.WaitForExit(120_000)) { try { p.Kill(); } catch { /* best effort */ } }
    return new ProcResult(o, e, p.ExitCode);
  }
}
