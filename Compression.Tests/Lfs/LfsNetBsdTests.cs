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

  private const string Manifest =
    "manifest() { (cd $1 && find . -mindepth 1 | sort | while read -r p; do q=${p#./}; m=$(stat -f %Lp \"$p\"); if [ -L \"$p\" ]; then echo \"L $m $q -> $(readlink \"$p\")\"; elif [ -d \"$p\" ]; then echo \"D $m $q\"; else echo \"F $m $(stat -f %z \"$p\") $(cksum -a sha256 -n \"$p\" | cut -d' ' -f1) $q\"; fi; done); }";

  /// <summary>
  /// Guest steps on our volume at the start of the data disk: <c>fsck_lfs -n</c> (every line it
  /// prints besides the phase banners, the totals and the roll-forward prompt it always puts and
  /// <c>-n</c> declines is a finding), a read-only mount's manifest,
  /// then optionally <paramref name="guestWrites"/> through the kernel and a second check.
  /// Findings and manifests come back in the data disk past the volume.
  /// </summary>
  private static (string Output, byte[] Disk) CheckInGuest(byte[] volume, string? guestWrites) {
    var mb = volume.Length >> 20;
    var check = "fsck_lfs -n -f /dev/rvnd0d > /tmp/f 2>&1; grep -v '^\\*\\*' /tmp/f | grep -v ' files, ' | grep -v '^ROLL FILESYSTEM FORWARD? no$' | grep -v '^ *$' > /tmp/bad; echo FINDINGS$1=$(wc -l < /tmp/bad | tr -d ' ')";
    var commands = new List<string> {
      Manifest,
      "chk() { " + check + "; }",
      $"dd if=/dev/rwd1d of=/tmp/o.img bs=1m count={mb} 2>/dev/null; vnconfig vnd0 /tmp/o.img; echo VND=$?",
      "chk A; cp /tmp/bad /tmp/badA; mount -t lfs -o ro /dev/vnd0d /mnt; echo MOUNTA=$?; manifest /mnt > /tmp/mA; umount /mnt",
    };
    if (guestWrites is not null)
      commands.Add("mount -t lfs /dev/vnd0d /mnt && (" + guestWrites + ") && sync && umount /mnt; echo WROTE=$?; " +
                   "chk B; cp /tmp/bad /tmp/badB; mount -t lfs -o ro /dev/vnd0d /mnt; manifest /mnt > /tmp/mB; umount /mnt");
    commands.Add("vnconfig -u vnd0; dd if=/tmp/o.img of=/dev/rwd1d bs=1m 2>/dev/null; " +
                 $"for x in badA mA badB mB; do touch /tmp/$x; done; n=0; for x in badA mA badB mB; do dd if=/tmp/$x of=/dev/rwd1d bs=64k seek=$(({mb} * 16 + n)) conv=sync 2>/dev/null; n=$((n + 1)); done; echo DONE=1");
    return NetBsdGuest.Run(commands, dataMegabytes: mb + 4, initialDisk: volume);
  }

  private static string Slot(byte[] disk, int volumeBytes, int slot) =>
    System.Text.Encoding.UTF8.GetString(disk, volumeBytes + slot * 65536, 65536).TrimEnd('\0');

  private static byte[] Bytes(int length, int seed) {
    var b = new byte[length];
    new Random(seed).NextBytes(b);
    return b;
  }

  /// <summary>
  /// Negative control for the check above: the same volume with one superblock total off by one
  /// must produce a finding, so a clean report means something.
  /// </summary>
  [Test]
  public void GivenMiscountedVolume_WhenChecked_ThenFsckReportsIt() {
    NetBsdGuest.Require();
    using var ms = new MemoryStream();
    var options = new Compression.Registry.FormatCreateOptions();
    options.FormatSpecific["ImageSize"] = "16 MB";
    new LfsFormatDescriptor().Create(ms, [Compression.Registry.ArchiveInputInfo.InMemory("a", Bytes(5000, 1))], options);
    var volume = ms.ToArray();
    foreach (var at in new[] { 8192, 3264 * 512 }) {
      var avail = BitConverter.ToInt32(volume, at + 44);
      BitConverter.GetBytes(avail + 1).CopyTo(volume, at + 44);
      uint sum = 0;
      for (var i = 0; i < 508; i += 2) sum ^= BitConverter.ToUInt16(volume, at + i);
      BitConverter.GetBytes(sum).CopyTo(volume, at + 508);
    }
    var (output, disk) = CheckInGuest(volume, null);
    Assert.That(NetBsdGuest.Value(output, "FINDINGSA"), Is.Positive, output);
    Assert.That(Slot(disk, volume.Length, 0), Does.Contain("AVAIL GIVEN AS"));
  }

  /// <summary>
  /// Our writer → NetBSD: fsck_lfs finds our volume clean, the kernel mounts it and sees our
  /// bytes; then the kernel writes into it (new file, rewrite, delete) and fsck_lfs is still
  /// clean; our reader reads the kernel's result; we edit that volume in place and NetBSD
  /// checks and mounts it once more.
  /// </summary>
  [TestCase(16, TestName = "Lfs_NetBsd_OurVolume_SmallGeometry_FsckCleanMountsAndRoundTrips")]
  [TestCase(96, TestName = "Lfs_NetBsd_OurVolume_DefaultGeometry_FsckCleanMountsAndRoundTrips")]
  public void GivenOurVolume_WhenNetBsdChecksMountsAndWrites_ThenAllAgree(int megabytes) {
    NetBsdGuest.Require();
    var d = new LfsFormatDescriptor();
    var inputs = new List<Compression.Registry.ArchiveInputInfo> {
      Compression.Registry.ArchiveInputInfo.InMemory("hello.txt", "hello lfs\n"u8.ToArray()),
      Compression.Registry.ArchiveInputInfo.InMemory("empty", []),
      Compression.Registry.ArchiveInputInfo.InMemory("d/frag", Bytes(1500, 1)),
      Compression.Registry.ArchiveInputInfo.InMemory("d/e/single", Bytes(12 * 8192 + 5, 2)),
      Compression.Registry.ArchiveInputInfo.InMemory("d/e/double", Bytes(300 * 1024, 3)),
      new("dir-only", "dir-only", IsDirectory: true),
    };
    inputs.AddRange(Enumerable.Range(0, 60).Select(i => Compression.Registry.ArchiveInputInfo.InMemory($"many/{i:D2}", Bytes(i * 97, 100 + i))));
    using var ms = new MemoryStream();
    var options = new Compression.Registry.FormatCreateOptions();
    options.FormatSpecific["ImageSize"] = $"{megabytes} MB";
    d.Create(ms, inputs, options);
    var volume = ms.ToArray();

    var (output, disk) = CheckInGuest(volume,
      "mkdir /mnt/g && jot 50000 > /mnt/g/n && echo again > /mnt/hello.txt && rm /mnt/many/07 && dd if=/dev/urandom of=/mnt/d/k bs=4000 count=40 2>/dev/null");
    Assert.Multiple(() => {
      Assert.That(NetBsdGuest.Value(output, "FINDINGSA"), Is.Zero, "fsck_lfs on our volume:\n" + Slot(disk, volume.Length, 0));
      Assert.That(NetBsdGuest.Value(output, "MOUNTA"), Is.Zero, output);
      Assert.That(NetBsdGuest.Value(output, "WROTE"), Is.Zero, output);
      Assert.That(NetBsdGuest.Value(output, "FINDINGSB"), Is.Zero, "fsck_lfs after the kernel wrote:\n" + Slot(disk, volume.Length, 2));
    });
    // The kernel's view of our volume equals ours.
    LfsTests.AssertMatches(new LfsReader(new MemoryStream(volume, writable: false)), LfsTests.ParseManifest(Slot(disk, volume.Length, 1)));
    // We read what the kernel wrote.
    var kernelWritten = disk.AsSpan(0, volume.Length).ToArray();
    LfsTests.AssertMatches(new LfsReader(new MemoryStream(kernelWritten, writable: false)), LfsTests.ParseManifest(Slot(disk, volume.Length, 3)));

    // We edit the kernel's volume in place; NetBSD checks and mounts it again.
    using var edit = new MemoryStream();
    edit.Write(kernelWritten);
    d.Add(edit, [Compression.Registry.ArchiveInputInfo.InMemory("g/ours", Bytes(40000, 9)), Compression.Registry.ArchiveInputInfo.InMemory("hello.txt", "third\n"u8.ToArray())]);
    d.Remove(edit, ["many/08", "dir-only"]);
    var edited = edit.ToArray();
    var (output2, disk2) = CheckInGuest(edited, null);
    Assert.That(NetBsdGuest.Value(output2, "FINDINGSA"), Is.Zero, "fsck_lfs on our edit of the kernel's volume:\n" + Slot(disk2, edited.Length, 0));
    Assert.That(NetBsdGuest.Value(output2, "MOUNTA"), Is.Zero, output2);
    LfsTests.AssertMatches(new LfsReader(new MemoryStream(edited, writable: false)), LfsTests.ParseManifest(Slot(disk2, edited.Length, 1)));
  }
}
