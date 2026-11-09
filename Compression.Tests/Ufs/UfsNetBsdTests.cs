#pragma warning disable CS1591
using System.Text.RegularExpressions;
using Compression.Registry;
using Compression.Tests.Support;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

/// <summary>
/// NetBSD 11.1, whose <c>fsck_ffs</c> and kernel read FFS in either byte order
/// (<c>FFS_EI</c>), judges the big-endian volumes this package writes: each one must pass
/// <c>fsck_ffs -n -f</c>, mount read-only, and give NetBSD's <c>cksum -a sha256</c> of
/// every file what the test put there. One guest boot checks every volume. Opt-in
/// through <c>CWB_NETBSD_LIVE</c> (see <see cref="NetBsdGuest"/>).
/// </summary>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
[Category("Slow")]
public sealed class UfsNetBsdTests {

  private static readonly string[] Bad = ["UNREF", "BAD", "INCORRECT", "WRONG", "SALVAGE", "FIX?", "MISSING", "DUP", "UNKNOWN", "CORRECTED"];

  private static Dictionary<string, string> Sha(IReadOnlyDictionary<string, byte[]> files)
    => files.ToDictionary(f => f.Key, f => UfsBigEndianTests.Sha(f.Value));

  /// <summary>Every volume to check, with the file hashes NetBSD must report.</summary>
  private static List<(string Name, byte[] Image, Dictionary<string, string> Files)> Volumes() {
    var list = new List<(string, byte[], Dictionary<string, string>)>();
    var d = new UfsFormatDescriptor();
    foreach (var version in new[] { "UFS1", "UFS2" }) {
      var be = Ufs2WriterTests.Options(version, bigEndian: true);
      list.Add(($"created-{version}", Ufs2WriterTests.Create(UfsBigEndianTests.Tree, be), Sha(UfsBigEndianTests.Tree)));

      using (var netbsd = UfsBigEndianTests.Vector($"netbsd-newfs-{version.ToLowerInvariant()}-be.img.gz")) {
        var expected = UfsBigEndianTests.EditNetBsdVolume(netbsd);
        list.Add(($"edited-newfs-{version}", netbsd.ToArray(), expected));
      }

      var (defrag, defragFiles) = Ufs2MaintenanceTests.Fragmented(be);
      d.Defragment(defrag);
      list.Add(($"defragmented-{version}", defrag.ToArray(), Sha(defragFiles)));

      var (packed, packedFiles) = Ufs2MaintenanceTests.Fragmented(be);
      d.Defragment(packed, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });
      list.Add(($"packed-at-end-{version}", packed.ToArray(), Sha(packedFiles)));

      var (moved, movedFiles, _) = Ufs2MaintenanceTests.Moved(zeroSource: true, be);
      d.WipeUnusedSpace(moved);
      list.Add(($"moved-and-wiped-{version}", moved.ToArray(), Sha(movedFiles)));
    }
    return list;
  }

  [Test]
  public void GivenBigEndianVolumesThisPackageWrote_WhenNetBsdChecksAndMountsThem_ThenFsckIsCleanAndEveryFileHashes() {
    NetBsdGuest.Require();
    var volumes = Volumes();
    var slot = (int)((volumes.Max(v => v.Image.LongLength) + (1 << 20) - 1) >> 20);
    var disk = new byte[volumes.Count * (long)slot << 20];
    for (var i = 0; i < volumes.Count; ++i) volumes[i].Image.CopyTo(disk, (long)i * slot << 20);

    var commands = new List<string> {
      "check() { dd if=/dev/rwd1d of=/tmp/v.img bs=1m skip=$2 count=$3 2>/dev/null; echo \"BEGIN $1\"; fsck_ffs -n -f /tmp/v.img 2>&1; echo \"FSCK $1 $?\"; " +
      "vnconfig vnd0 /tmp/v.img && mount -r /dev/vnd0d /mnt && echo \"MOUNTED $1\" && (cd /mnt && find . -type f | sort | while read -r f; do echo \"H $1 ${f#./} $(cksum -a sha256 -n \"$f\" | cut -d' ' -f1)\"; done); " +
      "umount /mnt; vnconfig -u vnd0; echo \"END $1\"; }",
    };
    for (var i = 0; i < volumes.Count; ++i) {
      var mb = (int)((volumes[i].Image.LongLength + (1 << 20) - 1) >> 20);
      commands.Add($"check {i} {i * slot} {mb}");
    }
    var (output, _) = NetBsdGuest.Run(commands, volumes.Count * slot, disk);
    TestContext.Out.WriteLine(output);

    Assert.Multiple(() => {
      for (var i = 0; i < volumes.Count; ++i) {
        var (name, _, files) = volumes[i];
        var section = Regex.Match(output, $@"(?s)BEGIN {i}\r?\n(.*?)END {i}");
        Assert.That(section.Success, Is.True, $"{name}: the guest did not check it:\n{output}");
        if (!section.Success) continue;
        var text = section.Groups[1].Value;
        var fsck = text[..Math.Max(0, text.IndexOf($"FSCK {i} ", StringComparison.Ordinal))];
        Assert.That(text, Does.Contain($"FSCK {i} 0"), $"{name}: fsck_ffs exit status\n{text}");
        foreach (var bad in Bad) Assert.That(fsck, Does.Not.Contain(bad), $"{name}: fsck_ffs\n{text}");
        Assert.That(text, Does.Contain($"MOUNTED {i}"), $"{name}: mount\n{text}");
        var seen = Regex.Matches(text, $@"(?m)^H {i} (\S+) ([0-9a-f]{{64}})\r?$")
          .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        Assert.That(seen, Is.EquivalentTo(files), $"{name}: NetBSD's view of the files\n{text}");
      }
    });
  }
}
