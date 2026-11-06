using Compression.Tests.Support;
using FileSystem.Lfs;

namespace Compression.Tests.Lfs;

/// <summary>
/// Live oracle: in the NetBSD guest, <c>newfs_lfs</c> creates a fresh volume on a vnd, the
/// kernel fills it (including a rewrite, a deleted file and a hard link), <c>fsck_lfs</c>
/// writes a checkpoint, and NetBSD's manifest of the mounted volume must equal what our
/// reader returns. Opt-in through <c>CWB_NETBSD_LIVE</c> (see <see cref="NetBsdGuest"/>).
/// </summary>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
[Category("Slow")]
public sealed class LfsNetBsdTests {

  [TestCase("", TestName = "Lfs_NetBsd_DefaultGeometry_ReadsBack")]
  [TestCase("-b 4096 -f 4096", TestName = "Lfs_NetBsd_4KBlocksAndFragments_ReadsBack")]
  public void GivenKernelFilledVolume_WhenRead_ThenItMatchesNetBsdsMountedView(string newfsOptions) {
    NetBsdGuest.Require();
    var (output, disk) = NetBsdGuest.Run([
      "manifest() { (cd $1 && find . -mindepth 1 | sort | while read -r p; do q=${p#./}; m=$(stat -f %Lp \"$p\"); if [ -L \"$p\" ]; then echo \"L $m $q -> $(readlink \"$p\")\"; elif [ -d \"$p\" ]; then echo \"D $m $q\"; else echo \"F $m $(stat -f %z \"$p\") $(cksum -a sha256 -n \"$p\" | cut -d' ' -f1) $q\"; fi; done); }",
      "dd if=/dev/zero of=/tmp/l.img bs=1m count=16 2>/dev/null; vnconfig vnd0 /tmp/l.img; newfs_lfs -F " + newfsOptions + " /dev/rvnd0d > /dev/null 2>&1; echo NEWFS=$?",
      "mount -t lfs /dev/vnd0d /mnt && mkdir -p /mnt/x/y && jot 60000 > /mnt/x/y/n && echo a > /mnt/w && dd if=/dev/urandom of=/mnt/x/r bs=5000 count=3 2>/dev/null && sync && echo bb > /mnt/w && rm /mnt/x/r && ln /mnt/x/y/n /mnt/hl && ln -s x/y/n /mnt/sl && : > /mnt/e && sync && umount /mnt; echo FILLED=$?",
      "fsck_lfs -y -f /dev/rvnd0d > /dev/null 2>&1; mount -t lfs -o ro /dev/vnd0d /mnt && manifest /mnt > /tmp/m; umount /mnt; vnconfig -u vnd0",
      "dd if=/tmp/l.img of=/dev/rwd1d bs=1m 2>/dev/null; dd if=/tmp/m of=/dev/rwd1d bs=64k seek=320 conv=sync 2>/dev/null; echo DONE=1",
    ], dataMegabytes: 24);
    Assert.That(NetBsdGuest.Value(output, "NEWFS"), Is.Zero, output);
    Assert.That(NetBsdGuest.Value(output, "FILLED"), Is.Zero, output);
    NetBsdGuest.Value(output, "DONE");
    var manifest = System.Text.Encoding.ASCII.GetString(disk, 320 * 65536, 65536).TrimEnd('\0');
    var r = new LfsReader(new MemoryStream(disk, 0, 16 * 1024 * 1024, writable: false));
    LfsTests.AssertMatches(r, LfsTests.ParseManifest(manifest));
  }
}
