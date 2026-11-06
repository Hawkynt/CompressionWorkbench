#pragma warning disable CS1591
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

/// <summary>
/// UFS2 volumes this package creates and edits, judged by FreeBSD's <c>fsck_ffs -n</c>
/// (<c>fsck.ufs</c> from Debian's ufsutils 8.2) and by the Linux kernel's ufs driver
/// mounting them read-only with <c>ufstype=ufs2</c> (libguestfs appliance) and reading
/// every file back. Each oracle skips when its tool is not reachable from WSL.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
[Category("Wsl")]
public sealed class Ufs2ExternalTests {
  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    _tmp = Path.Combine(Path.GetTempPath(), $"cwb_ufs2x_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
  }

  private static readonly Dictionary<string, byte[]> Tree = new() {
    ["hello.txt"] = "hello ufs2\n"u8.ToArray(),
    ["empty.bin"] = [],
    ["direct.bin"] = Ufs2WriterTests.Pattern(196_608, 1),
    ["sub/indirect.bin"] = Ufs2WriterTests.Pattern(1_500_000, 2),
    ["sub/deeper/n.txt"] = "nested\n"u8.ToArray(),
  };

  private string Save(string name, byte[] image) {
    var path = Path.Combine(_tmp, name);
    File.WriteAllBytes(path, image);
    return path;
  }

  private static (int Exit, string Output) Fsck(string path) {
    var r = FsInteropToolbox.RunWsl($"fsck.ufs -n {FsInteropToolbox.WinToWsl(path)} 2>&1");
    return (r.ExitCode, r.StdOut + r.StdErr);
  }

  private static void AssertClean((int Exit, string Output) fsck) {
    Assert.That(fsck.Exit, Is.Zero, fsck.Output);
    Assert.That(fsck.Output, Does.Not.Contain("UNREF").And.Not.Contain("BAD").And.Not.Contain("INCORRECT")
      .And.Not.Contain("WRONG").And.Not.Contain("SALVAGE").And.Not.Contain("FIX?").And.Not.Contain("MISSING"), fsck.Output);
  }

  private static void RequireFsck() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("fsck.ufs"))
      Assert.Ignore("fsck.ufs (FreeBSD fsck_ffs from ufsutils) is not on the WSL PATH.");
  }

  [Test]
  public void GivenAUfs2VolumeTheWriterMade_WhenChecked_ThenFsckFfsIsClean() {
    RequireFsck();
    AssertClean(Fsck(Save("w.ufs", Ufs2WriterTests.Create(Tree))));
  }

  [Test]
  public void GivenAUfs2VolumeEditedInPlace_WhenChecked_ThenFsckFfsIsClean() {
    RequireFsck();
    using var ms = new MemoryStream();
    ms.Write(Ufs2WriterTests.Create(Tree));
    var d = new UfsFormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("later/added.bin", Ufs2WriterTests.Pattern(400_000, 5))]);
    for (var i = 0; i < 60; ++i) d.Add(ms, [ArchiveInputInfo.InMemory($"many/file-with-a-longish-name-{i:D3}", [(byte)i])]);
    d.Remove(ms, ["sub/indirect.bin", "hello.txt"]);
    AssertClean(Fsck(Save("e.ufs", ms.ToArray())));
  }

  /// <summary>A volume FreeBSD's own newfs -O2 made (the checked-in reference vector), edited here.</summary>
  [Test]
  public void GivenAFreeBsdNewfsVolume_WhenEditedInPlace_ThenFsckFfsIsClean() {
    RequireFsck();
    using var resource = typeof(Ufs2ExternalTests).Assembly.GetManifestResourceStream("UfsVectors.newfs-ufs2.img.gz")!;
    using var gzip = new System.IO.Compression.GZipStream(resource, System.IO.Compression.CompressionMode.Decompress);
    using var ms = new MemoryStream();
    gzip.CopyTo(ms);
    var d = new UfsFormatDescriptor();
    d.Add(ms, [ArchiveInputInfo.InMemory("docs/readme.txt", "added to a newfs volume\n"u8.ToArray()),
               ArchiveInputInfo.InMemory("blob.bin", Ufs2WriterTests.Pattern(250_000, 7))]);
    AssertClean(Fsck(Save("n.ufs", ms.ToArray())));
    ms.Position = 0;
    using var r = new UfsReader(ms);
    Assert.That(Encoding.ASCII.GetString(r.Extract(r.Entries.Single(e => e.Name == "docs/readme.txt"))), Is.EqualTo("added to a newfs volume\n"));
  }

  [Test, Category("KernelMount"), Category("Slow")]
  public void GivenAUfs2VolumeTheWriterMade_WhenTheKernelMountsIt_ThenEveryFileReadsBackByteExact()
    => AssertKernelReads(Save("k.ufs", Ufs2WriterTests.Create(Tree)), Tree);

  /// <summary>A volume after one maintenance operation, and the files it must still hold.</summary>
  private static (byte[] Image, Dictionary<string, byte[]> Files) Maintained(string operation) {
    var d = new UfsFormatDescriptor();
    switch (operation) {
      case "move-zeroing-source": {
        var (ms, files, _) = Ufs2MaintenanceTests.Moved(zeroSource: true);
        return (ms.ToArray(), files);
      }
      default: {
        var (ms, files) = Ufs2MaintenanceTests.Fragmented();
        switch (operation) {
          case "defragment": d.Defragment(ms); break;
          case "defragment-at-end": d.Defragment(ms, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd }); break;
          case "wipe": d.WipeUnusedSpace(ms); break;
          default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
        Ufs2MaintenanceTests.AssertHolds(ms, files);
        return (ms.ToArray(), files);
      }
    }
  }

  [TestCase("defragment")]
  [TestCase("defragment-at-end")]
  [TestCase("wipe")]
  [TestCase("move-zeroing-source")]
  public void GivenAUfs2VolumeAfterMaintenance_WhenChecked_ThenFsckFfsIsClean(string operation) {
    RequireFsck();
    AssertClean(Fsck(Save(operation + ".ufs", Maintained(operation).Image)));
  }

  [TestCase("defragment"), Category("KernelMount"), Category("Slow")]
  [TestCase("defragment-at-end")]
  [TestCase("wipe")]
  [TestCase("move-zeroing-source")]
  public void GivenAUfs2VolumeAfterMaintenance_WhenTheKernelMountsIt_ThenEveryFileReadsBackByteExact(string operation) {
    var (image, files) = Maintained(operation);
    AssertKernelReads(Save(operation + ".ufs", image), files);
  }

  /// <summary>The Linux ufs driver mounts the volume read-only as UFS2 and hashes every file.</summary>
  private void AssertKernelReads(string img, IReadOnlyDictionary<string, byte[]> files) {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("guestfish"))
      Assert.Ignore("guestfish not available (install libguestfs-tools).");
    var script = Path.Combine(_tmp, "gf.txt");
    File.WriteAllText(script, string.Join('\n', [
      "run", "mount-vfs ro,ufstype=ufs2 ufs /dev/sda /",
      .. files.Keys.Select(k => $"checksum sha256 /{k}"), "umount /",
    ]) + "\n");
    var r = FsInteropToolbox.RunWsl(
      "export LIBGUESTFS_BACKEND=direct; echo 1234 | sudo -S chmod 0666 /dev/kvm 2>/dev/null; echo 1234 | sudo -S chmod 0644 /boot/vmlinuz-* 2>/dev/null; " +
      $"timeout 600 guestfish -a {FsInteropToolbox.WinToWsl(img)} -f {FsInteropToolbox.WinToWsl(script)} 2>&1");
    if (r.StdOut.Contains("cannot find any suitable libguestfs supermin", StringComparison.Ordinal) || r.StdOut.Contains("/dev/kvm", StringComparison.Ordinal))
      Assert.Ignore("the libguestfs appliance cannot start here: " + r.StdOut);
    Assert.That(r.ExitCode, Is.Zero, r.StdOut + r.StdErr);
    var lines = r.StdOut.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    Assert.That(lines.TakeLast(files.Count), Is.EqualTo(files.Values.Select(v => Convert.ToHexStringLower(SHA256.HashData(v)))), r.StdOut);
  }
}
