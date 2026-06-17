using System.Text;
using FileSystem.HfsPlus;

namespace Compression.Tests.HfsPlus;

/// <summary>
/// WSL-gated acceptance tests for the HFS+ writer and reader using the
/// reference <c>hfsprogs</c> toolchain (<c>mkfs.hfsplus</c>, <c>fsck.hfsplus</c>).
/// <para>
/// These complement <see cref="HfsPlusExternalConformanceTests"/> (which runs
/// <c>fsck.hfsplus</c> directly on a Linux host). Here the tools are reached
/// through WSL on a Windows host via <see cref="FsInteropToolbox.RunWsl"/> /
/// <see cref="FsInteropToolbox.WinToWsl"/>, so the same external proof is
/// available on the developer's Windows machine. Two directions are covered:
/// </para>
/// <list type="number">
/// <item><description>Forward: an image built by <see cref="HfsPlusWriter"/>
/// (deep directory nesting, long Unicode names, multi-block files) is accepted
/// by <c>fsck.hfsplus -f -n</c> with "appears to be OK" / exit 0.</description></item>
/// <item><description>Reverse: a volume formatted by <c>mkfs.hfsplus</c> is
/// parsed by <see cref="HfsPlusReader"/> without error (volume header, catalog
/// B-tree).</description></item>
/// </list>
/// Each test skips cleanly via <see cref="Assert.Ignore(string)"/> when WSL or
/// the specific tool is unavailable.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public class HfsPlusFsckHfsprogsTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_hfsplus_wsl_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static void RequireWslTool(string tool) {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed. Run `wsl --install` in Admin PowerShell and reboot, " +
                    "then `sudo apt install -y hfsprogs hfsutils` inside the Linux shell.");
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"WSL is present but '{tool}' is not installed. Run inside WSL: " +
                    "`sudo apt install -y hfsprogs`.");
  }

  // ── Forward gate: our writer → fsck.hfsplus -f -n → exit 0 ──────────────

  /// <summary>
  /// Given an HFS+ image whose catalog spans many leaf nodes (long Unicode
  /// names, several nested directories, a multi-block file), when checked by
  /// <c>fsck.hfsplus -f -n</c>, then the checker walks the volume header, both
  /// B-trees, the catalog hierarchy and the bitmap and reports the volume OK.
  /// This is deeper than <see cref="HfsPlusExternalConformanceTests"/>'s
  /// representative image: it stresses long names, deep nesting and larger
  /// payloads in one volume.
  /// </summary>
  [Test]
  public void Writer_DeepTreeLongNamesLargeFiles_PassesFsckHfsplus() {
    RequireWslTool("fsck.hfsplus");

    var writer = new HfsPlusWriter(volumeName: "DeepVol");

    // Root files of differing sizes.
    writer.AddFile("readme.txt", "root readme"u8.ToArray());

    // Long Unicode names whose case-folded order differs from raw byte order.
    writer.AddFile("A-very-long-file-name-that-exceeds-thirty-two-characters-for-sure.txt",
      "long ascii name"u8.ToArray());
    writer.AddFile("ünïcödé-långé-namé-with-äccénts-and-symbols-#1.dat",
      Encoding.UTF8.GetBytes("accented long name"));
    writer.AddFile("日本語の非常に長いファイル名前のテスト.txt",
      Encoding.UTF8.GetBytes("cjk long name"));

    // Several nested directory levels, each with files.
    writer.AddFile("docs/intro.txt", "intro"u8.ToArray());
    writer.AddFile("docs/api/reference.txt", "reference"u8.ToArray());
    writer.AddFile("docs/api/v2/changes.txt", "changes"u8.ToArray());
    writer.AddFile("media/images/photos/holiday/beach.txt", "beach"u8.ToArray());

    // Multi-block files of varying sizes (forces multi-extent-free contiguous
    // runs across several allocation blocks).
    var rng = new Random(1337);
    var medium = new byte[40_000]; rng.NextBytes(medium);
    writer.AddFile("data/medium.bin", medium);
    var large = new byte[200_000]; rng.NextBytes(large);
    writer.AddFile("data/large.bin", large);

    // A directory with enough files to spill the catalog across many leaves
    // joined by an index level.
    for (var i = 0; i < 1500; i++)
      writer.AddFile($"bulk/file{i:D4}.dat", Encoding.ASCII.GetBytes($"content-{i}"));

    AssertFsckClean(writer.Build(), "deep-tree image");
  }

  /// <summary>
  /// Given an HFS+ image produced with a non-default (16 KB) allocation block
  /// size, when checked by <c>fsck.hfsplus</c>, then it is still accepted. This
  /// confirms the writer's block-size handling matches the reference checker's
  /// expectations beyond the 4 KB default.
  /// </summary>
  [Test]
  public void Writer_NonDefaultBlockSize_PassesFsckHfsplus() {
    RequireWslTool("fsck.hfsplus");

    var writer = new HfsPlusWriter(volumeName: "BlockVol");
    writer.AddFile("hello.txt", "hello at 16k blocks"u8.ToArray());
    writer.AddFile("nested/dir/deep.bin", new byte[50_000]);

    AssertFsckClean(writer.Build(blockSize: 16384), "16 KB block-size image");
  }

  /// <summary>
  /// Given an HFS+ image mutated after creation by <see cref="HfsPlusModifier"/>
  /// (the engine behind the descriptor's CanModify capability), when re-checked
  /// by <c>fsck.hfsplus</c>, then it must still be accepted — proving the
  /// CanModify promotion is honest (a mutation leaves a structurally sound
  /// volume, not a corrupt one).
  /// </summary>
  [Test]
  public void Writer_MutatedByModifier_StillPassesFsckHfsplus() {
    RequireWslTool("fsck.hfsplus");

    var writer = new HfsPlusWriter(volumeName: "ModVol");
    writer.AddFile("original.txt", "original content"u8.ToArray());
    writer.AddFile("docs/note.txt", "a note"u8.ToArray());
    var image = writer.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;
    HfsPlusModifier.AddFile(ms, "added.txt", "added after creation"u8.ToArray());

    AssertFsckClean(ms.ToArray(), "modifier-mutated image");
  }

  // ── Reverse gate: mkfs.hfsplus → our reader parses cleanly ──────────────

  /// <summary>
  /// Given a volume freshly formatted by the reference <c>mkfs.hfsplus</c>, when
  /// read by <see cref="HfsPlusReader"/>, then the reader parses the Apple-tool
  /// volume header and catalog B-tree without throwing and surfaces no spurious
  /// entries (a fresh volume has only the root, which the reader does not list).
  /// This proves our reader consumes a real reference layout, not just our own
  /// writer's output.
  /// </summary>
  [Test]
  public void Reader_ParsesMkfsHfsplusVolume() {
    RequireWslTool("mkfs.hfsplus");

    var imgPath = Path.Combine(this._tmpDir, "mkfs.hfsplus.img");
    var wslImg = FsInteropToolbox.WinToWsl(imgPath);

    // 8 MB zero-filled image, then format it with the reference tool.
    var create = FsInteropToolbox.RunWsl(
      $"dd if=/dev/zero of={wslImg} bs=1M count=8 status=none && " +
      $"mkfs.hfsplus -v ReverseVol {wslImg}");
    Assert.That(create.ExitCode, Is.EqualTo(0),
      $"mkfs.hfsplus failed to create the image:\nstdout:\n{create.StdOut}\nstderr:\n{create.StdErr}");
    Assert.That(File.Exists(imgPath), Is.True, "mkfs.hfsplus did not produce an image file on disk");

    using var fs = File.OpenRead(imgPath);
    using var reader = new HfsPlusReader(fs);
    // A pristine volume lists no user files/dirs; the contract is "parses
    // without throwing", which reaching this assertion already proves.
    Assert.That(reader.Entries, Is.Not.Null,
      "HfsPlusReader returned a null entry list on a mkfs.hfsplus volume");
    Assert.That(reader.Entries.Any(e => !e.IsDirectory), Is.False,
      "A freshly formatted mkfs.hfsplus volume should expose no user files");
  }

  // ── Shared assertion ────────────────────────────────────────────────────

  private void AssertFsckClean(byte[] image, string what) {
    var imagePath = Path.Combine(this._tmpDir, "volume.hfsplus");
    File.WriteAllBytes(imagePath, image);
    var wslImg = FsInteropToolbox.WinToWsl(imagePath);

    // -f forces a full check even when the volume looks clean; -n answers "no"
    // to every repair prompt, making the run strictly read-only.
    var result = FsInteropToolbox.RunWsl($"fsck.hfsplus -f -n {wslImg}");
    var combined = result.StdOut + "\n" + result.StdErr;

    Assert.That(combined, Does.Contain("appears to be OK"),
      $"fsck.hfsplus did not report our {what} clean (exit {result.ExitCode}).\n{combined}");
    Assert.That(combined, Does.Not.Contain("found corrupt"),
      $"fsck.hfsplus reported corruption in our {what}.\n{combined}");
  }
}
