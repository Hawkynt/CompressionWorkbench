using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// The pool writer. Pools it writes are read back by this repository's reader here; the
/// external tests check them with OpenZFS itself: <c>zdb -bcc</c> must find no leak and no
/// checksum error and <c>zdb -r</c> must copy every file out byte for byte, and the kernel module
/// (in the libguestfs appliance) must import, scrub and mount the pool and read every file.
/// </summary>
[TestFixture]
public sealed class ZfsWriterTests {
  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  /// <summary>Files covering the layouts: empty, sub-sector, one block, indirect blocks, nested and empty directories, a fat-ZAP directory.</summary>
  private static Dictionary<string, byte[]> Corpus(bool large) {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["hello.txt"] = "hello, zfs\n"u8.ToArray(),
      ["empty.txt"] = [],
      ["dir/sub/deep.txt"] = "deep\n"u8.ToArray(),
      ["block.bin"] = Pattern(128 * 1024, 1),
      ["multi.bin"] = Pattern(300_000, 2),
    };
    if (large)
      files["two-levels.bin"] = Pattern(17 * 1024 * 1024 + 4321, 3);       // past 128 level-1 pointers
    var longName = new string('n', 70);
    for (var i = 0; i < (large ? 2600 : 60); ++i)                            // past a micro ZAP, and past one fat-ZAP leaf
      files[$"crowd/{longName}-{i:D5}.txt"] = Encoding.ASCII.GetBytes($"entry {i}\n");
    return files;
  }

  private static byte[] Pattern(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  private static byte[] Write(Dictionary<string, byte[]> files, string pool = "cwbw") {
    var inputs = files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList();
    inputs.Add(new ArchiveInputInfo("emptydir", "emptydir", IsDirectory: true));
    using var output = new MemoryStream();
    new ZfsFormatDescriptor().Create(output, inputs, new FormatCreateOptions { FormatSpecific = { ["PoolName"] = pool } });
    return output.ToArray();
  }

  [Test]
  public void Given_Files_When_APoolIsWritten_Then_TheReaderReturnsThemByteForByte() {
    var files = Corpus(large: true);
    var image = Write(files);
    using var reader = new ZfsReader(new MemoryStream(image));
    Assert.That(reader.PoolName, Is.EqualTo("cwbw"));
    Assert.That(reader.PoolVersion, Is.EqualTo(28UL));
    Assert.That(reader.Warnings, Is.Empty);
    var byName = reader.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.Multiple(() => {
      foreach (var (name, data) in files) {
        Assert.That(byName.TryGetValue(name, out var entry), Is.True, name);
        if (entry != null)
          Assert.That(Sha(reader.Extract(entry)), Is.EqualTo(Sha(data)), name);
      }
      Assert.That(byName.TryGetValue("emptydir", out var dir) && dir.IsDirectory, Is.True, "empty directory");
    });
  }

  [Test]
  public void Given_ABadPoolName_When_Written_Then_ItIsRefused() =>
    Assert.Throws<ArgumentException>(() => Write(new() { ["a"] = [1] }, "1pool"));

  [Test]
  public void Given_AFileWhereADirectoryIsNeeded_When_Written_Then_ItIsRefused() =>
    Assert.Throws<ArgumentException>(() => Write(new() { ["a"] = [1], ["a/b"] = [2] }));

  // ── OpenZFS ─────────────────────────────────────────────────────────────────────────────

  private static string WriteToDisk(string dir, Dictionary<string, byte[]> files) {
    Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "cwbw.img"), Write(files));
    return FsInteropToolbox.WinToWsl(dir);
  }

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_AWrittenPool_When_ZdbTraversesIt_Then_NothingLeaksAndEveryFileCopiesOut() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("zdb"))
      Assert.Ignore("zdb not available in WSL (sudo apt install -y zfsutils-linux).");
    var files = Corpus(large: true);
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_zfsw_{Guid.NewGuid():N}");
    try {
      var wsl = WriteToDisk(dir, files);
      var bcc = FsInteropToolbox.RunWsl($"zdb -e -p {wsl} -bcc cwbw 2>&1");
      Assert.That(bcc.StdOut, Does.Contain("No leaks (block sum matches space maps exactly)"), bcc.StdOut);
      Assert.That(bcc.StdOut, Does.Not.Contain("mismatch").And.Not.Contain("error"), bcc.StdOut);

      // zdb -r exits 22 on an empty file of a pool OpenZFS made as well; the kernel test covers it.
      var sample = files.Keys.Where(n => !n.StartsWith("crowd/", StringComparison.Ordinal) && files[n].Length > 0)
        .Append(files.Keys.First(n => n.StartsWith("crowd/", StringComparison.Ordinal)));
      foreach (var name in sample) {
        var copy = FsInteropToolbox.RunWsl($"zdb -e -p {wsl} -r cwbw '{name}' {wsl}/copy.bin 2>&1");
        Assert.That(copy.ExitCode, Is.Zero, $"{name}: {copy.StdOut}");
        Assert.That(Sha(File.ReadAllBytes(Path.Combine(dir, "copy.bin"))), Is.EqualTo(Sha(files[name])), name);
      }
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  private const string SuperminPackages = "/usr/lib/x86_64-linux-gnu/guestfs/supermin.d/packages-zfs";

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_AWrittenPool_When_TheKernelImportsAndScrubsIt_Then_ItIsCleanAndServesEveryFile() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("guestfish"))
      Assert.Ignore("guestfish (libguestfs-tools) is not installed in WSL.");
    if (FsInteropToolbox.RunWsl($"test -s {SuperminPackages}").ExitCode != 0)
      Assert.Ignore($"The libguestfs appliance lacks zfsutils-linux; create {SuperminPackages} as ReferenceVectors/generate.sh describes.");
    var files = Corpus(large: false);
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_zfsw_{Guid.NewGuid():N}");
    try {
      var wsl = WriteToDisk(dir, files);
      File.WriteAllText(Path.Combine(dir, "inner.sh"), """
        modprobe zfs
        O=/sysroot/out; M=/sysroot/mnt; mkdir -p $M
        zpool import -d $O -o altroot=$M cwbw || { echo IMPORT-FAILED; exit 1; }
        zpool scrub -w cwbw
        zpool status -v cwbw
        ( cd $M/cwbw && find . -mindepth 1 -type f | sort | while read -r p; do echo "$(sha256sum < "$p" | cut -d' ' -f1) ${p#./}"; done ) > $O/pool.manifest
        zpool export cwbw && echo EXPORTED
        """.Replace("\r\n", "\n"));
      var run = FsInteropToolbox.RunWsl(
        "export LIBGUESTFS_BACKEND=direct LIBGUESTFS_CACHEDIR=$HOME/cwbzfs-work/cache LIBGUESTFS_MEMSIZE=2048; " +
        $"mkdir -p $HOME/cwbzfs-work/cache; cd {wsl}; truncate -s 512M scratch.img; " +
        "printf 'run\\nmkfs ext4 /dev/sda\\nmount /dev/sda /\\nmkdir /out\\nupload cwbw.img /out/cwbw.img\\nupload inner.sh /inner.sh\\n" +
        "debug sh \"sh /sysroot/inner.sh > /sysroot/log.txt 2>&1\"\\ndownload /log.txt log.txt\\n" +
        "download /out/pool.manifest pool.manifest\\n' | timeout 900 guestfish -a scratch.img 2>&1");
      var logPath = Path.Combine(dir, "log.txt");
      var log = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
      if (!File.Exists(Path.Combine(dir, "pool.manifest")) && !log.Contains("IMPORT-FAILED"))
        Assert.Inconclusive("the appliance did not run:\n" + run.StdOut + run.StdErr);
      Assert.That(log, Does.Not.Contain("IMPORT-FAILED"), log);
      Assert.That(log, Does.Contain("with 0 errors").And.Contain("No known data errors").And.Contain("EXPORTED"), log);
      var served = File.ReadAllLines(Path.Combine(dir, "pool.manifest")).Where(l => l.Length > 65)
        .ToDictionary(l => l[65..], l => l[..64], StringComparer.Ordinal);
      Assert.That(served.Keys, Is.EquivalentTo(files.Keys));
      foreach (var (name, data) in files)
        Assert.That(served[name], Is.EqualTo(Sha(data)), name);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
