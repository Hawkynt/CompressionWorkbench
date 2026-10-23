using System.Text.RegularExpressions;
using FileSystem.Zfs;
using static Compression.Tests.Zfs.ZfsReferencePools;

namespace Compression.Tests.Zfs;

/// <summary>
/// The reader against OpenZFS itself, through WSL. <c>zdb</c> reads pool files in user
/// space, so it needs no kernel module; creating a fresh pool does, and runs in the
/// libguestfs appliance with the Ubuntu kernel's <c>zfs.ko</c> (see
/// <c>ReferenceVectors/generate.sh</c> for the one-time appliance setup).
/// </summary>
[TestFixture]
[Category("Slow")]
[Category("ExternalTool")]
public partial class ZfsExternalConformanceTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_zfs_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static void RequireZdb() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("zdb"))
      Assert.Ignore("zdb not available in WSL (sudo apt install -y zfsutils-linux).");
  }

  [GeneratedRegex(@"^Dataset (\S+) \[ZPL\], ID (\d+), cr_txg \d+, \S+, (\d+) objects", RegexOptions.Multiline)]
  private static partial Regex ZdbDatasetLine();

  [Test]
  public void Zdb_ListsTheSameDatasetsAndSnapshots() {
    RequireZdb();
    var dir = Path.Combine(this._tmpDir, "pool");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "cwbmain.img"), ImageBytes("cwbmain"));

    var zdb = FsInteropToolbox.RunWsl($"zdb -e -p {FsInteropToolbox.WinToWsl(dir)} -d cwbmain 2>&1");
    var fromZdb = ZdbDatasetLine().Matches(zdb.StdOut).Select(m => m.Groups[1].Value).ToHashSet();
    Assert.That(fromZdb, Is.Not.Empty, "zdb printed no datasets:\n" + zdb.StdOut);

    using var reader = new ZfsReader(Open("cwbmain"));
    var ours = reader.Datasets.Where(d => d.Refusal == null).Select(d => d.Name).ToHashSet();
    Assert.That(ours, Is.EquivalentTo(fromZdb));
  }

  [Test]
  public void Zdb_ReportsTheSameSizeForEveryFileObject() {
    RequireZdb();
    var dir = Path.Combine(this._tmpDir, "pool");
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "cwbv28.img"), ImageBytes("cwbv28"));

    using var reader = new ZfsReader(Open("cwbv28"));
    var files = reader.Entries.Where(e => e.Dataset == "cwbv28/zpl3" && !e.IsDirectory && !e.IsSymlink).ToList();
    Assert.That(files, Is.Not.Empty);
    var objects = string.Join(' ', files.Select(f => f.ObjectId).Distinct());
    var zdb = FsInteropToolbox.RunWsl(
      $"zdb -e -p {FsInteropToolbox.WinToWsl(dir)} -dddd cwbv28/zpl3 {objects} 2>&1");
    foreach (var file in files) {
      // zdb prints each object's header, then its znode with "size  <n>".
      var block = Regex.Match(zdb.StdOut, $@"^\s+{file.ObjectId}\s+.*?\n(?:.*\n)*?\s+size\s+(\d+)", RegexOptions.Multiline);
      Assert.That(block.Success, Is.True, $"zdb said nothing about object {file.ObjectId}:\n{zdb.StdOut}");
      Assert.That(long.Parse(block.Groups[1].Value), Is.EqualTo(file.Size), file.Name);
    }
  }

  // ── a fresh pool, created by the OpenZFS kernel module in the appliance ──

  private const string SuperminPackages = "/usr/lib/x86_64-linux-gnu/guestfs/supermin.d/packages-zfs";

  [Test]
  public void FreshPool_EveryCompression_ReadsByteIdentical() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("guestfish"))
      Assert.Ignore("guestfish (libguestfs-tools) is not installed in WSL.");
    if (FsInteropToolbox.RunWsl($"test -s {SuperminPackages}").ExitCode != 0)
      Assert.Ignore($"The libguestfs appliance lacks zfsutils-linux; create {SuperminPackages} as ReferenceVectors/generate.sh describes.");

    var script = Path.Combine(this._tmpDir, "inner.sh");
    File.WriteAllText(script, """
      set -e
      modprobe zfs
      O=/sysroot/out; M=/sysroot/mnt; mkdir -p $O $M
      truncate -s 96M $O/live.img
      zpool create -f -o ashift=9 -O mountpoint=$M/live live $O/live.img
      for c in lzjb zle gzip-5 lz4 zstd-7 off; do
        zfs create -o compression=$c -o recordsize=32K live/$c
        d=$M/live/$c
        head -c 150000 /dev/urandom | od -An -tx1 > $d/text.txt
        head -c 70000 /dev/urandom > $d/rand.bin
        printf 'small %s\n' $c > $d/small.txt
        truncate -s 1M $d/holes.bin; printf x | dd of=$d/holes.bin bs=1 seek=500000 conv=notrunc 2>/dev/null
      done
      sync
      ( cd $M/live && find . -type f | sort | while read -r p; do echo "$(sha256sum < "$p" | cut -d' ' -f1) ${p#./}"; done ) > $O/live.manifest
      zpool export live
      echo DONE > $O/done
      """.Replace("\r\n", "\n"));

    var work = FsInteropToolbox.WinToWsl(this._tmpDir);
    var run = FsInteropToolbox.RunWsl(
      "export LIBGUESTFS_BACKEND=direct LIBGUESTFS_CACHEDIR=$HOME/cwbzfs-work/cache LIBGUESTFS_MEMSIZE=2048; " +
      $"mkdir -p $HOME/cwbzfs-work/cache; cd {work}; truncate -s 1G scratch.img; " +
      "printf 'run\\nmkfs ext4 /dev/sda\\nmount /dev/sda /\\nupload inner.sh /inner.sh\\n" +
      "debug sh \"sh /sysroot/inner.sh > /sysroot/log.txt 2>&1\"\\ndownload /log.txt log.txt\\n" +
      "download /out/live.img live.img\\ndownload /out/live.manifest live.manifest\\n' | timeout 900 guestfish -a scratch.img 2>&1");
    var log = Path.Combine(this._tmpDir, "log.txt");
    var manifestPath = Path.Combine(this._tmpDir, "live.manifest");
    if (!File.Exists(manifestPath))
      Assert.Inconclusive("the appliance did not produce a pool:\n" + run.StdOut + run.StdErr +
        (File.Exists(log) ? File.ReadAllText(log) : ""));

    using var image = File.OpenRead(Path.Combine(this._tmpDir, "live.img"));
    using var reader = new ZfsReader(image);
    var byPath = reader.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    var lines = File.ReadAllLines(manifestPath).Where(l => l.Length > 0).ToList();
    Assert.That(lines, Has.Count.EqualTo(24));
    foreach (var line in lines) {
      var sha = line[..64];
      var path = line[65..];
      Assert.That(byPath.ContainsKey(path), Is.True, $"missing {path}");
      Assert.That(Sha256Of(reader.Extract(byPath[path])), Is.EqualTo(sha), path);
    }
  }
}
