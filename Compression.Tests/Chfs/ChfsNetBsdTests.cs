using System.Security.Cryptography;
using System.Text;
using Compression.Tests.Support;
using FileSystem.Chfs;

namespace Compression.Tests.Chfs;

/// <summary>
/// Live oracle: NetBSD's <c>makefs -t chfs</c> builds images (NAND default geometry and a NOR
/// geometry) in the guest from a fresh tree; our reader must return exactly the files NetBSD
/// hashed. Opt-in through <c>CWB_NETBSD_LIVE</c> (see <see cref="NetBsdGuest"/>).
/// </summary>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
[Category("Slow")]
public sealed class ChfsNetBsdTests {

  private static readonly string[] Tree = [
    "rm -rf /tmp/t; mkdir -p /tmp/t/a/b /tmp/t/e",
    "jot 30000 > /tmp/t/a/numbers; printf 'x' > /tmp/t/a/b/one; : > /tmp/t/empty; dd if=/dev/urandom of=/tmp/t/r bs=7000 count=1 2>/dev/null; ln -s a/numbers /tmp/t/link",
    "chmod 0640 /tmp/t/a/numbers; chmod 0700 /tmp/t/a",
  ];

  [TestCase("", TestName = "Chfs_NetBsdMakefs_NandDefault_ReadsBack")]
  [TestCase("-o mediatype=0,pagesize=1024,eraseblock=8192", TestName = "Chfs_NetBsdMakefs_NorSmallBlocks_ReadsBack")]
  public void GivenFreshMakefsImage_WhenRead_ThenEveryFileMatchesNetBsdsHash(string options) {
    NetBsdGuest.Require();
    var (output, disk) = NetBsdGuest.Run([
      .. Tree,
      $"makefs -t chfs {options} /tmp/c.img /tmp/t > /dev/null && echo SIZE=$(stat -f %z /tmp/c.img)",
      "dd if=/tmp/c.img of=/dev/rwd1d bs=64k conv=sync 2>/dev/null",
      "(cd /tmp/t && find . -type f | sort | while read f; do echo \"$(cksum -a sha256 -n \"$f\" | cut -d' ' -f1) ${f#./}\"; done) > /tmp/m; dd if=/tmp/m of=/dev/rwd1d bs=64k seek=128 conv=sync 2>/dev/null; echo DONE=1",
    ], dataMegabytes: 16);
    NetBsdGuest.Value(output, "DONE");
    var size = (int)NetBsdGuest.Value(output, "SIZE");
    var manifest = Encoding.ASCII.GetString(disk, 128 * 65536, 65536).TrimEnd('\0')
      .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ', 2)).ToDictionary(p => p[1], p => p[0]);

    var r = new ChfsReader(new MemoryStream(disk, 0, size, writable: false));
    var files = r.Entries.Where(e => e.IsFile).ToDictionary(e => e.Path, e => Convert.ToHexStringLower(SHA256.HashData(e.Data)));
    Assert.That(files, Is.EquivalentTo(manifest));
    Assert.That(Encoding.ASCII.GetString(r.Entries.Single(e => e.Path == "link").Data), Is.EqualTo("a/numbers"));
  }

  // ---- the writer against NetBSD 9.5's kernel (nandemulator: NAND, 2 KiB pages, 128 KiB eraseblocks, 32 MiB)

  private const int FlashAt = 32, ListingAt = 100;   // MiB offsets on the data disk

  /// <summary>
  /// Loads <paramref name="image"/> onto the emulated flash, mounts it with the kernel, lists every
  /// entry the kernel sees (type, permissions, owner, group, mtime, size, SHA-256 or link target),
  /// lets the kernel write (<paramref name="kernelWrites"/>), unmounts and dumps the whole flash.
  /// </summary>
  private static (string Output, Dictionary<string, string> Listing, byte[] Flash) MountInKernel(byte[] image, params string[] kernelWrites) {
    var disk = new byte[image.Length];
    image.CopyTo(disk, 0);
    var (output, after) = NetBsdGuest.Run([
      "modload nandemulator ; modload chfs",
      $"dd if=/dev/rwd1d of=/dev/rflash0 bs=128k count={image.Length / 131072} 2>/dev/null; echo LOADED=$?",
      "mount_chfs /dev/flash0 /mnt; echo MOUNTED=$?",
      "(cd /mnt && find . ! -name . | sort | while read p; do if [ -f \"$p\" ] && [ ! -h \"$p\" ]; then h=$(cksum -a sha256 < \"$p\"); elif [ -h \"$p\" ]; then h=\"-> $(readlink \"$p\")\"; else h=dir; fi; echo \"${p#./}|$(stat -f '%HT %Lp %u %g %m' \"$p\")|$h\"; done) > /tmp/l; wc -l < /tmp/l",
      $"dd if=/tmp/l of=/dev/rwd1d bs=1m seek={ListingAt} conv=sync 2>/dev/null; echo LISTED=$?",
      .. kernelWrites,
      "umount /mnt; echo UNMOUNTED=$?",
      $"dd if=/dev/rflash0 of=/dev/rwd1d bs=1m seek={FlashAt} 2>/dev/null; echo DUMPED=$?",
    ], dataMegabytes: ListingAt + 4, initialDisk: disk, release9: true);
    foreach (var step in new[] { "LOADED", "MOUNTED", "LISTED", "UNMOUNTED", "DUMPED" })
      Assert.That(NetBsdGuest.Value(output, step), Is.Zero, $"{step}:\n{output}");
    var listing = Encoding.UTF8.GetString(after, ListingAt << 20, 4 << 20).TrimEnd('\0')
      .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('|', 2)).ToDictionary(p => p[0], p => p[1]);
    return (output, listing, after.AsSpan(FlashAt << 20, 32 << 20).ToArray());
  }

  /// <summary>What the kernel's listing line should say for an entry of the writer's tree.</summary>
  private static string Expect(ChfsWriteNode n) {
    var type = (n.Mode & 0xF000) switch { 0x4000 => "Directory", 0xA000 => "Symbolic Link", _ => "Regular File" };
    var tail = (n.Mode & 0xF000) switch {
      0x4000 => "dir",
      0xA000 => "-> " + Encoding.UTF8.GetString(n.Data),
      _ => Convert.ToHexStringLower(SHA256.HashData(n.Data)),
    };
    return $"{type} {Convert.ToString(n.Mode & 0xFFF, 8)} {n.Uid} {n.Gid} {n.MTime}|{tail}";
  }

  private static Dictionary<string, string> Flatten(ChfsWriteNode root) {
    var map = new Dictionary<string, string>();
    void Walk(ChfsWriteNode dir, string prefix) {
      foreach (var c in dir.Children) {
        var path = prefix + c.Name;
        map[path] = Expect(c);
        if (c.IsDirectory) Walk(c, path + "/");
      }
    }
    Walk(root, "");
    return map;
  }

  private static byte[] Random(int size, int seed) {
    var b = new byte[size];
    new Random(seed).NextBytes(b);
    return b;
  }

  [Test]
  public void GivenWriterImage_WhenMountedByNetBsd95Kernel_ThenEveryEntryReadsBackAndKernelWritesReadBackInOurReader() {
    NetBsdGuest.Require(release9: true);
    const uint t = 1_700_000_000;
    ChfsWriteNode F(string name, byte[] data, uint mode = 0x81A4) => new(name, mode) { Data = data, Uid = 1000, Gid = 100, ATime = t, MTime = t + 1, CTime = t + 2 };
    ChfsWriteNode D(string name, uint perm, params ChfsWriteNode[] children) => new(name, 0x4000 | perm) { Children = children, ATime = t, MTime = t + 3, CTime = t };
    var root = D("", 0x1ED,
      F("empty", []),
      F("one", "x"u8.ToArray(), 0x8180),
      F("page-1", Random(2047, 1)), F("page", Random(2048, 2)), F("page+1", Random(2049, 3)),
      F("big.bin", Random(300_000, 4)),   // runs across three eraseblocks
      new ChfsWriteNode("link", 0xA1FF) { Data = "deep/a/b/leaf.txt"u8.ToArray(), MTime = t },
      D("deep", 0x1C0, D("a", 0x1ED, D("b", 0x1ED, F("leaf.txt", "leaf\n"u8.ToArray())))),
      D("empty-dir", 0x1ED),
      F(new string('n', 255), "long name"u8.ToArray()),
      D("many", 0x1ED, [.. Enumerable.Range(0, 120).Select(i => F($"f{i:D3}", Random(i * 37, i + 10)))]));
    using var ms = new MemoryStream();
    ChfsWriter.Write(ms, root);
    var image = ms.ToArray();

    var (output, listing, flash) = MountInKernel(image,
      "echo kernel-wrote > /mnt/deep/k.txt; mkdir /mnt/kdir; cp /mnt/big.bin /mnt/kdir/big2; echo KWRITE=$?");
    Assert.That(NetBsdGuest.Value(output, "KWRITE"), Is.Zero, output);
    Assert.That(listing, Is.EquivalentTo(Flatten(root)), output);

    // what the kernel wrote, our reader reads back; what we wrote is still there unchanged
    var r = new ChfsReader(new MemoryStream(flash, writable: false));
    var files = r.Entries.Where(e => e.IsFile).ToDictionary(e => e.Path, e => e.Data);
    Assert.That(Encoding.ASCII.GetString(files["deep/k.txt"]), Is.EqualTo("kernel-wrote\n"));
    Assert.That(files["kdir/big2"], Is.EqualTo(Random(300_000, 4)));
    Assert.That(files["big.bin"], Is.EqualTo(Random(300_000, 4)));
    Assert.That(files["page+1"], Is.EqualTo(Random(2049, 3)));
  }

  [Test]
  public void GivenMakefsImageModifiedByUs_WhenMountedByNetBsd95Kernel_ThenTheEditAndEveryKeptFileRead() {
    NetBsdGuest.Require(release9: true);
    var original = ChfsWriterTests.Image("chfs-nand");
    using var img = new MemoryStream();
    img.Write(original);
    var descriptor = new ChfsFormatDescriptor();
    var at = new Compression.Registry.ArchiveEntryMetadata(LastWriteTimeUtc: DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    descriptor.Add(img, [Compression.Registry.ArchiveInputInfo.InMemory("etc/added.txt", "added by the writer\n"u8, at)]);
    descriptor.Remove(img, ["bin/random.bin"]);
    var before = new ChfsReader(new MemoryStream(original, writable: false));
    var after = new ChfsReader(new MemoryStream(img.ToArray(), writable: false));

    var (output, listing, _) = MountInKernel(img.ToArray());
    var expected = after.Entries.ToDictionary(e => e.Path, e => e.IsDirectory ? "dir" : e.IsSymlink ? "-> " + Encoding.UTF8.GetString(e.Data) : Convert.ToHexStringLower(SHA256.HashData(e.Data)));
    Assert.That(listing.ToDictionary(p => p.Key, p => p.Value.Split('|')[1]), Is.EquivalentTo(expected), output);
    Assert.That(expected, Does.Not.ContainKey("bin/random.bin"));
    Assert.That(expected.Keys, Is.SupersetOf(before.Entries.Select(e => e.Path).Where(p => p != "bin/random.bin")));
  }
}
