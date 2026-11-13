using System.Security.Cryptography;

namespace Compression.Tests.Zfs;

/// <summary>
/// OpenZFS as the oracle for pools this repository wrote or edited: <c>zdb</c> in WSL, and the
/// kernel module inside the libguestfs appliance (<c>ReferenceVectors/generate.sh</c> describes
/// how the appliance gets <c>zfsutils-linux</c>).
/// </summary>
internal static class ZfsOracle {
  private const string SuperminPackages = "/usr/lib/x86_64-linux-gnu/guestfs/supermin.d/packages-zfs";

  public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  public static void RequireZdb() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("zdb"))
      Assert.Ignore("zdb not available in WSL (sudo apt install -y zfsutils-linux).");
  }

  public static void RequireAppliance() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("guestfish"))
      Assert.Ignore("guestfish (libguestfs-tools) is not installed in WSL.");
    if (FsInteropToolbox.RunWsl($"test -s {SuperminPackages}").ExitCode != 0)
      Assert.Ignore($"The libguestfs appliance lacks zfsutils-linux; create {SuperminPackages} as ReferenceVectors/generate.sh describes.");
  }

  /// <summary>A scratch directory holding <paramref name="image"/> as <c>&lt;pool&gt;.img</c>; returns it and its WSL path.</summary>
  public static (string Dir, string Wsl) Stage(byte[] image, string pool) => Stage([(pool + ".img", image)]);

  /// <summary>A scratch directory holding the device images of one pool (file names ending in <c>.img</c>).</summary>
  public static (string Dir, string Wsl) Stage(IReadOnlyList<(string File, byte[] Data)> images) {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_zfs_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    foreach (var (file, data) in images)
      File.WriteAllBytes(Path.Combine(dir, file), data);
    return (dir, FsInteropToolbox.WinToWsl(dir));
  }

  public static void Cleanup(string dir) {
    try { Directory.Delete(dir, true); } catch { /* best effort */ }
  }

  /// <summary><c>zdb -bcc</c> finds every block accounted for in the space maps and every checksum right.</summary>
  public static void AssertNoLeaks(string wsl, string pool) {
    var bcc = FsInteropToolbox.RunWsl($"zdb -e -p {wsl} -bcc {pool} 2>&1");
    Assert.That(bcc.StdOut, Does.Contain("No leaks (block sum matches space maps exactly)"), bcc.StdOut);
    Assert.That(bcc.StdOut, Does.Not.Contain("mismatch").And.Not.Contain("error"), bcc.StdOut);
  }

  /// <summary>
  /// <c>zdb -d</c> walks every dataset and the MOS: no MOS object leaks and every feature reference
  /// count it can check (space-map histograms included) is right. Returns its output.
  /// </summary>
  public static string AssertFeatureCounts(string wsl, string pool) {
    var d = FsInteropToolbox.RunWsl($"zdb -e -p {wsl} -d {pool} 2>&1");
    Assert.That(d.ExitCode, Is.Zero, d.StdOut);
    Assert.That(d.StdOut, Does.Not.Contain("mismatch").And.Not.Contain("leaked").And.Not.Contain("error"), d.StdOut);
    var b = FsInteropToolbox.RunWsl($"zdb -e -p {wsl} -b {pool} 2>&1");
    Assert.That(b.StdOut, Does.Not.Contain("space map refcount mismatch"), b.StdOut);
    return d.StdOut;
  }

  /// <summary><c>zdb -r</c> copies <paramref name="path"/> of <paramref name="dataset"/> out byte for byte.</summary>
  public static void AssertCopiesOut(string dir, string wsl, string dataset, string path, string sha) {
    var copy = FsInteropToolbox.RunWsl($"zdb -e -p {wsl} -r {dataset} '{path}' {wsl}/copy.bin 2>&1");
    Assert.That(copy.ExitCode, Is.Zero, $"{dataset} {path}: {copy.StdOut}");
    Assert.That(Sha(File.ReadAllBytes(Path.Combine(dir, "copy.bin"))), Is.EqualTo(sha), $"{dataset} {path}");
  }

  /// <summary>
  /// Imports the pool (every <c>.img</c> staged in <paramref name="dir"/>) in the kernel, scrubs it,
  /// mounts every dataset under one root and returns the SHA-256 of every regular file by path
  /// (child datasets under their names), after an export.
  /// </summary>
  public static Dictionary<string, string> KernelManifest(string dir, string wsl, string pool) => KernelManifest(dir, wsl, pool, out _);

  /// <summary>As <see cref="KernelManifest(string, string, string)"/>; <paramref name="log"/> also lists every <c>feature@</c> property as <c>FEATURE name state</c>.</summary>
  public static Dictionary<string, string> KernelManifest(string dir, string wsl, string pool, out string log) {
    var uploads = string.Concat(Directory.GetFiles(dir, "*.img").Select(Path.GetFileName).Order(StringComparer.Ordinal)
      .Select(f => $"upload {f} /out/{f}\\n"));
    File.WriteAllText(Path.Combine(dir, "inner.sh"), $$"""
      modprobe zfs
      O=/sysroot/out; M=/sysroot/mnt; mkdir -p $M
      zpool import -d $O -N -o altroot=$M {{pool}} || { echo IMPORT-FAILED; exit 1; }
      zpool scrub -w {{pool}}
      zpool status -v {{pool}}
      zpool get -H -o property,value all {{pool}} | grep feature@ | while read -r k v; do echo "FEATURE ${k#feature@} $v"; done
      zfs set mountpoint=/ {{pool}}
      zfs mount -a
      ( cd $M && find . -mindepth 1 -type f | sort | while read -r p; do echo "$(sha256sum < "$p" | cut -d' ' -f1) ${p#./}"; done ) > $O/pool.manifest
      zpool export {{pool}} && echo EXPORTED
      """.Replace("\r\n", "\n"));
    var run = FsInteropToolbox.RunWsl(
      "export LIBGUESTFS_BACKEND=direct LIBGUESTFS_CACHEDIR=$HOME/cwbzfs-work/cache LIBGUESTFS_MEMSIZE=2048; " +
      $"mkdir -p $HOME/cwbzfs-work/cache; cd {wsl}; truncate -s 512M scratch.img; " +
      $"printf 'run\\nmkfs ext4 /dev/sda\\nmount /dev/sda /\\nmkdir /out\\n{uploads}upload inner.sh /inner.sh\\n" +
      "debug sh \"sh /sysroot/inner.sh > /sysroot/log.txt 2>&1\"\\ndownload /log.txt log.txt\\n" +
      "download /out/pool.manifest pool.manifest\\n' | timeout 300 guestfish -a scratch.img 2>&1");
    var logPath = Path.Combine(dir, "log.txt");
    log = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
    if (!File.Exists(Path.Combine(dir, "pool.manifest")) && !log.Contains("IMPORT-FAILED"))
      Assert.Inconclusive("the appliance did not run:\n" + run.StdOut + run.StdErr);
    Assert.That(log, Does.Not.Contain("IMPORT-FAILED"), log);
    Assert.That(log, Does.Contain("with 0 errors").And.Contain("No known data errors").And.Contain("EXPORTED"), log);
    return File.ReadAllLines(Path.Combine(dir, "pool.manifest")).Where(l => l.Length > 65)
      .ToDictionary(l => l[65..], l => l[..64], StringComparer.Ordinal);
  }
}
