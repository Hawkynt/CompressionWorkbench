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
}
