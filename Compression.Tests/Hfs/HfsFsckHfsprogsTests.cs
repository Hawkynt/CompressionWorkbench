using System.Text;
using FileSystem.Hfs;

namespace Compression.Tests.Hfs;

/// <summary>
/// WSL-gated acceptance tests for the classic (pre-HFS+) HFS writer and reader
/// using the reference <c>hfsprogs</c> / <c>hfsutils</c> toolchain.
/// <para>
/// hfsprogs ships a single checker, <c>fsck.hfsplus</c>, that recognises BOTH
/// HFS Plus and the classic HFS volume layout ("** Checking HFS volume."), so
/// it serves as the external forward gate for our <see cref="HfsWriter"/> too.
/// The reverse gates use <c>mkfs.hfs</c> (creates an empty classic volume) and
/// the <c>hfsutils</c> family (<c>hformat</c> + <c>hcopy</c>) which can write a
/// file into a classic volume without a kernel mount — the only way to get a
/// populated reference image since WSL has no <c>hfs</c> kernel module.
/// </para>
/// All tools are reached through WSL on a Windows host via
/// <see cref="FsInteropToolbox.RunWsl"/> / <see cref="FsInteropToolbox.WinToWsl"/>.
/// Each test skips cleanly when WSL or the specific tool is unavailable.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public class HfsFsckHfsprogsTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_hfs_wsl_{Guid.NewGuid():N}");
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
                    "`sudo apt install -y hfsprogs hfsutils`.");
  }

  // ── Forward gate: our classic-HFS writer → fsck.hfsplus → exit 0 ────────

  /// <summary>
  /// Given a classic HFS image built by <see cref="HfsWriter"/> with several
  /// root files, when checked by <c>fsck.hfsplus -f -n</c> (which detects and
  /// checks the classic HFS layout, not just HFS Plus), then the volume is
  /// reported clean. This is the external proof that our MDB, volume bitmap and
  /// catalog/extents B*-trees match what an independent checker expects.
  /// </summary>
  [Test]
  public void Writer_MultipleFiles_PassesFsckHfsplus() {
    RequireWslTool("fsck.hfsplus");

    var writer = new HfsWriter();
    writer.SetVolumeName("ClassicVol");
    writer.AddFile("README.TXT", "classic hfs root file"u8.ToArray());
    writer.AddFile("DATA.BIN", Encoding.ASCII.GetBytes(new string('x', 4096)));
    writer.AddFile("NOTES.TXT", "second note"u8.ToArray());

    AssertFsckHfsClean(writer.Build(), "classic-HFS image");
  }

  /// <summary>
  /// Given a classic HFS image mutated by <see cref="HfsModifier"/> (the engine
  /// behind the descriptor's CanModify capability), when re-checked by
  /// <c>fsck.hfsplus</c>, then it must still be accepted — proving the CanModify
  /// promotion is honest for classic HFS.
  /// </summary>
  [Test]
  public void Writer_MutatedByModifier_StillPassesFsckHfsplus() {
    RequireWslTool("fsck.hfsplus");

    var writer = new HfsWriter();
    writer.SetVolumeName("ModClassic");
    writer.AddFile("ORIG.TXT", "original"u8.ToArray());
    var image = writer.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;
    HfsModifier.AddFile(ms, "ADDED.TXT", "added after creation"u8.ToArray());

    AssertFsckHfsClean(ms.ToArray(), "modifier-mutated classic-HFS image");
  }

  // ── Reverse gate A: mkfs.hfs (empty) → our reader parses cleanly ────────

  /// <summary>
  /// Given an empty classic volume formatted by the reference <c>mkfs.hfs</c>,
  /// when read by <see cref="HfsReader"/>, then the reader parses the MDB and
  /// catalog B-tree without throwing and lists no user files. Proves the reader
  /// consumes a real reference layout.
  /// </summary>
  [Test]
  public void Reader_ParsesMkfsHfsEmptyVolume() {
    RequireWslTool("mkfs.hfs");

    var imgPath = Path.Combine(this._tmpDir, "mkfs.hfs.img");
    var wslImg = FsInteropToolbox.WinToWsl(imgPath);

    var create = FsInteropToolbox.RunWsl(
      $"dd if=/dev/zero of={wslImg} bs=1M count=4 status=none && " +
      $"mkfs.hfs -h {wslImg}");
    Assert.That(create.ExitCode, Is.EqualTo(0),
      $"mkfs.hfs failed to create the image:\nstdout:\n{create.StdOut}\nstderr:\n{create.StdErr}");
    Assert.That(File.Exists(imgPath), Is.True, "mkfs.hfs did not produce an image file on disk");

    using var fs = File.OpenRead(imgPath);
    var reader = new HfsReader(fs);
    Assert.That(reader.Entries, Is.Not.Null,
      "HfsReader returned a null entry list on a mkfs.hfs volume");
    Assert.That(reader.Entries.Any(e => !e.IsDirectory), Is.False,
      "A freshly formatted mkfs.hfs volume should expose no user files");
  }

  // ── Reverse gate B: hfsutils-populated → our reader reads name + bytes ──

  /// <summary>
  /// Given a classic volume formatted and populated by the reference
  /// <c>hfsutils</c> tools (<c>hformat</c> + <c>hcopy</c>), when read by
  /// <see cref="HfsReader"/>, then the reader surfaces the copied file by name
  /// and extracts its exact bytes. This is the strongest reverse proof: our
  /// reader walks a catalog written entirely by an independent implementation
  /// and recovers both the directory entry and the file content.
  /// </summary>
  [Test]
  public void Reader_ReadsHfsutilsPopulatedVolume() {
    RequireWslTool("hformat");
    RequireWslTool("hcopy");

    var imgPath = Path.Combine(this._tmpDir, "hfsutils.img");
    var wslImg = FsInteropToolbox.WinToWsl(imgPath);
    var srcPath = Path.Combine(this._tmpDir, "payload.txt");
    var wslSrc = FsInteropToolbox.WinToWsl(srcPath);

    // No newline so hcopy's raw mode round-trips byte-for-byte (text mode would
    // translate Unix '\n' to classic-Mac '\r').
    var payload = Encoding.ASCII.GetBytes("hello from hfsutils reverse gate");
    File.WriteAllBytes(srcPath, payload);

    // Format a classic volume, mount it via hfsutils' userspace driver, copy the
    // payload in with raw (-r) mode so no newline translation occurs, then
    // unmount so the catalog is flushed.
    var script =
      $"dd if=/dev/zero of={wslImg} bs=1M count=4 status=none && " +
      $"hformat -l ReverseHfs {wslImg} && " +
      $"hmount {wslImg} && " +
      $"hcopy -r {wslSrc} :HELLO.TXT && " +
      "humount";
    var run = FsInteropToolbox.RunWsl(script);
    Assert.That(run.ExitCode, Is.EqualTo(0),
      $"hfsutils failed to build/populate the image:\nstdout:\n{run.StdOut}\nstderr:\n{run.StdErr}");

    using var fs = File.OpenRead(imgPath);
    var reader = new HfsReader(fs);
    var hello = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.EndsWith("HELLO.TXT"));
    Assert.That(hello, Is.Not.Null,
      $"HfsReader did not find HELLO.TXT in the hfsutils-populated volume. " +
      $"Entries: {string.Join(", ", reader.Entries.Select(e => e.Name))}");

    var extracted = reader.Extract(hello!);
    Assert.That(extracted, Is.EqualTo(payload),
      "HfsReader did not recover the exact bytes hfsutils wrote into HELLO.TXT");
  }

  // ── Shared assertion ────────────────────────────────────────────────────

  private void AssertFsckHfsClean(byte[] image, string what) {
    var imagePath = Path.Combine(this._tmpDir, "volume.hfs");
    File.WriteAllBytes(imagePath, image);
    var wslImg = FsInteropToolbox.WinToWsl(imagePath);

    var result = FsInteropToolbox.RunWsl($"fsck.hfsplus -f -n {wslImg}");
    var combined = result.StdOut + "\n" + result.StdErr;

    Assert.That(combined, Does.Contain("appears to be OK"),
      $"fsck.hfsplus did not report our {what} clean (exit {result.ExitCode}).\n{combined}");
    Assert.That(combined, Does.Not.Contain("found corrupt"),
      $"fsck.hfsplus reported corruption in our {what}.\n{combined}");
  }
}
